using Dock.Model.Mvvm.Controls;

namespace GameHook.UI.ViewModels.Tools;

public sealed class PropertiesToolViewModel : Tool
{
    public MainWindowViewModel Main { get; }

    public PropertiesToolViewModel(MainWindowViewModel main)
    {
        Main = main;
        Id = "Properties";
        Title = "Explorer";
    }
}
