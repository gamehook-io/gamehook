using System.Globalization;

namespace Gamehook.UI;

internal static class ValueFormatter
{
    // Display text for a decoded property value; culture-invariant so numbers match the API.
    public static string Format(object? value) => value switch
    {
        null => "",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
