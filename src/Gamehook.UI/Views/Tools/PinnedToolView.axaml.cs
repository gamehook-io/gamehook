using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Gamehook.UI.ViewModels;
using Gamehook.UI.ViewModels.Tools;

namespace Gamehook.UI.Views.Tools;

public partial class PinnedToolView : UserControl
{
    public PinnedToolView()
    {
        InitializeComponent();
    }

    // Fires on any click within a pinned row's Border. The pin/unpin Button nested inside it
    // marks its own PointerPressed handled, so this never sees clicks that landed on that button.
    private void PinnedRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual visual &&
            visual.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Border { DataContext: PropertyTreeNodeViewModel node } &&
            DataContext is PinnedToolViewModel { Main: { } main })
        {
            main.SelectPinnedNodeCommand.Execute(node);
        }
    }
}
