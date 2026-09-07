using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Gamehook.Domain;
using Gamehook.Infrastructure.MapperUpdate;
using Gamehook.Infrastructure.AppUpdate;
using Gamehook.UI.ViewModels;
using Gamehook.UI.ViewModels.Tools;
using Gamehook.UI.Views.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace Gamehook.UI.Views;

public partial class MainWindow : Window
{
    private void PropertyTabFloatButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PropertyToolViewModel inspector } source)
        {
            PropertyToolView.FloatInspector(inspector, source);
            e.Handled = true;
        }
    }
    private static readonly Geometry MaximizeIcon = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly Geometry RestoreIcon = Geometry.Parse("M2.5,0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeRestoreIcon();

        // Set once during host startup (see GamehookApiHostedService) before any window opens -
        // no event needed, just read it here. A bind failure means nothing is listening on
        // Port at all, so the docs link would just 404/refuse - disable it instead.
        if (App.Services.GetRequiredService<ApiBindStatus>().Error is not null)
        {
            ApiDocumentationMenuItem.IsEnabled = false;
            ToolTip.SetTip(ApiDocumentationMenuItem, "REST API failed to start - see the startup warning for details.");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeRestoreIcon();
        }
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeRestoreIcon is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeRestoreIcon.Data = maximized ? RestoreIcon : MaximizeIcon;
        ToolTip.SetTip((Button)MaximizeRestoreIcon.Parent!, maximized ? "Restore" : "Maximize");
    }

    private void ExitMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async void BrowseSaveStateButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        var startDirectory = viewModel?.GetLastOpenedSaveStateDirectory();
        if (startDirectory is null && viewModel?.SelectedSaveStatePath is { } selectedPath)
        {
            startDirectory = Path.GetDirectoryName(selectedPath);
        }

        var startLocation = startDirectory is null
            ? null
            : await StorageProvider.TryGetFolderFromPathAsync(startDirectory);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        await BrowseMapperAsync(viewModel, loadAfterSelection: false);
    }

    private async void LoadMapperMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        await BrowseMapperAsync(viewModel, loadAfterSelection: true);
    }

    private async Task BrowseMapperAsync(MainWindowViewModel viewModel, bool loadAfterSelection)
    {

        var startDirectory = viewModel.GetLastOpenedMapperDirectory()
            ?? (viewModel.SelectedMapper is { } selectedMapper
                ? Path.GetDirectoryName(selectedMapper.FullPath)
                : viewModel.GetMapperDirectory());
        var startLocation = startDirectory is null
            ? null
            : await StorageProvider.TryGetFolderFromPathAsync(startDirectory);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ResizeWest_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.West, e);
    private void ResizeEast_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.East, e);
    private void ResizeNorth_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.North, e);
    private void ResizeSouth_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.South, e);
    private void ResizeNorthWest_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthWest, e);
    private void ResizeNorthEast_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.NorthEast, e);
    private void ResizeSouthWest_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthWest, e);
    private void ResizeSouthEast_PointerPressed(object? sender, PointerPressedEventArgs e) => BeginResize(WindowEdge.SouthEast, e);

    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (CanResize && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void MinimizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleMaximized();

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void AboutMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var mapperUpdateService = App.Services.GetRequiredService<MapperUpdateService>();
        await new AboutWindow(mapperUpdateService,
            App.Services.GetService<AppUpdateStatusProvider>(),
            App.Services.GetService<AppUpdateService>()).ShowDialog(this);
    }

    // Scalar serves the OpenAPI reference at "/" on the REST API's own Kestrel port (see
    // GamehookApiHostedService) - same port whether or not the bind actually succeeded, so this
    // just opens it and lets the browser show its own connection-refused page on failure (the
    // startup AlertWindow in App.axaml.cs already surfaces a bind failure up front).
    private void ApiDocumentationMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var port = App.Services.GetRequiredService<IConfiguration>().GetValue<int>("Port");
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
    }
}
