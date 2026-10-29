using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gamehook.RestApi;

public static class KeyValueEndpoints
{
    public static void MapKeyValueEndpoints(this WebApplication app)
    {
        var origins = app.Services.GetRequiredService<DisplayOriginPolicy>();
        var kv = app.MapGroup("/kv").WithTags("Settings").RequireCors(DisplayOriginPolicy.CorsPolicyName);
        // CORS only tells browsers whether they may read a response; this refuses the request itself,
        // so a page on another site cannot write settings with a request that needs no preflight.
        kv.AddEndpointFilter(async (context, next) =>
        {
            var origin = context.HttpContext.Request.Headers.Origin;
            return origin.Count == 0 || (origin.Count == 1 && origins.IsAllowed(origin[0]))
                ? await next(context).ConfigureAwait(false)
                : ApiProblems.Forbidden("Requests from this origin may not use settings.", "origin_not_allowed");
        });

        kv.MapGet("", List)
            .WithName("ListSettings")
            .WithSummary("Lists the ids that have saved settings.")
            .WithDescription("Returns an array of GUIDs, in lowercase hyphenated form, sorted.")
            .Produces<string[]>(StatusCodes.Status200OK);

        kv.MapGet("/{id}", Get)
            .WithName("GetSettings")
            .WithSummary("Returns the saved settings for an id as a JSON object of key/value pairs.")
            .WithDescription("404 when the id has never saved settings. The id must be a GUID in the hyphenated form 00000000-0000-0000-0000-000000000000.")
            .Produces<Dictionary<string, object?>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        kv.MapPost("/{id}", Update)
            .WithName("UpdateSettings")
            .WithSummary("Updates the saved settings for an id and returns the full result.")
            .WithDescription($"The body must be a JSON object (Content-Type: application/json); values can be strings, numbers, booleans, null, lists and nested objects. Each top-level key replaces that key's saved value (the whole value, not a deep merge); a null value removes the key; other keys are untouched. Limits: {KeyValueStore.MaxDocumentBytes} bytes per id, {KeyValueStore.MaxKeys} keys, key names up to {KeyValueStore.MaxKeyLength} characters, strings up to {KeyValueStore.MaxStringLength} characters, JSON nested at most {KeyValueStore.MaxDepth} levels deep, at most {KeyValueStore.MaxNodes} values in total, no duplicate keys, and settings for at most {KeyValueStore.MaxEntries} ids. Saved as kv\\{{id}}.json in the Gamehook profile folder. Browser requests are accepted only from allowed origins (https://gamehook.io, https://*.gamehook.io and localhost by default, see DisplayOrigins); other origins get 403. Never store secrets here.")
            .Accepts<Dictionary<string, object?>>("application/json")
            .Produces<Dictionary<string, object?>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult List(KeyValueStore store) =>
        Results.Ok(store.List().Select(id => id.ToString("D")).ToArray());

    private static IResult Get(string id, KeyValueStore store)
    {
        if (!KeyValueStore.TryParseId(id, out var guid)) return InvalidId();
        try
        {
            return store.Get(guid) is { } settings
                ? Results.Json(settings)
                : ApiProblems.NotFound($"{guid:D} has no saved settings.", "settings_not_found");
        }
        catch (KeyValueStoreException ex)
        {
            return Problem(ex);
        }
    }

    private static async Task<IResult> Update(string id, HttpRequest request, KeyValueStore store, CancellationToken cancellationToken)
    {
        if (!KeyValueStore.TryParseId(id, out var guid)) return InvalidId();
        if (!request.HasJsonContentType()) return ApiProblems.UnsupportedMediaType("The request Content-Type must be application/json.", "unsupported_media_type");

        // Refuse oversized bodies before buffering them, whether or not a Content-Length was sent.
        const int limit = KeyValueStore.MaxDocumentBytes;
        if (request.ContentLength > limit) return TooLarge();
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = limit;
        }

        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > limit) return TooLarge();
                body.Write(buffer, 0, read);
            }

            var patch = KeyValueStore.ParseObject(body.GetBuffer().AsSpan(0, (int)body.Length));
            return Results.Json(store.Merge(guid, patch));
        }
        catch (KeyValueStoreException ex)
        {
            return Problem(ex);
        }
        catch (BadHttpRequestException)
        {
            return TooLarge();
        }
    }

    private static IResult InvalidId() => ApiProblems.BadRequest(
        "The id must be a GUID in the form 00000000-0000-0000-0000-000000000000.", "invalid_id");

    private static IResult TooLarge() => ApiProblems.PayloadTooLarge(
        $"The JSON body is larger than {KeyValueStore.MaxDocumentBytes} bytes.", "settings_too_large");

    private static IResult Problem(KeyValueStoreException ex) => ex.Error switch
    {
        KeyValueError.InvalidJson => ApiProblems.BadRequest(ex.Message, "invalid_settings_json"),
        KeyValueError.InvalidKey => ApiProblems.BadRequest(ex.Message, "invalid_settings_key"),
        KeyValueError.InvalidValue => ApiProblems.BadRequest(ex.Message, "invalid_settings_value"),
        KeyValueError.TooLarge => ApiProblems.PayloadTooLarge(ex.Message, "settings_too_large"),
        KeyValueError.TooManyEntries => ApiProblems.Unprocessable(ex.Message, "too_many_entries"),
        _ => ApiProblems.ServerError(ex.Message, "settings_storage_error"),
    };
}
