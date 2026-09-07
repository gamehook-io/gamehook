using GameHook.Domain;
using GameHook.Domain.Interface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameHook.Api;

public static class PropertiesEndpoints
{
    public static void MapPropertiesEndpoints(this WebApplication app)
    {
        app.MapGet("/properties", (HttpContext http, GameHookRouter router) =>
        {
            if (router.Properties is not { } properties)
                return Results.BadRequest(new { error = "No mapper loaded." });

            var extended = http.Request.Query.ContainsKey("extended");
            return Results.Ok(BuildNestedTree(properties, extended));
        })
        .WithName("GetAllProperties")
        .WithSummary("Reads every property's value as a nested JSON object. Add ?extended for the full object (value, bytes, and metadata) per property.")
        .WithTags("Properties");

        app.MapGet("/properties/{*path}", (string path, HttpContext http, GameHookRouter router) =>
        {
            var name = PathToPropertyName(path);
            if (!router.TryGetProperty(name, out var property))
                return Results.NotFound(new { error = $"Property '{path}' was not found." });

            if (http.Request.Query.ContainsKey("value")) return Results.Ok(property.Value);
            if (http.Request.Query.ContainsKey("bytes")) return Results.Ok(property.Bytes.ToArray());
            return Results.Ok(ToJson(property));
        })
        .WithName("GetProperty")
        .WithSummary("Reads one property. Add ?value or ?bytes to get just that field instead of the full object.")
        .WithTags("Properties");

        app.MapPost("/properties/{*path}", async (string path, WritePropertyRequest request, GameHookRouter router, CancellationToken cancellationToken) =>
        {
            var name = PathToPropertyName(path);
            if (!router.TryGetProperty(name, out var property))
                return Results.NotFound(new { error = $"Property '{path}' was not found." });

            if (request.Value is null && request.Bytes is null)
                return Results.BadRequest(new { error = "One of 'value' or 'bytes' is required." });

            (bool Success, string? Error) result;
            if (request.Bytes is { Length: > 0 })
            {
                var bytes = new byte[request.Bytes.Length];
                for (var i = 0; i < request.Bytes.Length; i++) bytes[i] = checked((byte)request.Bytes[i]);
                result = await router.WritePropertyBytesAsync(property, bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await router.WritePropertyValueAsync(name, request.Value, cancellationToken).ConfigureAwait(false);
            }

            return result.Success ? Results.Ok(new { success = true }) : Results.BadRequest(new { error = result.Error });
        })
        .WithName("WriteProperty")
        .WithSummary("Writes a property's value or raw bytes back to the device. Supply exactly one of 'value' or 'bytes'.")
        .WithTags("Properties");
    }

    // Mapper property names are dot-separated (e.g. "party.0.nickname"); the REST path uses '/'
    // per-segment instead, so a catch-all "player/name" route param maps to "player.name".
    private static string PathToPropertyName(string path) => path.Replace('/', '.');

    // Turns the flat "player.name" -> value map into a nested { player: { name: value } } tree.
    private static Dictionary<string, object?> BuildNestedTree(IReadOnlyDictionary<string, IProperty> properties, bool extended)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, property) in properties)
        {
            var segments = key.Split('.');
            var node = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!node.TryGetValue(segments[i], out var child) || child is not Dictionary<string, object?> childNode)
                {
                    childNode = new Dictionary<string, object?>(StringComparer.Ordinal);
                    node[segments[i]] = childNode;
                }

                node = childNode;
            }

            node[segments[^1]] = extended ? ToJson(property) : property.Value;
        }

        return root;
    }

    private static object ToJson(IProperty property) => new
    {
        name = property.Name,
        type = property.Type,
        address = property.Address,
        length = property.Length,
        region = property.Region,
        bits = property.Bits,
        reference = property.Reference,
        value = property.Value,
        bytes = property.Bytes.ToArray(),
        rawBytesHex = property.RawBytesHex,
    };
}

public sealed record WritePropertyRequest(object? Value = null, int[]? Bytes = null);
