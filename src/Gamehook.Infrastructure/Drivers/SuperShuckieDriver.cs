using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure.Drivers;

/// <summary>
/// Reads live memory from a running Super Shuckie instance via its built-in Poke-A-Byte
/// integration: a small UDP control protocol negotiates which addresses to expose, then the
/// emulator continuously refreshes those bytes into a shared memory block (once per frame,
/// subject to frame skipping) that this driver reads directly, with no per-poll round trip.
/// </summary>
public sealed class SuperShuckieDriver : IDriver, IDisposable
{
    public const string Name = "SuperShuckie";

    private const int DefaultPort = 55356;
    private const int MaxReadBlocks = 128;
    private const int ReadBlockSize = 12;
    private const int HeaderSize = 32;
    private const int SetupPacketSize = HeaderSize + ReadBlockSize * MaxReadBlocks;
    private const int AckTimeoutMilliseconds = 2000;
    private const int AckSendAttempts = 3;

    private const string SharedMemoryFileName = "EDPS_MemoryData.bin";
    private const string LinuxSharedMemoryPath = "/dev/shm/" + SharedMemoryFileName;
    private const string MacSharedMemoryPath = "/tmp/" + SharedMemoryFileName;

    private enum Instruction : byte
    {
        NoOp = 0,
        Ping = 1,
        Setup = 2,
        Write = 3,
        Freeze = 4,
        Unfreeze = 5,
        Close = 0xFF
    }

    private readonly record struct BlockKey(string RegionId, uint Address, int Length);

    private readonly UdpClient client;
    private readonly SemaphoreSlim configureLock = new(1, 1);

    private MemoryMappedFile? mappedFile;
    private MemoryMappedViewAccessor? view;
    private IReadOnlyList<BlockKey>? configuredBlocks;
    private IReadOnlyDictionary<BlockKey, int>? configuredOffsets;

    public SuperShuckieDriver(string? sourcePath)
    {
        var (host, port) = ParseEndpoint(sourcePath);
        client = new UdpClient();
        client.Client.ReceiveTimeout = AckTimeoutMilliseconds;
        client.Connect(host, port);
    }

