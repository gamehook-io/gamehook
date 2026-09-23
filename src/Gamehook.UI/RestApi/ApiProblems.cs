using Microsoft.AspNetCore.Http;

namespace Gamehook.RestApi;

internal static class ApiProblems
{
    public static IResult BadRequest(string detail, string code) => Create(StatusCodes.Status400BadRequest, "Invalid request", detail, code);

    public static IResult NotFound(string detail, string code) => Create(StatusCodes.Status404NotFound, "Resource not found", detail, code);

    public static IResult Unprocessable(string detail, string code) => Create(StatusCodes.Status422UnprocessableEntity, "Request could not be processed", detail, code);

    private static IResult Create(int status, string title, string detail, string code) => Results.Problem(
        statusCode: status,
        title: title,
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
