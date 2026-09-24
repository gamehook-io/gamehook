namespace Gamehook.Infrastructure;

internal static class AtomicFile
{
    // Writes beside the target, then renames over it, so a crash mid-write never leaves a
    // truncated file behind. UTF-8 without a BOM.
    public static void WriteAllText(string path, string contents)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
