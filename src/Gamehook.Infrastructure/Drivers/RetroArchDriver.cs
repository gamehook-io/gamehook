using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure.Drivers;

/// <summary>
/// Reads live memory from a running RetroArch instance over its UDP network command
/// interface (READ_CORE_MEMORY), using the same CPU-bus addresses as cheats/RetroAchievements.
/// </summary>
public sealed class RetroArchDriver : IDriver, IDisposable
{
    public const string Name = "RetroArch";

    private const int DefaultPort = 55355;
    private const int ReceiveTimeoutMilliseconds = 2000;

    // An 8KB binary read becomes roughly 24KB of ASCII hex, well below UDP's payload limit.
    private const int MaximumReadChunkLength = 8192;

    private readonly UdpClient client;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private bool disposed;

    // RetroArch echoes the requested address back in its reply, so in-flight requests are
    // demultiplexed by address rather than serialized one-at-a-time. This lets reads for
    // different regions (e.g. WRAM and VRAM) run concurrently over the single UDP socket
    // instead of paying a full round-trip per region, per poll.
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ReadOnlyMemory<byte>?>> pendingRequests = new();
    private readonly CancellationTokenSource disposeCts = new();
    private readonly Task receiveLoopTask;

    public RetroArchDriver(string? sourcePath)
    {
        var (host, port) = ParseEndpoint(sourcePath);
        client = new UdpClient();
        client.Connect(host, port);
        receiveLoopTask = ReceiveLoopAsync();
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

    public async Task Write(IDriver.WriteRequest request)
    {
        await readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await WriteCore(request).ConfigureAwait(false);
        }
        finally { readGate.Release(); }
    }

