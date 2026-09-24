using Avalonia.Controls;
using Gamehook.UI.ViewModels.Tools;
using Gamehook.UI.Views;

namespace Gamehook.UI.Views.Tools;

public partial class PropertyToolView : UserControl
{
    public PropertyToolView()
    {
        InitializeComponent();
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
