using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Gamehook.Android;

/// Android's file picker hands back content:// URIs, not paths, and the engine (mapper compiler,
/// save state driver) works on real files - so picked files are copied into app storage.
internal static class FilePicker
{
    /// Set by the view once it is attached to a TopLevel.
    public static TopLevel? Host { get; set; }

    /// Lets the user pick files and copies them into <paramref name="targetDirectory"/> under their
    /// own names. Returns the copied paths (empty when cancelled).
    public static async Task<IReadOnlyList<string>> PickIntoAsync(string title, bool allowMultiple, string targetDirectory)
    {
        if (Host?.StorageProvider is not { CanOpen: true } storage) return [];

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
        });
        if (files.Count == 0) return [];

        Directory.CreateDirectory(targetDirectory);
        var copied = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var destination = Path.Combine(targetDirectory, name);
            await using var source = await file.OpenReadAsync();
            await using var target = File.Create(destination);
            await source.CopyToAsync(target);
            copied.Add(destination);
        }

        return copied;
    }
}
