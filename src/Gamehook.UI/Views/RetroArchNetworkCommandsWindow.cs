using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Gamehook.Infrastructure.Drivers;
using Microsoft.Extensions.DependencyInjection;

namespace Gamehook.UI.Views;

/// <summary>Explicit consent dialog before Gamehook changes a RetroArch configuration file.</summary>
public sealed class RetroArchNetworkCommandsWindow : Window
{
    private readonly ComboBox configurationFiles;
    private readonly TextBlock error;

    public RetroArchNetworkCommandsWindow(IReadOnlyList<string> files)
    {
        Title = "Enable RetroArch Network Commands";
        Width = 620;
        MinWidth = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        configurationFiles = new ComboBox
        {
            ItemsSource = files,
            SelectedIndex = files.Count > 0 ? 0 : -1,
            IsVisible = files.Count > 0,
        };
        error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var message = new TextBlock
        {
            Text = files.Count > 0
                ? "RetroArch did not answer on UDP port 55355. Gamehook found this RetroArch configuration file. Enable Network Commands there? RetroArch must be restarted before Gamehook can connect."
                : "RetroArch did not answer on UDP port 55355. No common RetroArch configuration file was found. In RetroArch, enable Settings > Network > Network Commands, then restart RetroArch.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var configure = new Button { Content = "Enable and restart RetroArch", IsVisible = files.Count > 0, MinWidth = 180 };
        configure.Click += Configure_Click;
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close(false);

        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Network Commands unavailable", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    message,
                    configurationFiles,
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, configure },
                    },
                },
            },
        };
    }

    private void Configure_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (configurationFiles.SelectedItem is not string path) return;
        try
        {
            App.Services.GetRequiredService<RetroArchConfigurationService>().EnableNetworkCommands(path);
            Close(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.Text = $"Could not update {path}: {ex.Message}";
        }
    }
}
