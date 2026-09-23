using System.Globalization;

namespace Gamehook.Infrastructure.Drivers;

/// <summary>
/// Parses the source string network drivers accept: empty (localhost on the default port),
/// "host", or "host:port".
/// </summary>
public static class NetworkEndpoint
{
    public const string DefaultHost = "127.0.0.1";

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    public static string Format(string? host, int port) =>
        $"{(string.IsNullOrWhiteSpace(host) ? DefaultHost : host)}:{port.ToString(CultureInfo.InvariantCulture)}";

    public static (string Host, int Port) Parse(string? sourcePath, int defaultPort, string driverName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return (DefaultHost, defaultPort);
        }

        var separatorIndex = sourcePath.LastIndexOf(':');
        if (separatorIndex < 0)
        {
            return (sourcePath, defaultPort);
        }

        var host = sourcePath[..separatorIndex];
        var portText = sourcePath[(separatorIndex + 1)..];
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || !IsValidPort(port))
        {
            throw new ArgumentException($"Invalid {driverName} endpoint '{sourcePath}'.", nameof(sourcePath));
        }

        return (string.IsNullOrWhiteSpace(host) ? DefaultHost : host, port);
    }
}
