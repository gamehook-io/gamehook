using Dock.Model.Mvvm.Controls;

namespace Gamehook.UI.ViewModels.Tools;

public sealed class PinnedToolViewModel : Tool
{
    public InstanceViewModel Main { get; }

    public PinnedToolViewModel(InstanceViewModel main)
    {
        Main = main;
        Id = "Pinned";
        Title = "Pinned";
    }
}
