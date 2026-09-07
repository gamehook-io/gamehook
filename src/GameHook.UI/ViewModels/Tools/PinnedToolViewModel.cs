using Dock.Model.Mvvm.Controls;

namespace GameHook.UI.ViewModels.Tools;

public sealed class PinnedToolViewModel : Tool
{
    public MainWindowViewModel Main { get; }

    public PinnedToolViewModel(MainWindowViewModel main)
    {
        Main = main;
        Id = "Pinned";
        Title = "Pinned";
    }
}
