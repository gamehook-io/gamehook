using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Gamehook.UI.ViewModels;
using Gamehook.UI.ViewModels.Tools;

namespace Gamehook.UI.Views.Tools;

public partial class PropertiesToolView : UserControl
{
    private MainWindowViewModel? subscribedMain;
    public PropertiesToolView()
    {
        InitializeComponent();
        Tree.AddHandler(
            InputElement.PointerPressedEvent,
            Tree_PointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DataContextChanged += (_, _) => UpdateSubscription();
        AttachedToVisualTree += (_, _) => UpdateSubscription();
        DetachedFromVisualTree += (_, _) => ClearSubscription();
    }

    private void UpdateSubscription()
    {
        var main = (DataContext as PropertiesToolViewModel)?.Main;
        if (ReferenceEquals(main, subscribedMain)) return;
        ClearSubscription();
        subscribedMain = main;
        if (main is not null) main.CenterTreeItemRequested += OnCenterRequested;
    }

    private void ClearSubscription()
    {
        if (subscribedMain is not null) subscribedMain.CenterTreeItemRequested -= OnCenterRequested;
        subscribedMain = null;
    }

    private void OnCenterRequested(PropertyTreeNodeViewModel node) =>
        Dispatcher.UIThread.Post(() => CenterTreeItem(node), DispatcherPriority.Loaded);

    // TreeView.ScrollIntoView only nudges the item to the nearest visible edge, not the middle
    // of the viewport - force realization with it first (needed anyway since a just-expanded
    // parent may not have a container yet), then reposition precisely like the hex view does.
    private void CenterTreeItem(PropertyTreeNodeViewModel node)
    {
        Tree.ScrollIntoView(node);

        if (Tree.TreeContainerFromItem(node) is not { } container ||
            Tree.FindDescendantOfType<ScrollViewer>() is not { } scrollViewer)
        {
            return;
        }

        var bounds = container.TranslatePoint(new Point(0, 0), scrollViewer) ?? default;
        var viewport = scrollViewer.Viewport;
        var extent = scrollViewer.Extent;
        var targetY = scrollViewer.Offset.Y + bounds.Y + container.Bounds.Height / 2 - viewport.Height / 2;
        scrollViewer.Offset = new Vector(
            scrollViewer.Offset.X,
            System.Math.Clamp(targetY, 0, System.Math.Max(0, extent.Height - viewport.Height)));
    }

    // TreeView only expands/collapses via its tiny chevron glyph by default. Toggle on any
    // row click instead, unless the click landed on the chevron itself (which already toggled).
    private void Tree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual visual)
        {
            return;
        }

        if (visual.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
        {
            return;
        }

        if (visual.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is PropertyTreeNodeViewModel { Children.Count: > 0 } node)
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }
}
