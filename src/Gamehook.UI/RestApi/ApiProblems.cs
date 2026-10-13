using Microsoft.AspNetCore.Http;

namespace Gamehook.RestApi;

internal static class ApiProblems
{
    public static IResult BadRequest(string detail, string code) => Create(StatusCodes.Status400BadRequest, "Invalid request", detail, code);

    public static IResult NotFound(string detail, string code) => Create(StatusCodes.Status404NotFound, "Resource not found", detail, code);

    public static IResult Unprocessable(string detail, string code) => Create(StatusCodes.Status422UnprocessableEntity, "Request could not be processed", detail, code);

    public static IResult Conflict(string detail, string code) => Create(StatusCodes.Status409Conflict, "Request conflicts with current state", detail, code);

    public static IResult ServiceUnavailable(string detail, string code) => Create(StatusCodes.Status503ServiceUnavailable, "Service unavailable", detail, code);

    public static IResult Forbidden(string detail, string code) => Create(StatusCodes.Status403Forbidden, "Forbidden", detail, code);

    public static IResult PayloadTooLarge(string detail, string code) => Create(StatusCodes.Status413PayloadTooLarge, "Request body too large", detail, code);

    public static IResult UnsupportedMediaType(string detail, string code) => Create(StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", detail, code);

    public static IResult ServerError(string detail, string code) => Create(StatusCodes.Status500InternalServerError, "Request failed", detail, code);

    public static IResult InstanceNotFound(int index) => NotFound(
        $"Instance {index} does not exist. GET /instances lists the available instances.",
        "instance_not_found");

    public static IResult ContinuousReadDisabled(string action) => Conflict(
        $"{action} is unavailable while continuous read mode is disabled. Enable it with POST /settings {{ \"continuousRead\": true }} or Settings > Continuous Read Mode.",
        "continuous_read_disabled");

    private static IResult Create(int status, string title, string detail, string code) => Results.Problem(
        statusCode: status,
        title: title,
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
