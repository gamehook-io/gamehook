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
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var viewModel = DataContext as InstanceViewModel;
        var startDirectory = viewModel?.GetLastOpenedSaveStateDirectory();
        if (startDirectory is null && viewModel?.SelectedSaveStatePath is { } selectedPath)
        {
            startDirectory = Path.GetDirectoryName(selectedPath);
        }

        var startLocation = startDirectory is null
            ? null
            : await storageProvider.TryGetFolderFromPathAsync(startDirectory);

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a save-state file",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter =
            [
                new FilePickerFileType("Save-state files") { Patterns = ["*.state"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.FirstOrDefault() is { } file && viewModel is not null)
        {
            var path = file.TryGetLocalPath() ?? file.Path.LocalPath;
            viewModel.SelectedSaveStatePath = path;
            if (Path.GetDirectoryName(path) is { } directory)
            {
                viewModel.RememberLastOpenedSaveStateDirectory(directory);
            }
        }
    }

    private async void BrowseMapperButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not InstanceViewModel viewModel)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        await BrowseMapperAsync(topLevel, viewModel, loadAfterSelection: false);
    }

    // Shared with MainWindow's File > Load Mapper… menu item, which browses for the selected tab.
    internal static async Task BrowseMapperAsync(TopLevel topLevel, InstanceViewModel viewModel, bool loadAfterSelection)
    {
        var startDirectory = viewModel.GetLastOpenedMapperDirectory()
            ?? (viewModel.SelectedMapper is { } selectedMapper
                ? Path.GetDirectoryName(selectedMapper.FullPath)
                : viewModel.GetMapperDirectory());
        var startLocation = startDirectory is null
            ? null
            : await topLevel.StorageProvider.TryGetFolderFromPathAsync(startDirectory);

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a mapper file",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter =
            [
                new FilePickerFileType("Mapper files") { Patterns = ["*.xml"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.FirstOrDefault() is not { } file)
        {
            return;
        }

        var path = file.TryGetLocalPath() ?? file.Path.LocalPath;
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
}