    public async Task<IDriver.Response> Read(IDriver.Request request)
    {
        await readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await ReadCore(request).ConfigureAwait(false);
        }
        finally { readGate.Release(); }
    }

    private readonly SemaphoreSlim readGate = new(1, 1);
    private bool disposed;

    private async Task<IDriver.Response> ReadCore(IDriver.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.System);
        ArgumentNullException.ThrowIfNull(request.Segments);

        var addressable = new List<(IDriver.MemorySegmentRequest Segment, uint Address)>(request.Segments.Count);
        foreach (var requestSegment in request.Segments)
        {
            if (requestSegment.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), requestSegment.Length, "Segment length cannot be negative.");
            }

            // regions the driver can't address at all (no known base address for this system) are
            // simply left out of the response - Property.Refresh reports those as a null value
            // rather than failing the whole read.
            if (request.System.RegionDefinitions.FirstOrDefault(region => region.Id == requestSegment.RegionId)?.BusAddress is not { } baseAddress)
            {
                continue;
            }

            if (requestSegment.StartingAddress > uint.MaxValue - baseAddress
                || (ulong)requestSegment.Length > (ulong)uint.MaxValue - baseAddress - requestSegment.StartingAddress + 1)
                throw new ArgumentOutOfRangeException(nameof(request), "Requested segment exceeds the address space.");
            addressable.Add((requestSegment, baseAddress + (uint)requestSegment.StartingAddress));
        }

        var offsets = await EnsureConfigured(addressable).ConfigureAwait(false);

        var segments = new List<IDriver.MemorySegmentSnapshot>(addressable.Count);
        foreach (var (segment, address) in addressable)
        {
            if (segment.Length == 0)
            {
                segments.Add(new IDriver.MemorySegmentSnapshot(segment.RegionId, segment.StartingAddress, ReadOnlyMemory<byte>.Empty));
                continue;
            }

            if (!offsets.TryGetValue(new BlockKey(segment.RegionId, address, segment.Length), out var offset))
            {
                continue;
            }

            var bytes = new byte[segment.Length];
            lock (configureLock)
            {
                view!.ReadArray(offset, bytes, 0, segment.Length);
            }

            segments.Add(new IDriver.MemorySegmentSnapshot(segment.RegionId, segment.StartingAddress, bytes));
        }

        return new IDriver.Response(DateTimeOffset.UtcNow, segments);
    }

    public async Task Write(IDriver.WriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.System);
        ArgumentNullException.ThrowIfNull(request.Segments);

        foreach (var segment in request.Segments)
        {
            if (segment.Bytes.Length == 0) continue;

            if (request.System.RegionDefinitions.FirstOrDefault(region => region.Id == segment.RegionId)?.BusAddress is not { } baseAddress)
            {
                throw new NotSupportedException($"No known base address for region '{segment.RegionId}' on {request.System.Id}.");
            }

            if (segment.StartingAddress > uint.MaxValue - baseAddress
                || (ulong)segment.Bytes.Length > (ulong)uint.MaxValue - baseAddress - segment.StartingAddress + 1)
                throw new ArgumentOutOfRangeException(nameof(request), "Requested segment exceeds the address space.");

            var address = baseAddress + (uint)segment.StartingAddress;
            await SendWriteAndAwaitAck(address, segment.Bytes).ConfigureAwait(false);
        }
    }

    // Layout: header, then a 4-byte game address, a 4-byte payload length, then the raw payload
    // bytes - same header shape as Setup, just a single address/length/payload block instead of a
    // block table.
    private async Task SendWriteAndAwaitAck(uint address, ReadOnlyMemory<byte> payload)
    {
        var packet = new byte[HeaderSize + 8 + payload.Length];
        WriteHeader(packet, Instruction.Write, isResponse: false);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), address);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)payload.Length);
        payload.Span.CopyTo(packet.AsSpan(HeaderSize));

        await configureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < AckSendAttempts; attempt++)
            {
                try
                {
                    await client.SendAsync(packet, packet.Length).ConfigureAwait(false);
                    if (await TryReceiveAck(Instruction.Write).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    lastError = ex;
                }
            }

            throw new TimeoutException($"Super Shuckie did not acknowledge the write to 0x{address:x}.", lastError);
        }
        finally
        {
            configureLock.Release();
        }
    }

    // Poke-A-Byte's shared memory layout is fixed once negotiated via Setup, so the block list only
    // needs to be (re-)sent when the set of requested addresses actually changes - most polls reuse
    // the same regions every time and can skip straight to reading the shared memory block.
    private async Task<IReadOnlyDictionary<BlockKey, int>> EnsureConfigured(
        List<(IDriver.MemorySegmentRequest Segment, uint Address)> addressable)
    {
        var candidateBlocks = addressable
            .Where(entry => entry.Segment.Length > 0)
            .Select(entry => new BlockKey(entry.Segment.RegionId, entry.Address, entry.Segment.Length))
            .ToList();

        await configureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (configuredBlocks is not null && configuredOffsets is not null && configuredBlocks.SequenceEqual(candidateBlocks))
            {
                return configuredOffsets;
            }

            if (candidateBlocks.Count > MaxReadBlocks)
            {
                throw new NotSupportedException(
                    $"Super Shuckie's Poke-A-Byte protocol supports at most {MaxReadBlocks} memory regions per read; {candidateBlocks.Count} were requested.");
            }

            var offsets = new Dictionary<BlockKey, int>(candidateBlocks.Count);
            var protocolBlocks = new List<(uint FileOffset, uint GameAddress, uint Length)>(candidateBlocks.Count);
            var runningOffset = 0;
            foreach (var block in candidateBlocks)
            {
                offsets[block] = runningOffset;
                protocolBlocks.Add(((uint)runningOffset, block.Address, (uint)block.Length));
                runningOffset = checked(runningOffset + block.Length);
            }

            view?.Dispose();
            mappedFile?.Dispose();
            view = null;
            mappedFile = null;
            configuredBlocks = null;
            configuredOffsets = null;

            // nothing addressable was requested (e.g. every region is unmapped for this system) -
            // no point negotiating a Setup with the emulator at all.
            if (candidateBlocks.Count > 0)
            {
                await SendSetupAndAwaitAck(protocolBlocks).ConfigureAwait(false);
                (mappedFile, view) = OpenSharedMemory(runningOffset);
            }

            configuredBlocks = candidateBlocks;
            configuredOffsets = offsets;
            return offsets;
        }
        finally
        {
            configureLock.Release();
        }
    }

    private async Task SendSetupAndAwaitAck(IReadOnlyList<(uint FileOffset, uint GameAddress, uint Length)> blocks)
    {
        var packet = new byte[SetupPacketSize];
        WriteHeader(packet, Instruction.Setup, isResponse: false);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)blocks.Count);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), 0); // frame_skip: never skip

        for (var i = 0; i < blocks.Count; i++)
        {
            var offset = HeaderSize + i * ReadBlockSize;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), blocks[i].FileOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset + 4, 4), blocks[i].GameAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset + 8, 4), blocks[i].Length);
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < AckSendAttempts; attempt++)
        {
            try
            {
                await client.SendAsync(packet, packet.Length).ConfigureAwait(false);
                if (await TryReceiveAck(Instruction.Setup).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                lastError = ex;
            }
        }

        throw new TimeoutException("Super Shuckie did not acknowledge the Poke-A-Byte setup request.", lastError);
    }

    private async Task<bool> TryReceiveAck(Instruction expected)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AckTimeoutMilliseconds);
            var result = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return result.Buffer.Length >= HeaderSize
                && result.Buffer[0] == 1
                && result.Buffer[5] == 1
                && result.Buffer[4] == (byte)expected;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            return false;
        }
    }

    private static void WriteHeader(Span<byte> packet, Instruction instruction, bool isResponse)
    {
        packet[0] = 1; // protocol version
        packet[4] = (byte)instruction;
        packet[5] = (byte)(isResponse ? 1 : 0);
    }

    // The emulator creates this the moment it accepts our Setup request (before it acks it), so by
    // the time an ack has arrived the shared memory is guaranteed to exist and be sized correctly.
    private static (MemoryMappedFile, MemoryMappedViewAccessor) OpenSharedMemory(int length)
    {
        var mappedFile = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? MemoryMappedFile.OpenExisting(SharedMemoryFileName, MemoryMappedFileRights.Read)
            : OpenUnixSharedMemoryFile();

        try
        {
            var view = mappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            return (mappedFile, view);
        }
        catch
        {
            mappedFile.Dispose();
            throw;
        }
    }

    private static MemoryMappedFile OpenUnixSharedMemoryFile()
    {
        var path = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacSharedMemoryPath : LinuxSharedMemoryPath;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
    }

    private static (string Host, int Port) ParseEndpoint(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return ("127.0.0.1", DefaultPort);
        }

        var separatorIndex = sourcePath.LastIndexOf(':');
        if (separatorIndex < 0)
        {
            return (sourcePath, DefaultPort);
        }

        var host = sourcePath[..separatorIndex];
        var portText = sourcePath[(separatorIndex + 1)..];
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException($"Invalid Super Shuckie endpoint '{sourcePath}'.", nameof(sourcePath));
        }

        return (host, port);
    }

    public void Dispose()
    {
        readGate.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            DisposeCore();
        }
        finally { readGate.Release(); }
    }

    private void DisposeCore()
    {
        var closePacket = new byte[HeaderSize];
        WriteHeader(closePacket, Instruction.Close, isResponse: false);
        try
        {
            client.Send(closePacket, closePacket.Length);
        }
        catch (SocketException)
        {
            // best-effort: the emulator may already be gone.
        }

        view?.Dispose();
        mappedFile?.Dispose();
        client.Dispose();
        configureLock.Dispose();
    }
}
