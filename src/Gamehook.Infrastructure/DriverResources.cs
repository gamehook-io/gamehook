using System.Globalization;
using Gamehook.Domain.Interface;
using Gamehook.Infrastructure.Drivers;

namespace Gamehook.Infrastructure;

/// What each registered driver connects to, so GamehookInstances can keep two instances off the
/// same one. Network drivers are keyed by host:port regardless of driver name - two drivers on one
/// port would talk to the same emulator. File drivers (Save State) are keyed by the file.
public static class DriverResources
{
    public static DriverResource? Identify(IEnumerable<DriverRegistration> registrations, string driverName, string? sourcePath)
    {
        var registration = registrations.FirstOrDefault(r => string.Equals(r.Name, driverName, StringComparison.OrdinalIgnoreCase));

        if (registration?.DefaultPort is { } defaultPort)
        {
            string host;
            int port;
            try
            {
                (host, port) = NetworkEndpoint.Parse(sourcePath, defaultPort, registration.Name);
            }
            catch (ArgumentException)
            {
                // Unparseable endpoint: the load itself fails with a clearer error.
                return null;
            }

            host = NormalizeHost(host);
            var endpoint = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
            return new DriverResource($"endpoint:{endpoint}", endpoint);
        }

        if (string.IsNullOrWhiteSpace(sourcePath)) return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var key = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        return new DriverResource($"file:{key}", fullPath);
    }

    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().ToLowerInvariant();
        return trimmed is "localhost" or "::1" or "[::1]" ? NetworkEndpoint.DefaultHost : trimmed;
    }
}
