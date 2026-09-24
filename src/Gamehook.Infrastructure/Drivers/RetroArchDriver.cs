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

    public const int DefaultPort = 55355;
    public const string AllowMultiFrameReadsKey = "RetroArch:AllowMultiFrameReads";
    private const int ReceiveTimeoutMilliseconds = 2000;

    // An 8KB binary read becomes roughly 24KB of ASCII hex, well below UDP's payload limit.
    private const int MaximumReadChunkLength = 8192;

    // Every chunk is requested at once and RetroArch answers them all in the same frame, so the
    // replies land together: a GBA mapper's ~13 chunks is ~320KB, well past Linux's 208KB default
    // socket buffer, and the kernel silently drops whatever overflows (the read then times out).
    // Ask for room for every reply; the OS may cap it (net.core.rmem_max). Then, if multi-frame reads
    // are allowed, the number of chunks in flight is limited to what the granted buffer can hold, so
    // a poll spreads over several frames; if not, a poll that cannot fit fails instead.
    private const int RequestedReceiveBufferLength = 4 * 1024 * 1024;

    private readonly UdpClient client;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly SemaphoreSlim chunkSlots;
    private readonly bool allowMultiFrameReads;
    private bool disposed;

    // RetroArch echoes the requested address back in its reply, so in-flight requests are
    // demultiplexed by address rather than serialized one-at-a-time. This lets reads for
    // different regions (e.g. WRAM and VRAM) run concurrently over the single UDP socket
    // instead of paying a full round-trip per region, per poll.
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<ReadOnlyMemory<byte>?>> pendingRequests = new();
    private readonly CancellationTokenSource disposeCts = new();
    private readonly Task receiveLoopTask;

    // Addresses where RetroArch has truncated a read (a core memory-map descriptor ends there).
    private readonly ConcurrentDictionary<uint, byte> descriptorBoundaries = new();

    /// <param name="allowMultiFrameReads">
    /// When false, every chunk of a poll is always requested at once so RetroArch answers them in the
    /// same frame, and a poll too large for the socket's receive buffer fails rather than being spread
    /// over several frames (where values from different regions could come from different frames).
    /// </param>
    public RetroArchDriver(string? sourcePath, bool allowMultiFrameReads = true)
    {
        var (host, port) = NetworkEndpoint.Parse(sourcePath, DefaultPort, "RetroArch");
        this.allowMultiFrameReads = allowMultiFrameReads;
        client = new UdpClient();
        client.Client.ReceiveBufferSize = RequestedReceiveBufferLength;
        chunkSlots = new SemaphoreSlim(allowMultiFrameReads
            ? Math.Max(1, client.Client.ReceiveBufferSize / ReplyBufferCost(MaximumReadChunkLength))
            : int.MaxValue);
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
        foreach (var segment in request.Segments)
        {
            if (segment.Bytes.Length == 0) continue;
            var address = BusAddress.Require(request.System, segment);
            var command = Encoding.ASCII.GetBytes(
                $"WRITE_CORE_MEMORY {address:x} {string.Join(' ', segment.Bytes.ToArray().Select(b => b.ToString("x2")))}\n");
            await client.SendAsync(command, command.Length).ConfigureAwait(false);
        }
    }

    // Worst-case buffer space one reply takes: 3 ASCII chars per byte, plus kernel bookkeeping, which
    // Linux charges against the buffer too (it reports double the payload room for that reason).
    private static int ReplyBufferCost(int length) => 2 * (length * 3 + 64);

    private async Task<IDriver.Response> ReadCore(IDriver.Request request)
    {
        var addressable = BusAddress.ResolveReadable(request);

        if (!allowMultiFrameReads)
        {
            var required = addressable.Sum(entry => (long)ReplyBufferCost(entry.Segment.Length)
                + 2L * 64 * (entry.Segment.Length / MaximumReadChunkLength));
            if (required > client.Client.ReceiveBufferSize)
            {
                throw new InvalidOperationException(
                    $"This mapper's reads need a {required / 1024}KB network receive buffer to arrive in a single frame, " +
                    $"but the operating system allows only {client.Client.ReceiveBufferSize / 1024}KB. Raise the limit " +
                    "(on Linux, the net.core.rmem_max sysctl) or set RetroArch:AllowMultiFrameReads to true.");
            }
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

    // RetroArch only services network commands once per emulated frame (from the core's input
    // poll), so every dependent round-trip costs a whole frame (~16.7ms at 60fps). A core truncates
    // a read at a memory-map descriptor boundary (e.g. Gambatte's GB WRAM is split at 0xD000), which
    // used to cost a follow-up request - and a second frame - on every poll. Boundaries learned from
    // those truncations are remembered, so later reads are split at them up front and every piece
    // goes out in the same frame. A stale boundary (different core/content) only costs a split.
    private async Task<ReadOnlyMemory<byte>?> ReadCoreMemory(uint address, int length)
    {
        var bytes = new byte[length];
        var end = address + (uint)length;
        var splits = descriptorBoundaries.Keys.Where(boundary => boundary > address && boundary < end);
        for (var offset = MaximumReadChunkLength; offset < length; offset += MaximumReadChunkLength)
        {
            splits = splits.Append(address + (uint)offset);
        }

        var starts = splits.Distinct().Order().Prepend(address).ToArray();
        var chunks = starts.Select((start, index) =>
            (Offset: (int)(start - address), Length: (int)((index + 1 < starts.Length ? starts[index + 1] : end) - start))).ToArray();

        var received = await Task.WhenAll(chunks.Select(chunk =>
            ReadCoreMemoryRange(address + (uint)chunk.Offset, bytes.AsMemory(chunk.Offset, chunk.Length)))).ConfigureAwait(false);

        for (var index = 0; index < chunks.Length; index++)
        {
            var (offset, requestedLength) = chunks[index];
            if (received[index] == requestedLength) continue;
            if (offset == 0 && received[index] == 0) return null;
            throw new InvalidDataException(
                $"RetroArch stopped responding with data partway through a read at 0x{address:x} (got {offset + received[index]} of {length} byte(s)).");
        }

        return bytes;
    }

    // Fills destination from address, following any descriptor-boundary truncation with a
    // continuation. Returns how many bytes were filled; short only when RetroArch has no data.
    private async Task<int> ReadCoreMemoryRange(uint address, Memory<byte> destination)
    {
        var receivedLength = 0;
        while (receivedLength < destination.Length)
        {
            var start = address + (uint)receivedLength;
            if (await ReadCoreMemoryChunk(start, destination.Length - receivedLength).ConfigureAwait(false) is not { } chunk)
            {
                return receivedLength;
            }

            chunk.Span.CopyTo(destination.Span[receivedLength..]);
            receivedLength += chunk.Length;
            if (receivedLength < destination.Length)
            {
                descriptorBoundaries.TryAdd(address + (uint)receivedLength, 0);
            }
        }

        return receivedLength;
    }

    private async Task<ReadOnlyMemory<byte>?> ReadCoreMemoryChunk(uint address, int length)
    {
        var command = Encoding.ASCII.GetBytes($"READ_CORE_MEMORY {address:x} {length}\n");
        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await chunkSlots.WaitAsync().ConfigureAwait(false);
        if (!pendingRequests.TryAdd(address, tcs))
        {
            chunkSlots.Release();
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
            throw new TimeoutException($"RetroArch did not respond to READ_CORE_MEMORY for address 0x{address:x}. Is RetroArch running with Network Commands enabled?", ex);
        }
        finally
        {
            pendingRequests.TryRemove(address, out _);
            chunkSlots.Release();
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
            chunkSlots.Dispose();
        }
        finally { readGate.Release(); }
    }
}
