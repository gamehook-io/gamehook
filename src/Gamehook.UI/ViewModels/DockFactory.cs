using System;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Gamehook.UI.ViewModels.Tools;

namespace Gamehook.UI.ViewModels;

public sealed class DockFactory : Factory
{
    private readonly MainWindowViewModel main;
    private readonly PropertiesToolViewModel properties;
    private readonly HexViewerToolViewModel hexViewer;
    private readonly PropertyToolViewModel property;
    private readonly PinnedToolViewModel pinned;

    public event Action<PropertyToolViewModel>? PropertyInspectorReplaced;

    public DockFactory(
        MainWindowViewModel main,
        PropertiesToolViewModel properties,
        HexViewerToolViewModel hexViewer,
        PropertyToolViewModel property,
        PinnedToolViewModel pinned)
    {
        this.main = main;
        this.properties = properties;
        this.hexViewer = hexViewer;
        this.property = property;
        this.pinned = pinned;
    }

    public override IRootDock CreateLayout()
    {
        var leftDock = new ToolDock
        {
            Proportion = 0.25,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties),
            Alignment = Alignment.Left,
            IsCollapsable = false,
        };

        var hexViewerDock = new ToolDock
        {
            Proportion = 0.45,
            ActiveDockable = hexViewer,
            VisibleDockables = CreateList<IDockable>(hexViewer),
            IsCollapsable = false,
        };

        var rightDock = new ProportionalDock
        {
            Proportion = 0.3,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>
            (
                new ToolDock
                {
                    Proportion = 0.7,
                    ActiveDockable = property,
                    VisibleDockables = CreateList<IDockable>(property),
                    Alignment = Alignment.Right,
                    IsCollapsable = false,
                },
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Proportion = 0.3,
                    ActiveDockable = pinned,
                    VisibleDockables = CreateList<IDockable>(pinned),
                    Alignment = Alignment.Right,
                    IsCollapsable = false,
                }
            ),
        };

        var mainLayout = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>
            (
                leftDock,
                new ProportionalDockSplitter(),
                hexViewerDock,
                new ProportionalDockSplitter(),
                rightDock
            ),
        };

        var rootDock = CreateRootDock();
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        return rootDock;
    }

    // Floating property snapshots are the only dockables that can close - route their close
    // through the hide/restore path instead of removing them outright.
    public override void CloseDockable(IDockable? dockable)
    {
        // Floating property snapshots are disposable windows. Unlike main-layout tools, their
        // close button removes the snapshot instead of merely hiding a panel.
        if (dockable is PropertyToolViewModel { IsFloatingSnapshot: true })
        {
            base.CloseDockable(dockable);
            return;
        }

        if (dockable is Tool)
        {
            HideDockable(dockable);
            return;
        }

        base.CloseDockable(dockable);
    }

    // Floating an inspector turns it into a property-specific snapshot. The original ToolDock
    // stays alive, then receives a new inspector that waits for the next tree/hex selection.
    public override void FloatDockable(IDockable dockable)
    {
        FloatPropertyDockable(dockable, () => base.FloatDockable(dockable));
    }

    public override void FloatDockable(IDockable dockable, DockWindowOptions? options)
    {
        FloatPropertyDockable(dockable, () => base.FloatDockable(dockable, options));
    }

    private void FloatPropertyDockable(IDockable? dockable, Action floatDockable)
    {
        if (dockable is not PropertyToolViewModel inspector || dockable.Owner is not IDock sourceDock)
        {
            floatDockable();
            return;
        }

        inspector.FreezeSelection();
        floatDockable();
        inspector.CanClose = true;
        inspector.CanFloat = false;

        var replacement = new PropertyToolViewModel(main, suppressCurrentSelection: true)
        {
            CanClose = false,
            CanPin = false,
            CanFloat = true,
            CanDockAsDocument = false,
            CanDrag = false,
            CanDrop = false,
        };
        AddDockable(sourceDock, replacement);
        sourceDock.ActiveDockable = replacement;
        PropertyInspectorReplaced?.Invoke(replacement);
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new()
        {
            ["Properties"] = () => main,
            ["HexViewer"] = () => main,
            ["Property"] = () => main,
            ["Pinned"] = () => main,
        };

        HostWindowLocator = new()
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };

        base.InitLayout(layout);
    }
}
