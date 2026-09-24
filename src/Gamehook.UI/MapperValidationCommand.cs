using System.Text.Json;
using System.Text.Json.Serialization;
using Gamehook.Infrastructure;

namespace Gamehook.UI;

internal static class MapperValidationCommand
{
    private const string Command = "--validate-mapper";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static int? TryRun(string[] args)
    {
        var commandIndex = Array.IndexOf(args, Command);
        if (commandIndex < 0) return null;

        if (args.Length != 2 || commandIndex != 0)
        {
            WriteProblem("Usage: Gamehook.UI --validate-mapper <mapper.xml>");
            return 2;
        }

        try
        {
            Console.WriteLine(JsonSerializer.Serialize(MapperValidation.Validate(args[1]), JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            WriteProblem(ex.Message);
            return 1;
        }
    }

    private static void WriteProblem(string detail) =>
        Console.WriteLine(JsonSerializer.Serialize(new { valid = false, detail }, JsonOptions));
}