    private async Task WriteCore(IDriver.WriteRequest request)
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
            var command = Encoding.ASCII.GetBytes(
                $"WRITE_CORE_MEMORY {address:x} {string.Join(' ', segment.Bytes.ToArray().Select(b => b.ToString("x2")))}\n");
            await client.SendAsync(command, command.Length).ConfigureAwait(false);
        }
    }

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

        // each region's read (and any boundary-crossing continuation within it) is independent of
        // every other region's, so they run concurrently rather than waiting on each other in turn.
        var results = await Task.WhenAll(addressable.Select(async entry =>
        {
            var bytes = entry.Segment.Length == 0
                ? ReadOnlyMemory<byte>.Empty
                : await ReadCoreMemory(entry.Address, entry.Segment.Length).ConfigureAwait(false);
            return (entry.Segment, Bytes: bytes);
        })).ConfigureAwait(false);

        var segments = new List<IDriver.MemorySegmentSnapshot>(results.Length);
        foreach (var (segment, bytes) in results)
        {
            // RetroArch reported it can't service this specific address (e.g. no descriptor covers it
            // for the loaded core/content) - same treatment, omit the segment instead of failing.
            if (bytes is not { } value)
            {
                continue;
            }

            segments.Add(new IDriver.MemorySegmentSnapshot(segment.RegionId, segment.StartingAddress, value));
        }

        return new IDriver.Response(DateTimeOffset.UtcNow, segments);
    }

    // Start every fixed-size chunk at once. A core can still truncate a read at a memory-map
    // descriptor boundary; only the missing tail then needs a dependent continuation request.
    private async Task<ReadOnlyMemory<byte>?> ReadCoreMemory(uint address, int length)
    {
        var bytes = new byte[length];
        var chunks = new List<(int Offset, int Length)>();
        for (var offset = 0; offset < length; offset += MaximumReadChunkLength)
        {
            chunks.Add((offset, Math.Min(length - offset, MaximumReadChunkLength)));
        }

        var replies = await Task.WhenAll(chunks.Select(chunk =>
            ReadCoreMemoryChunk(address + (uint)chunk.Offset, chunk.Length))).ConfigureAwait(false);

        for (var index = 0; index < chunks.Count; index++)
        {
            var (offset, requestedLength) = chunks[index];
            var chunk = replies[index];
            if (chunk is null)
            {
                return offset == 0 ? null : throw new InvalidDataException(
                    $"RetroArch stopped responding with data partway through a read at 0x{address:x} (got {offset} of {length} byte(s)).");
            }

            chunk.Value.Span.CopyTo(bytes.AsSpan(offset));
            var receivedLength = chunk.Value.Length;
            while (receivedLength < requestedLength)
            {
                var continuation = await ReadCoreMemoryChunk(
                    address + (uint)(offset + receivedLength), requestedLength - receivedLength).ConfigureAwait(false);
                if (continuation is null)
                {
                    throw new InvalidDataException(
                        $"RetroArch stopped responding with data partway through a read at 0x{address:x} (got {offset + receivedLength} of {length} byte(s)).");
                }

                continuation.Value.Span.CopyTo(bytes.AsSpan(offset + receivedLength));
                receivedLength += continuation.Value.Length;
            }
        }

        return bytes;
    }

    private async Task<ReadOnlyMemory<byte>?> ReadCoreMemoryChunk(uint address, int length)
    {
        var command = Encoding.ASCII.GetBytes($"READ_CORE_MEMORY {address:x} {length}\n");
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!pendingRequests.TryAdd(address, tcs))
        {
            throw new InvalidOperationException($"A read for address 0x{address:x} is already in flight.");
        }

        try
        {
            await client.SendAsync(command, command.Length).ConfigureAwait(false);
            var result = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ReceiveTimeoutMilliseconds)).ConfigureAwait(false);
            if (result is { } bytes && (bytes.Length == 0 || bytes.Length > length))
                throw new InvalidDataException("RetroArch returned an invalid byte count.");
            return result;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw new TimeoutException($"RetroArch did not respond to READ_CORE_MEMORY for address 0x{address:x}.", ex);
        }
        finally
        {
            pendingRequests.TryRemove(address, out _);
        }
    }

    // Single reader for the socket: incoming replies are matched back to their waiting request by
    // the address RetroArch echoes in the response, then handed off, so concurrent ReadCoreMemory
    // calls never race each other for the next datagram off the wire.
    private async Task ReceiveLoopAsync()
    {
        while (!disposeCts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(disposeCts.Token).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Connected UDP reports an unreachable RetroArch endpoint here (e.g. no content
                // loaded yet). This is transient, not fatal - RetroArch starts responding again
                // once a game loads, so the loop must keep listening rather than exit, or Mapper's
                // retry-forever recovery (see Mapper.ReadAsync) would never get a reply again.
                // Preserve the failure for Mapper so an already-loaded workspace can show its
                // recoverable red connection state instead of being torn down as a generic read error.
                foreach (var pendingAddress in pendingRequests.Keys)
                {
                    if (pendingRequests.TryRemove(pendingAddress, out var pending))
                    {
                        pending.TrySetException(ex);
                    }
                }

                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            if (!TryParseAddress(result.Buffer, out var address, out var payload) || !pendingRequests.TryRemove(address, out var tcs))
            {
                continue;
            }

            try
            {
                tcs.TrySetResult(ParsePayload(payload, result.Buffer, address));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        // the client was disposed (or the loop otherwise stopped) with requests still waiting -
        // fail them instead of leaving their callers hanging until each one times out on its own.
        foreach (var address in pendingRequests.Keys)
        {
            if (pendingRequests.TryRemove(address, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    private static ReadOnlySpan<byte> CommandPrefix => "READ_CORE_MEMORY "u8;

    // A big region reply (e.g. an 8KB WRAM read) is thousands of ASCII hex byte tokens. Decoding the
    // whole datagram to a string and Split()-ing it allocated one substring per token, every poll tick.
    // Parsing straight off the UDP receive buffer with Utf8Parser avoids all of that - no string, no
    // per-token allocation - and is the dominant cost at high poll rates, dwarfing the network round-trip.
    private static bool TryParseAddress(byte[] buffer, out uint address, out ReadOnlySpan<byte> payload)
    {
        var span = TrimAscii(buffer);
        if (span.Length <= CommandPrefix.Length || !span[..CommandPrefix.Length].SequenceEqual(CommandPrefix))
        {
            address = 0;
            payload = default;
            return false;
        }

        span = TrimLeadingSpaces(span[CommandPrefix.Length..]);
        if (!Utf8Parser.TryParse(span, out address, out var consumed, 'x'))
        {
            payload = default;
            return false;
        }

        payload = TrimLeadingSpaces(span[consumed..]);
        return true;
    }

    private static ReadOnlyMemory<byte>? ParsePayload(ReadOnlySpan<byte> payload, byte[] rawBufferForErrors, uint address)
    {
        if (payload.Length == 0)
        {
            throw new InvalidDataException($"RetroArch returned no data for the read at 0x{address:x}.");
        }

        var firstTokenEnd = payload.IndexOf((byte)' ');
        var firstToken = firstTokenEnd < 0 ? payload : payload[..firstTokenEnd];
        if (firstToken.SequenceEqual("-1"u8))
        {
            return null;
        }

        // upper bound: every byte token is at least "X " (2 chars), so this never undersizes the buffer.
        var scratch = new byte[payload.Length / 2 + 1];
        var count = 0;
        var remaining = payload;
        while (remaining.Length > 0)
        {
            if (!Utf8Parser.TryParse(remaining, out byte value, out var consumed, 'x'))
            {
                throw new InvalidDataException(
                    $"Unexpected response from RetroArch: '{Encoding.ASCII.GetString(rawBufferForErrors).Trim()}'.");
            }

            scratch[count++] = value;
            remaining = TrimLeadingSpaces(remaining[consumed..]);
        }

        return new ReadOnlyMemory<byte>(scratch, 0, count);
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhiteSpace(value[start])) start++;
        var end = value.Length;
        while (end > start && IsAsciiWhiteSpace(value[end - 1])) end--;
        return value[start..end];
    }

    private static ReadOnlySpan<byte> TrimLeadingSpaces(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] == (byte)' ') start++;
        return value[start..];
    }

    private static bool IsAsciiWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

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
            throw new ArgumentException($"Invalid RetroArch endpoint '{sourcePath}'.", nameof(sourcePath));
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
            disposeCts.Cancel();
            client.Dispose();
            receiveLoopTask.GetAwaiter().GetResult();
            disposeCts.Dispose();
        }
        finally { readGate.Release(); }
    }
}
