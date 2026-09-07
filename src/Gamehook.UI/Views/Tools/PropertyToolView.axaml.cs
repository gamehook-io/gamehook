using Avalonia.Controls;
using Avalonia.Input;
using Gamehook.UI.ViewModels.Tools;
using Gamehook.UI.Views;

namespace Gamehook.UI.Views.Tools;

public partial class PropertyToolView : UserControl
{
    public PropertyToolView()
    {
        InitializeComponent();
    }

    private async void ValueTextBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PropertyToolViewModel inspector)
        {
            await inspector.SubmitEditAsync();
        }
    }

    // Commit on Enter without waiting for focus to leave the box - the LostFocus handler above
    // still fires afterward, but SubmitEditAsync no-ops on a re-submit of the same text.
    private async void ValueTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DataContext is PropertyToolViewModel inspector)
        {
            await inspector.SubmitEditAsync();
        }
    }

    private void FloatButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not PropertyToolViewModel inspector ||
            (!inspector.HasSelectedProperty && !inspector.HasSelection))
        {
            return;
        }

        var snapshot = new PropertyToolViewModel(inspector.Main);
        snapshot.FreezeSelection();
        inspector.SuppressCurrentSelection();
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new PropertyWindow { DataContext = snapshot };
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
