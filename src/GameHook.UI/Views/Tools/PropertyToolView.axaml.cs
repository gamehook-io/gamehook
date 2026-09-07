using Avalonia.Controls;
using GameHook.UI.ViewModels.Tools;
using GameHook.UI.Views;

namespace GameHook.UI.Views.Tools;

public partial class PropertyToolView : UserControl
{
    public PropertyToolView()
    {
        InitializeComponent();
    }

    private async void SaveValueButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PropertyToolViewModel inspector)
        {
            await inspector.SubmitEditAsync();
        }
    }

    private void CancelValueButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is PropertyToolViewModel inspector)
        {
            inspector.CancelEdit();
        }
    }

    internal static void FloatInspector(PropertyToolViewModel inspector, Control source)
    {
        if (!inspector.HasSelectedProperty && !inspector.HasSelection)
        {
            return;
        }

        var snapshot = new PropertyToolViewModel(inspector.Main);
        snapshot.FreezeSelection();
        inspector.SuppressCurrentSelection();
        var owner = TopLevel.GetTopLevel(source) as Window;
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
