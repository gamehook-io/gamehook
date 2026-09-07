namespace GameHook.Infrastructure.AppUpdate;

// Deliberately dependency-free (no IConfiguration, no DI, no logging, no FilesystemProvider) -
// this has to run at the very top of Program.cs, before anything else is constructed, so it must
// survive even when the bad build that triggered it broke config loading or DI wiring itself.
public static class CrashGuard
{
    private const string FileName = "launch-health.txt";

    // Three failed-to-become-healthy launches in a row before Program.cs hands off to
    // RecoveryRunner instead of trying to start the app again.
    public const int CrashLoopThreshold = 3;

    // Called once, immediately, on every launch. Returns the number of consecutive launches
    // (including this one) that haven't yet called MarkHealthy().
    public static int RecordLaunchAttempt()
    {
        try
        {
            var path = GetMarkerPath();
            var count = ReadCount(path) + 1;
            File.WriteAllText(path, count.ToString());
            return count;
        }
        catch
        {
            // Filesystem trouble here shouldn't block startup - it just means this launch isn't
            // protected by the crash-loop safety net.
            return 0;
        }
    }

    // Called once the app has actually rendered and survived a short soak period - resets the
    // counter so a single crash doesn't count toward the threshold.
    public static void MarkHealthy()
    {
        try
        {
            File.WriteAllText(GetMarkerPath(), "0");
        }
        catch
        {
        }
    }

    private static int ReadCount(string path) =>
        File.Exists(path) && int.TryParse(File.ReadAllText(path), out var count) ? count : 0;

    private static string GetMarkerPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var directory = Path.Combine(localAppData, "GameHook");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, FileName);
    }
}
