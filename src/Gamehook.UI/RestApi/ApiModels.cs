using System.ComponentModel;
using System.Text.Json;

namespace Gamehook.RestApi;

/// <summary>Gamehook instance metadata.</summary>
public sealed record GamehookInfoResponse(
    [property: Description("Gamehook application version.")] string Version);

/// <summary>Metadata for the currently loaded mapper.</summary>
public sealed record LoadedMapperResponse(
    [property: Description("Mapper identifier from its definition.")] string? Id,
    [property: Description("Display name of the game.")] string Name,
    [property: Description("Target platform identifier.")] string Platform,
    [property: Description("Native processor identifier, when used.")] string? NativeProcessor,
    [property: Description("True when the mapper is not an official one (user-mappers folder, MapperDirectory, or elsewhere on disk).")] bool Custom);

/// <summary>An available mapper that can be loaded with POST /mapper.</summary>
public sealed record AvailableMapperResponse(
    [property: Description("Value to pass to POST /mapper.")] string Value,
    string? Id,
    string Name,
    string? Platform,
    [property: Description("True for custom mappers (user-mappers folder or MapperDirectory) rather than the official set.")] bool Custom,
    bool Loaded);

/// <summary>Full value and metadata for one mapper property.</summary>
public sealed record PropertyResponse(
    [property: Description("Dotted mapper property path.")] string Name,
    [property: Description("Mapper-defined property type.")] string Type,
    [property: Description("Absolute property address, when memory-backed.")] ulong? Address,
    [property: Description("Property length in bytes.")] int Length,
    [property: Description("Memory region, when memory-backed.")] string? Region,
    [property: Description("Bit mask, when property maps to selected bits.")] string? Bits,
    [property: Description("Reference table name, when configured.")] string? Reference,
    [property: Description("Decoded current value. JSON type depends on the mapper property type.")] object? Value,
    [property: Description("Last-read bytes as integers from 0 through 255.")] int[] Bytes,
    [property: Description("Last-read bytes as space-separated uppercase hexadecimal.")] string RawBytesHex);

/// <summary>Successful write result.</summary>
public sealed record SuccessResponse(bool Success);

/// <summary>Selected driver.</summary>
public sealed record DriverResponse(
    [property: Description("Selected driver name.")] string Value,
    [property: Description("Port the driver connects to, for network drivers.")] int? Port = null);

/// <summary>Mapper load result.</summary>
public sealed record MapperLoadResponse(string Value, string Status);

/// <summary>Loads a mapper by key from the configured mapper directory.</summary>
public sealed record LoadMapperRequest(
    [property: Description("Mapper key returned by the mapper directory listing.")] string Value);

/// <summary>Writes one mapper property. Supply exactly one of value or bytes.</summary>
public sealed class WritePropertyRequest
{
    [Description("Decoded property value to encode and write. Must not be supplied with bytes.")]
    public JsonElement Value { get; init; }

    [Description("Raw property bytes to write. Each integer must be from 0 through 255. Must not be supplied with value.")]
    public JsonElement Bytes { get; init; }
}

/// <summary>Runtime-changeable Gamehook settings.</summary>
public sealed record SettingsResponse(
    [property: Description("Whether Gamehook continuously reads the driver. WebSocket updates and writes require it.")] bool ContinuousRead);

/// <summary>Changes settings. Omitted fields are left unchanged.</summary>
public sealed record UpdateSettingsRequest(
    [property: Description("Enable or disable continuous read mode.")] bool? ContinuousRead = null);
