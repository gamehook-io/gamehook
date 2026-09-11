using Dock.Model.Mvvm.Controls;

namespace Gamehook.UI.ViewModels.Tools;

public sealed class WorkspaceToolViewModel : Document
{
    public MainWindowViewModel Main { get; }

    public WorkspaceToolViewModel(MainWindowViewModel main)
    {
        Main = main;
        Id = "Workspace";
        Title = "Workspace";
    }
}
