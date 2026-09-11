using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure.Drivers;

public sealed class SaveStateDriver(string statePath) : IDriver
{
    public const string Name = "Save State";

    private const int RzipHeaderLength = 20;
    private const int GameBoyHighMemoryLength = 512;
    private static readonly byte[] RzipMagic = [35, 82, 90, 73, 80, 118, 1, 35];
    private static readonly byte[] RetroArchStateMagic = "RASTATE"u8.ToArray();
    private DateTime lastWriteTimeUtc = DateTime.MinValue;
    private long lastLength = -1;
    private IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? cachedRegions;

    public async Task<IDriver.Response> Read(IDriver.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.System);
        ArgumentNullException.ThrowIfNull(request.Segments);
        EnsureSupportedSystem(request.System);

        var fileInfo = new FileInfo(statePath);
        if (fileInfo.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("Save-state file exceeds 64 MiB.");
        if (cachedRegions is null || fileInfo.LastWriteTimeUtc != lastWriteTimeUtc || fileInfo.Length != lastLength)
        {
            var fileBytes = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
            cachedRegions = ReadRegions(fileBytes);
            // Keep pre-read metadata: a concurrent save must invalidate the next read.
            lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            lastLength = fileInfo.Length;
        }

        var regions = cachedRegions!;
        var segments = new List<IDriver.MemorySegmentSnapshot>(request.Segments.Count);

        foreach (var requestSegment in request.Segments)
        {
            if (!regions.TryGetValue(requestSegment.RegionId, out var region))
            {
                throw new NotSupportedException($"Save state does not contain region '{requestSegment.RegionId}'.");
            }

            if (requestSegment.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), requestSegment.Length, "Segment length cannot be negative.");
            }

            if (requestSegment.StartingAddress > (ulong)region.Length ||
                (ulong)requestSegment.Length > (ulong)region.Length - requestSegment.StartingAddress)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Requested segment is outside region bounds.");
            }

            segments.Add(new IDriver.MemorySegmentSnapshot(
                requestSegment.RegionId,
                requestSegment.StartingAddress,
                region.Slice((int)requestSegment.StartingAddress, requestSegment.Length)));
        }

        return new IDriver.Response(DateTimeOffset.UtcNow, segments);
    }

    private static void EnsureSupportedSystem(GameSystem system)
    {
        if (!ReferenceEquals(system, GameSystem.GB) && !ReferenceEquals(system, GameSystem.GBC))
        {
            throw new NotSupportedException("Save-state driver supports only GB and GBC.");
        }
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> ReadRegions(byte[] fileBytes)
    {
        var stateBytes = HasPrefix(fileBytes, RzipMagic) ? DecompressRzip(fileBytes) : fileBytes;

        if (!HasPrefix(stateBytes, RetroArchStateMagic))
        {
            return new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SRAM"] = stateBytes
            };
        }

        var blocks = ReadGameBoyBlocks(GetGameBoyState(stateBytes));
        var regions = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);

        AddRegion("SRAM", "sram");
        AddRegion("VRAM", "vram");
        AddRegion("WRAM", "wram");
        AddRegion("Wave RAM", "waveram");

        if (blocks.TryGetValue("hram", out var highMemory))
        {
            if (highMemory.Length != GameBoyHighMemoryLength)
            {
                throw new InvalidDataException("Game Boy high-memory block has an unexpected size.");
            }

            regions["OAM"] = highMemory.Slice(0x000, 0x0A0);
            regions["IO"] = highMemory.Slice(0x100, 0x080);
            regions["HRAM"] = highMemory.Slice(0x180, 0x07F);
            regions["IE"] = highMemory.Slice(0x1FF, 0x001);
        }

        return regions;

        void AddRegion(string regionId, string blockId)
        {
            if (blocks.TryGetValue(blockId, out var block))
            {
                regions[regionId] = block;
            }
        }
    }

    private static byte[] DecompressRzip(byte[] fileBytes)
    {
        if (fileBytes.Length < RzipHeaderLength)
        {
            throw new InvalidDataException("RZIP file is shorter than its header.");
        }

        var uncompressedLength = BinaryPrimitives.ReadUInt64LittleEndian(fileBytes.AsSpan(12, 8));
        if (uncompressedLength > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("RZIP state is too large.");
        }

        var outputBytes = new byte[(int)uncompressedLength];
        using var output = new MemoryStream(outputBytes, writable: true);
        var sourceOffset = RzipHeaderLength;

        while (output.Position < output.Length)
        {
            if (sourceOffset > fileBytes.Length - sizeof(uint))
            {
                throw new InvalidDataException("RZIP state has a truncated chunk header.");
            }

            var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(sourceOffset, sizeof(uint)));
            sourceOffset += sizeof(uint);

            if (compressedLength == 0 || compressedLength > fileBytes.Length - sourceOffset)
            {
                throw new InvalidDataException("RZIP state has an invalid chunk length.");
            }

            using var input = new MemoryStream(fileBytes, sourceOffset, (int)compressedLength, writable: false);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            inflater.CopyTo(output);
            sourceOffset += (int)compressedLength;
        }

        if (sourceOffset != fileBytes.Length)
        {
            throw new InvalidDataException("RZIP state has unexpected trailing data.");
        }

        return outputBytes;
    }

    private static ReadOnlyMemory<byte> GetGameBoyState(byte[] stateBytes)
    {
        const int headerLength = 8;
        const int blockHeaderLength = 8;
        var state = stateBytes.AsSpan();

        if (state.Length < headerLength + blockHeaderLength)
        {
            throw new InvalidDataException("RetroArch state is shorter than its header.");
        }

        var offset = headerLength;
        while (offset < state.Length)
        {
            if (offset > state.Length - blockHeaderLength)
            {
                throw new InvalidDataException("RetroArch state has a truncated block header.");
            }

            var blockId = Encoding.ASCII.GetString(state.Slice(offset, 4));
            var blockLength = BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(offset + 4, 4));
            offset += blockHeaderLength;

            if (blockLength > state.Length - offset)
            {
                throw new InvalidDataException("RetroArch state has an invalid block length.");
            }

            if (blockId == "MEM ")
            {
                return stateBytes.AsMemory(offset, (int)blockLength);
            }

            if (blockId == "END ")
            {
                break;
            }

            offset += (int)blockLength;
        }

        throw new InvalidDataException("RetroArch state does not contain serialized core memory.");
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> ReadGameBoyBlocks(ReadOnlyMemory<byte> coreState)
    {
        var state = coreState.Span;
        if (state.Length < 5 || state[0] != 0)
        {
            throw new InvalidDataException("Unsupported Game Boy save-state version.");
        }

        var offset = 5 + ReadUInt24BigEndian(state.Slice(2, 3));
        if (offset > state.Length)
        {
            throw new InvalidDataException("Game Boy save state has an invalid snapshot length.");
        }

        var blocks = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        while (offset < state.Length)
        {
            var labelLength = state[offset..].IndexOf((byte)0);
            if (labelLength < 0 || offset + labelLength + 4 > state.Length)
            {
                throw new InvalidDataException("Game Boy save state has a malformed block label.");
            }

            var label = Encoding.ASCII.GetString(state.Slice(offset, labelLength));
            offset += labelLength + 1;
            var blockLength = ReadUInt24BigEndian(state.Slice(offset, 3));
            offset += 3;

            if (blockLength > state.Length - offset)
            {
                throw new InvalidDataException("Game Boy save state has an invalid block length.");
            }

            blocks[label] = coreState.Slice(offset, blockLength);
            offset += blockLength;
        }

        return blocks;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix)
        => bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
}
