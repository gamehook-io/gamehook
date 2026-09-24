using Dock.Model.Mvvm.Controls;

namespace Gamehook.UI.ViewModels.Tools;

public sealed class WorkspaceToolViewModel : Document
{
    public InstanceViewModel Main { get; }

    public WorkspaceToolViewModel(InstanceViewModel main)
    {
        Main = main;
        Id = "Workspace";
        Title = "Workspace";
    }
}
