using System.Reflection;

namespace Gamehook.UI;

internal static class AppVersion
{
    // Informational version without the "+commit" build metadata suffix.
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? string.Empty;
}
