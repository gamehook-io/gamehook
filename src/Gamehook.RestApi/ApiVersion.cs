using System.Reflection;

namespace Gamehook.RestApi;

/// Version reported by `GET /`. Hosts set this to their own app version at startup; the default
/// is this assembly's informational version (without the "+commit" build metadata suffix).
public static class ApiVersion
{
    public static string Current { get; set; } =
        typeof(ApiVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ApiVersion).Assembly.GetName().Version?.ToString()
        ?? string.Empty;
}
