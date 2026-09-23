using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class PropertiesEndpoints
{
    public static void MapPropertiesEndpoints(this WebApplication app)
    {
        app.MapGet("/instance/properties", async (GamehookRouter router, CancellationToken cancellationToken) =>
        {
            if (await ReadOnDemandAsync(router, cancellationToken).ConfigureAwait(false) is { } readProblem)
                return readProblem;
            if (router.Properties is not { } properties)
                return ApiProblems.NotFound("No mapper is loaded.", "mapper_not_loaded");

            return Results.Ok(BuildNestedTree(properties));
        })
        .WithName("GetInstanceProperties")
        .WithSummary("Reads every property's value as a nested JSON object.")
        .WithDescription("Object keys follow mapper property paths, with dots represented as nested objects. Values use mapper-defined JSON types. Returns 404 if no mapper is loaded. While continuous read mode is disabled, the driver is read at request time; 503 if that read fails.")
        .Produces<Dictionary<string, object?>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithTags("Instance");

        app.MapGet("/instance/properties/{*path}", async (
            string path,
            [Microsoft.AspNetCore.Mvc.FromQuery, System.ComponentModel.Description("Return only the decoded property value.")] string? value,
            [Microsoft.AspNetCore.Mvc.FromQuery, System.ComponentModel.Description("Return only the property bytes as integer array.")] string? bytes,
            GamehookRouter router,
            CancellationToken cancellationToken) =>
        {
            if (await ReadOnDemandAsync(router, cancellationToken).ConfigureAwait(false) is { } readProblem)
                return readProblem;

            var name = PathToPropertyName(path);
            if (!router.TryGetProperty(name, out var property))
                return ApiProblems.NotFound($"Property '{path}' was not found.", "property_not_found");

            if (value is not null && bytes is not null)
                return ApiProblems.BadRequest("Use only one of 'value' or 'bytes'.", "property_selector_conflict");
            if (value is not null) return Results.Ok(property.Value);
            if (bytes is not null) return Results.Ok(property.Bytes.ToArray().Select(item => (int)item).ToArray());
            return Results.Ok(ToJson(property));
        })
        .WithName("GetInstanceProperty")
        .WithSummary("Reads one property. Add ?value or ?bytes to get just that field instead of the full object.")
        .WithDescription("Without a query selector, returns property metadata, decoded value, bytes, and hexadecimal bytes. ?value returns only the decoded value; ?bytes returns only the byte array. The value JSON type depends on the property type. While continuous read mode is disabled, the driver is read at request time; 503 if that read fails.")
        .Produces<PropertyResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithTags("Instance");

        app.MapPost("/instance/properties/{*path}", async (string path, WritePropertyRequest request, GamehookRouter router, CancellationToken cancellationToken) =>
        {
            if (!router.Session.IsContinuousReadEnabled)
                return ApiProblems.ContinuousReadDisabled("Writing");

            var name = PathToPropertyName(path);
            if (!router.TryGetProperty(name, out var property))
                return ApiProblems.NotFound($"Property '{path}' was not found.", "property_not_found");

            var hasValue = request.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined;
            var hasBytes = request.Bytes.ValueKind != System.Text.Json.JsonValueKind.Undefined;
            if (hasValue == hasBytes)
                return ApiProblems.BadRequest("Supply exactly one of 'value' or 'bytes'.", "property_write_input_invalid");

            (bool Success, string? Error) result;
            if (hasBytes)
            {
                if (request.Bytes.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return ApiProblems.BadRequest("'bytes' must be an array of integers from 0 to 255.", "property_bytes_invalid");

                byte[] bytes;
                try
                {
                    bytes = request.Bytes.EnumerateArray().Select(item => item.GetByte()).ToArray();
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
                {
                    return ApiProblems.BadRequest("'bytes' must be an array of integers from 0 to 255.", "property_bytes_invalid");
                }
                result = await router.WritePropertyBytesAsync(property, bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await router.WritePropertyValueAsync(name, ToValue(request.Value), cancellationToken).ConfigureAwait(false);
            }

            return result.Success
                ? Results.Ok(new SuccessResponse(true))
                : ApiProblems.Unprocessable(result.Error ?? "Property write failed.", "property_write_failed");
        })
        .WithName("WriteInstanceProperty")
        .WithSummary("Writes a property's value or raw bytes back to the device. Supply exactly one of 'value' or 'bytes'.")
        .WithDescription("For value writes, supply the JSON value accepted by the mapper property, for example { \"value\": \"Red\" }. For byte writes, supply integers from 0 through 255, for example { \"bytes\": [12, 34] }. Supply exactly one field; both or neither returns 400. Returns 409 while continuous read mode is disabled.")
        .Produces<SuccessResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Instance");
    }

    // Continuous read mode keeps the mapper's values fresh on its own; with it disabled nothing polls the
    // driver, so read it now to answer with values as of this request. Null means "go ahead".
    private static async Task<IResult?> ReadOnDemandAsync(GamehookRouter router, CancellationToken cancellationToken)
    {
        var session = router.Session;
        if (session.IsContinuousReadEnabled || session.Mapper is not { } mapper) return null;
        if (await session.ReadOnDemandAsync(cancellationToken).ConfigureAwait(false)) return null;

        return ApiProblems.ServiceUnavailable(
            mapper.LastReadFailureMessage ?? session.ConnectionWarning ?? session.Status,
            "driver_read_failed");
    }

    // Mapper property names are dot-separated (e.g. "party.0.nickname"); the REST path uses '/'
    // per-segment instead, so a catch-all "player/name" route param maps to "player.name".
    private static string PathToPropertyName(string path) => path.Replace('/', '.');

    // Turns the flat "player.name" -> value map into a nested { player: { name: value } } tree.
    private static Dictionary<string, object?> BuildNestedTree(IReadOnlyDictionary<string, IProperty> properties)
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

            node[segments[^1]] = property.Value;
        }

        return root;
    }

    private static PropertyResponse ToJson(IProperty property) => new(
        property.Name,
        property.Type,
        property.Address,
        property.Length,
        property.Region,
        property.Bits,
        property.Reference,
        property.Value,
        property.Bytes.ToArray().Select(value => (int)value).ToArray(),
        property.RawBytesHex);

    private static object? ToValue(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.String => value.GetString(),
        System.Text.Json.JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        System.Text.Json.JsonValueKind.Number => value.GetDouble(),
        System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Select(ToValue).ToArray(),
        System.Text.Json.JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value)),
        _ => null,
    };
}
