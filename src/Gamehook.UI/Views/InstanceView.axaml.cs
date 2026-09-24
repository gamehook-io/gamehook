using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Gamehook.UI.ViewModels;

namespace Gamehook.UI.Views;

public partial class InstanceView : UserControl
{
    public InstanceView()
    {
        InitializeComponent();
    }

    private async void BrowseSaveStateButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not InstanceViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var startDirectory = viewModel.GetLastOpenedSaveStateDirectory()
            ?? (viewModel.SelectedSaveStatePath is { } selectedPath ? Path.GetDirectoryName(selectedPath) : null);
        if (await PickFileAsync(topLevel, "Select a save-state file", startDirectory, "Save-state files", "*.state") is not { } path)
        {
            return;
        }

        viewModel.SelectedSaveStatePath = path;
        if (Path.GetDirectoryName(path) is { } directory)
        {
            viewModel.RememberLastOpenedSaveStateDirectory(directory);
        }
    }

    private async void BrowseMapperButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is InstanceViewModel viewModel && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            await BrowseMapperAsync(topLevel, viewModel, loadAfterSelection: false);
        }
    }

    // Shared with MainWindow's File > Load Mapper… menu item, which browses for the selected tab.
    internal static async Task BrowseMapperAsync(TopLevel topLevel, InstanceViewModel viewModel, bool loadAfterSelection)
    {
        var startDirectory = viewModel.GetLastOpenedMapperDirectory()
            ?? (viewModel.SelectedMapper is { } selectedMapper
                ? Path.GetDirectoryName(selectedMapper.FullPath)
                : viewModel.GetMapperDirectory());
        if (await PickFileAsync(topLevel, "Select a mapper file", startDirectory, "Mapper files", "*.xml") is not { } path)
        {
            return;
        }

        viewModel.SelectMapperFile(path);
        if (Path.GetDirectoryName(path) is { } directory)
        {
            viewModel.RememberLastOpenedMapperDirectory(directory);
        }

        if (loadAfterSelection && viewModel.LoadCommand.CanExecute(null))
        {
            await viewModel.LoadCommand.ExecuteAsync(null);
        }
        else
        {
            viewModel.ShowLoadScreen();
        }
    }

    private static async Task<string?> PickFileAsync(TopLevel topLevel, string title, string? startDirectory, string fileTypeName, string pattern)
    {
        var storage = topLevel.StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startDirectory is null ? null : await storage.TryGetFolderFromPathAsync(startDirectory),
            FileTypeFilter = [new FilePickerFileType(fileTypeName) { Patterns = [pattern] }, FilePickerFileTypes.All],
        });

        return files.FirstOrDefault() is { } file ? file.TryGetLocalPath() ?? file.Path.LocalPath : null;
    }
}
