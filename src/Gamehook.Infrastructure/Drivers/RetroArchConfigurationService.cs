using System.Net.Sockets;

namespace Gamehook.Infrastructure.Drivers;

/// <summary>Probes RetroArch Network Commands and safely updates known local configuration files.</summary>
public sealed class RetroArchConfigurationService
{
    public async Task<bool> NetworkCommandsAvailableAsync(int port = RetroArchDriver.DefaultPort, CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient();
        client.Connect(NetworkEndpoint.DefaultHost, port);
        var command = "VERSION\n"u8.ToArray();
        await client.SendAsync(command, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await client.ReceiveAsync(cancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return response.Buffer.Length > 0;
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> FindConfigurationFiles() => CandidateConfigurationFiles()
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void EnableNetworkCommands(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);
        var lines = File.ReadAllLines(fullPath).ToList();
        var replacement = "network_cmd_enable = \"true\"";
        var updated = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("network_cmd_enable", StringComparison.Ordinal)
                && !trimmed.StartsWith('#'))
            {
                lines[index] = replacement;
                updated = true;
                break;
            }
        }

        if (!updated) lines.Add(replacement);

        AtomicFile.WriteAllText(fullPath, string.Concat(lines.Select(line => line + Environment.NewLine)));
    }

    private static IEnumerable<string> CandidateConfigurationFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(appData)) yield return Path.Combine(appData, "RetroArch", "retroarch.cfg");
        if (!string.IsNullOrWhiteSpace(localAppData)) yield return Path.Combine(localAppData, "RetroArch", "retroarch.cfg");
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".config", "retroarch", "retroarch.cfg");
            yield return Path.Combine(home, ".var", "app", "org.libretro.RetroArch", "config", "retroarch", "retroarch.cfg");
            yield return Path.Combine(home, "Library", "Application Support", "RetroArch", "retroarch.cfg");
        }
    }
}
