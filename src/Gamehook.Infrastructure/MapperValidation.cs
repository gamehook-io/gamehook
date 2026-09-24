using System.Text.Json.Serialization;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Mapping;

namespace Gamehook.Infrastructure;

/// <summary>
/// Compiles a mapper without connecting to an emulator, returning metadata suitable for tooling.
/// </summary>
public static class MapperValidation
{
    public static MapperMetadata Validate(string mapperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);

        var definition = MapperCompiler.Load(mapperPath);
        using var mapper = new Mapper(definition, ValidationDriver.Instance);
        return new MapperMetadata(
            mapper.MapperPath,
            mapper.Id,
            mapper.GameName,
            mapper.System.Id,
            mapper.NativeProcessorId,
            mapper.Properties.Count,
            mapper.References.Count,
            definition.ScriptSource is not null);
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
    string? NativeProcessor,
    int PropertyCount,
    int ReferenceTableCount,
    bool HasScript)
{
    // Serialized first so `--validate-mapper` output reads { "valid": true, ... }.
    [JsonPropertyOrder(-1)]
    public bool Valid => true;
}
