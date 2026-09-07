using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Gamehook.UI.ViewModels;
using Gamehook.UI.ViewModels.Tools;

namespace Gamehook.UI.Views.Tools;

public partial class HexViewerToolView : UserControl
{
    // A click sets SelectedNode either from the tree (clicking a row directly) or from this hex
    // viewer (PropertyClicked below). Either way the *other* side should scroll to mirror the
    // new selection - but never the side that was actually clicked, since the user is already
    // looking at it and a re-center there would just yank the view out from under their click.
    private bool suppressAutoScroll;
    private MainWindowViewModel? subscribedMain;

    public HexViewerToolView()
    {
        InitializeComponent();
        HexViewer.PropertyClicked += (_, property) =>
        {
            if (DataContext is HexViewerToolViewModel { Main: { } main })
            {
                suppressAutoScroll = true;
                main.SelectProperty(property);
            }
        };
        HexViewer.BytesSelected += (_, selection) =>
        {
            if (DataContext is HexViewerToolViewModel { Main: { } main })
            {
                main.SelectBytes(selection.RegionId, selection.StartingAddress, selection.Bytes);
            }
        };
        HexViewer.ByteEditRequested += async (_, edit) =>
        {
            if (DataContext is not HexViewerToolViewModel { Main.Mapper: { } mapper })
            {
                return;
            }

            var (success, _) = await mapper.WriteRawBytesAsync(edit.RegionId, edit.Address, new[] { edit.NewValue });
            if (!success)
            {
                HexViewer.RevertByte(edit.RegionId, edit.Address, edit.OriginalValue);
            }
        };
        DataContextChanged += (_, _) => UpdateSubscription();
        AttachedToVisualTree += (_, _) => UpdateSubscription();
        DetachedFromVisualTree += (_, _) => ClearSubscription();
    }

    private void UpdateSubscription()
    {
        var main = (DataContext as HexViewerToolViewModel)?.Main;
        if (ReferenceEquals(main, subscribedMain)) return;
        ClearSubscription();
        subscribedMain = main;
        if (main is not null) main.ScrollHexToSelectionRequested += OnScrollRequested;
    }

    private void ClearSubscription()
    {
        if (subscribedMain is not null) subscribedMain.ScrollHexToSelectionRequested -= OnScrollRequested;
        subscribedMain = null;
    }

    private void OnScrollRequested()
    {
        if (suppressAutoScroll) { suppressAutoScroll = false; return; }
        if (subscribedMain is { } main) ScrollToSelectedProperty(main);
    }

    // Tree selection can switch SelectedRegionId too, so the hex view needs a layout pass
    // (region rebuild) before its bytes have a Y position to scroll to.
    private void ScrollToSelectedProperty(MainWindowViewModel viewModel)
    {
        if (viewModel.SelectedNode?.Property is not { } property)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (HexViewer.GetPropertyBounds(property) is not { } bounds)
                {
                    return;
                }

                var viewport = HexScrollViewer.Viewport;
                var extent = HexScrollViewer.Extent;
                var targetX = bounds.X + bounds.Width / 2 - viewport.Width / 2;
                var targetY = bounds.Y + bounds.Height / 2 - viewport.Height / 2;
                HexScrollViewer.Offset = new Vector(
                    System.Math.Clamp(targetX, 0, System.Math.Max(0, extent.Width - viewport.Width)),
                    System.Math.Clamp(targetY, 0, System.Math.Max(0, extent.Height - viewport.Height)));
            },
            DispatcherPriority.Loaded);
    }
}
