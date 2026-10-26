using Avalonia.Controls;
using Avalonia.Interactivity;
using Gamehook.Android.ViewModels;

namespace Gamehook.Android.Views;

public partial class MainView : UserControl
{
    private TopLevel? topLevel;

    public MainView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        topLevel = TopLevel.GetTopLevel(this);
        FilePicker.Host = topLevel;
        if (topLevel is not null) topLevel.BackRequested += OnBackRequested;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (topLevel is not null) topLevel.BackRequested -= OnBackRequested;
        FilePicker.Host = null;
        topLevel = null;
        base.OnUnloaded(e);
    }

    // Peel one layer (sheet, inspector, folder, tab); at the top level fall through to Android,
    // which backgrounds the app. The foreground service keeps the Web API up.
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell && shell.HandleBack()) e.Handled = true;
    }
}
