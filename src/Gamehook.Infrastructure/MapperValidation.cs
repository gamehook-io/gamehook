using System.Xml.Linq;
using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure;

/// <summary>
/// Compiles a mapper without connecting to an emulator, returning metadata suitable for tooling.
/// </summary>
public static class MapperValidation
{
    public static MapperMetadata Validate(string mapperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);

        var fullPath = Path.GetFullPath(mapperPath);
        var document = XDocument.Load(fullPath, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Mapper has no root element.");
        if (root.Name.LocalName != "mapper")
        {
            throw new InvalidDataException("Mapper root element must be 'mapper'.");
        }

        using var mapper = new Mapper(fullPath, ValidationDriver.Instance);
        return new MapperMetadata(
            fullPath,
            (string?)root.Attribute("id"),
            mapper.GameName,
            mapper.System.Id,
            (string?)root.Attribute("version"),
            mapper.NativeProcessorId,
            mapper.Properties.Count,
            mapper.References.Count,
            File.Exists(Path.ChangeExtension(fullPath, ".js")));
    }

    private sealed class ValidationDriver : IDriver
    {
        public static readonly ValidationDriver Instance = new();

        public Task<IDriver.Response> Read(IDriver.Request request) =>
            throw new InvalidOperationException("Mapper validation never reads emulator memory.");
    }
}

public sealed record MapperMetadata(
    string Path,
    string? Id,
    string Name,
    string Platform,
    string? Version,
    string? NativeProcessor,
    int PropertyCount,
    int ReferenceTableCount,
    bool HasScript);
