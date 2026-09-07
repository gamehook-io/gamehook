using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Gamehook.UI.ViewModels;

namespace Gamehook.UI.ViewModels.Tools;

public sealed partial class PropertyToolViewModel : Tool, IDisposable
{
    public MainWindowViewModel Main { get; }
    private PropertyTreeNodeViewModel? suppressedSelection;
    private IReadOnlyList<SelectionParseViewModel>? suppressedSelectionParses;
    private bool isFrozen;
    private IReadOnlyList<SelectionParseViewModel>? frozenSelectionParses;
    private string? frozenSelectionAddressRange;
    private RawByteSelection? frozenRawSelection;

    [ObservableProperty]
    private PropertyTreeNodeViewModel? selectedNode;

    // A replacement inspector starts blank even though the property that was floated remains
    // selected globally. Its next selection behaves like a normal docked inspector.
    public PropertyToolViewModel(MainWindowViewModel main, bool suppressCurrentSelection = false)
    {
        Main = main;
        suppressedSelection = suppressCurrentSelection ? main.SelectedNode : null;
        suppressedSelectionParses = suppressCurrentSelection ? main.SelectionParses : null;
        Id = "Property";
        Title = "Property";
        Main.PropertyChanged += OnMainPropertyChanged;
        Main.RawSelectionUpdated += OnRawSelectionUpdated;
        UpdateFloatCapability();
    }

    public PropertyTreeNodeViewModel? ActiveNode => isFrozen
        ? SelectedNode
        : ReferenceEquals(Main.SelectedNode, suppressedSelection) ? null : Main.SelectedNode;

    public IReadOnlyList<SelectionParseViewModel>? SelectionParses => isFrozen
        ? frozenSelectionParses
        : ReferenceEquals(Main.SelectionParses, suppressedSelectionParses) ? null : Main.SelectionParses;
    public string? SelectionAddressRange => isFrozen
        ? frozenSelectionAddressRange
        : ReferenceEquals(Main.SelectionParses, suppressedSelectionParses) ? null : Main.SelectionAddressRange;
    public bool IsFloatingSnapshot => isFrozen;
    public bool HasSelection => SelectionParses is { Count: > 0 };
    public bool HasSelectedProperty => ActiveNode?.Property is not null;
    public new bool IsEmpty => !HasSelection && !HasSelectedProperty;

    // Called by DockFactory before the dock library creates a host window. The floating panel
    // must no longer track later tree/hex selections, otherwise every float would show same data.
    public void FreezeSelection()
    {
        if (isFrozen)
        {
            return;
        }

        SelectedNode = Main.SelectedNode;
        frozenSelectionParses = Main.SelectionParses;
        frozenSelectionAddressRange = Main.SelectionAddressRange;
        frozenRawSelection = Main.RawSelection;
        isFrozen = true;
        if (frozenRawSelection is null)
        {
            Main.PropertyChanged -= OnMainPropertyChanged;
            Main.RawSelectionUpdated -= OnRawSelectionUpdated;
        }
        Title = SelectedNode?.Property?.Name ??
            (frozenSelectionAddressRange is { } range ? $"Unmapped bytes: {range}" : "Property");
        RaiseInspectorPropertiesChanged();
    }

    // Leave selected node intact for tree/hex context, but clear it from this docked inspector
    // after its value has been captured by a popup.
    public void SuppressCurrentSelection()
    {
        if (isFrozen)
        {
            return;
        }

        suppressedSelection = Main.SelectedNode;
        suppressedSelectionParses = Main.SelectionParses;
        RaiseInspectorPropertiesChanged();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isFrozen || e.PropertyName is not (nameof(MainWindowViewModel.SelectedNode) or
            nameof(MainWindowViewModel.SelectionParses) or nameof(MainWindowViewModel.SelectionAddressRange)))
        {
            return;
        }

        RaiseInspectorPropertiesChanged();
    }

    private void OnRawSelectionUpdated(RawByteSelection selection)
    {
        if (!isFrozen || frozenRawSelection is not { } frozen ||
            frozen.RegionId != selection.RegionId || frozen.StartingAddress != selection.StartingAddress ||
            frozen.Length != selection.Length)
        {
            return;
        }

        frozenRawSelection = selection;
        frozenSelectionParses = selection.Parses;
        OnPropertyChanged(nameof(SelectionParses));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RaiseInspectorPropertiesChanged()
    {
        if (!isFrozen)
        {
            UpdateFloatCapability();
        }

        OnPropertyChanged(nameof(ActiveNode));
        OnPropertyChanged(nameof(SelectionParses));
        OnPropertyChanged(nameof(SelectionAddressRange));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSelectedProperty));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void UpdateFloatCapability() => CanFloat = HasSelectedProperty;

    public void Dispose()
    {
        Main.PropertyChanged -= OnMainPropertyChanged;
        Main.RawSelectionUpdated -= OnRawSelectionUpdated;
    }
}
