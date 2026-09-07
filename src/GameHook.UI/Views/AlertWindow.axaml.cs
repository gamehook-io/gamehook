using Avalonia.Controls;

namespace GameHook.UI.Views;

// Generic dismissible message dialog - currently used to warn the user, non-fatally, that the
// REST API/WebSocket port failed to bind (see ApiBindStatus).
public partial class AlertWindow : Window
{
    public AlertWindow()
    {
        InitializeComponent();
    }

    public AlertWindow(string title, string message) : this()
    {
        Title = title;
        MessageText.Text = message;
    }

    private void DismissButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
