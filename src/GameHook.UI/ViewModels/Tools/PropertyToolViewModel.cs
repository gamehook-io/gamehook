using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using GameHook.UI.ViewModels;

namespace GameHook.UI.ViewModels.Tools;

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

    // The text box the user is typing into. Deliberately not re-stamped from ActiveNode.DisplayValue
    // on every poll tick (only when the selection itself changes) - otherwise a live game value
    // would overwrite the user's in-progress edit out from under them.
    [ObservableProperty]
    private string? editValueText;

    [ObservableProperty]
    private string? editError;

    public bool HasEditError => EditError is not null;

    partial void OnEditErrorChanged(string? value) => OnPropertyChanged(nameof(HasEditError));

    private string? lastSubmittedFingerprint;
    private ObservableCollection<BitValueViewModel>? observedBitValues;

    private IReadOnlyList<string> referenceValues = [];

    public IReadOnlyList<string> ReferenceValues => referenceValues;

    public bool HasReferenceValues => referenceValues.Count > 0;

    [ObservableProperty]
    private decimal? editNumberValue;

    [ObservableProperty]
    private bool? editBooleanValue;

    [ObservableProperty]
    private ObservableCollection<BitValueViewModel> bitValues = [];

    private bool CanWriteValue => ActiveNode?.Property is { Address: not null, MemoryContainer: null };
    public bool CanEditReference => CanWriteValue && HasReferenceValues;
    public bool CanEditBoolean => CanWriteValue && !HasReferenceValues && ActiveNode?.Property?.Type == "bool";
    public bool CanEditNumber => CanWriteValue && !HasReferenceValues &&
        ActiveNode?.Property is { Type: "int" or "uint" or "binaryCodedDecimal" };
    public bool CanEditString => CanWriteValue && !HasReferenceValues && ActiveNode?.Property?.Type == "string";
    public bool CanEditBitArray => CanWriteValue && !HasReferenceValues && ActiveNode?.Property?.Type == "bitArray";
    public bool HasEditableValue => CanEditReference || CanEditBoolean || CanEditNumber || CanEditString || CanEditBitArray;

    public bool ShowReadOnlyValue => !HasEditableValue;
    public bool HasPendingValueEdit => HasEditableValue &&
        GetEditedValue(ActiveNode?.Property?.Type).Fingerprint != lastSubmittedFingerprint;

    partial void OnEditValueTextChanged(string? value) => RefreshValueEditState();
    partial void OnEditNumberValueChanged(decimal? value) => RefreshValueEditState();
    partial void OnEditBooleanValueChanged(bool? value) => RefreshValueEditState();

    partial void OnBitValuesChanged(ObservableCollection<BitValueViewModel> value)
    {
        if (observedBitValues is not null)
        {
            foreach (var bit in observedBitValues) bit.PropertyChanged -= OnBitValueChanged;
        }
        observedBitValues = value;
        foreach (var bit in value) bit.PropertyChanged += OnBitValueChanged;
        RefreshValueEditState();
    }

    // Edits are deliberately staged. Device memory changes only when user explicitly presses Save.
    public async Task SubmitEditAsync()
    {
        if (ActiveNode?.Property is not { } property || Main.Mapper is not { } mapper) return;
        var (value, fingerprint) = GetEditedValue(property.Type);
        if (value is null && property.Type is "bool" or "int" or "uint" or "binaryCodedDecimal")
        {
            EditError = "Enter a value before saving.";
            return;
        }
        if (fingerprint == lastSubmittedFingerprint) return;

        var (success, error) = await mapper.WriteAsync(property.Name, value).ConfigureAwait(true);
        EditError = success ? null : error;
        if (success)
        {
            lastSubmittedFingerprint = fingerprint;
            RefreshValueEditState();
        }
    }

    public void CancelEdit()
    {
        LoadEditorValues();
        EditError = null;
    }

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
    public string? SelectionRawBytesHex
    {
        get
        {
            var bytes = isFrozen ? frozenRawSelection?.Bytes : Main.RawSelection?.Bytes;
            return bytes is { } selected
                ? string.Join(' ', selected.Span.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)))
                : null;
        }
    }
    public bool IsFloatingSnapshot => isFrozen;
    // Property inspector wins if a transition briefly exposes both selection sources.
    public bool HasSelection => !HasSelectedProperty && SelectionParses is { Count: > 0 };
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
            frozen.Bytes.Length != selection.Bytes.Length)
        {
            return;
        }

        frozenRawSelection = selection;
        frozenSelectionParses = selection.Parses;
        OnPropertyChanged(nameof(SelectionParses));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectionRawBytesHex));
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
        OnPropertyChanged(nameof(SelectionRawBytesHex));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSelectedProperty));
        OnPropertyChanged(nameof(IsEmpty));
        UpdateReferenceValues();
        OnPropertyChanged(nameof(CanEditReference));
        OnPropertyChanged(nameof(CanEditBoolean));
        OnPropertyChanged(nameof(CanEditNumber));
        OnPropertyChanged(nameof(CanEditString));
        OnPropertyChanged(nameof(CanEditBitArray));
        OnPropertyChanged(nameof(HasEditableValue));
        OnPropertyChanged(nameof(ShowReadOnlyValue));
        LoadEditorValues();
        EditError = null;
    }

    private void UpdateReferenceValues()
    {
        referenceValues = ActiveNode?.Property?.Reference is { } reference &&
            Main.Mapper?.References.TryGetValue(reference, out var table) == true
            ? table.Values.Values.Distinct(StringComparer.Ordinal).ToArray()
            : [];
        OnPropertyChanged(nameof(ReferenceValues));
        OnPropertyChanged(nameof(HasReferenceValues));
    }

    private void LoadEditorValues()
    {
        var property = ActiveNode?.Property;
        var value = property?.Value;
        EditValueText = value?.ToString() ?? "";
        EditBooleanValue = value is bool booleanValue ? booleanValue : null;
        EditNumberValue = property?.Type is "int" or "uint" or "binaryCodedDecimal" && value is not null
            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            : null;
        BitValues = value is bool[] bits
            ? new ObservableCollection<BitValueViewModel>(bits.Select((isSet, index) => new BitValueViewModel(index, isSet)))
            : [];
        lastSubmittedFingerprint = GetEditedValue(ActiveNode?.Property?.Type).Fingerprint;
        RefreshValueEditState();
    }

    private void OnBitValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BitValueViewModel.IsSet)) RefreshValueEditState();
    }

    private void RefreshValueEditState() => OnPropertyChanged(nameof(HasPendingValueEdit));

    private (object? Value, string Fingerprint) GetEditedValue(string? type)
    {
        // A reference may decorate any underlying mapper type. Its dropdown value is always
        // the mapped display string that Property.EncodeWithReference reverse-resolves.
        if (CanEditReference) return (EditValueText ?? "", EditValueText ?? "");

        return type switch
        {
            "bool" => (EditBooleanValue, EditBooleanValue?.ToString() ?? ""),
            "int" or "uint" or "binaryCodedDecimal" => (EditNumberValue, EditNumberValue?.ToString(CultureInfo.InvariantCulture) ?? ""),
            "bitArray" => (BitValues.Select(bit => bit.IsSet).ToArray(), string.Concat(BitValues.Select(bit => bit.IsSet ? '1' : '0'))),
            _ => (EditValueText ?? "", EditValueText ?? ""),
        };
    }

    private void UpdateFloatCapability() => CanFloat = HasSelectedProperty;

    public void Dispose()
    {
        if (observedBitValues is not null)
        {
            foreach (var bit in observedBitValues) bit.PropertyChanged -= OnBitValueChanged;
        }
        Main.PropertyChanged -= OnMainPropertyChanged;
        Main.RawSelectionUpdated -= OnRawSelectionUpdated;
    }
}

public sealed partial class BitValueViewModel(int index, bool isSet) : ObservableObject
{
    public string Label { get; } = $"Bit {index}";

    [ObservableProperty]
    private bool isSet = isSet;
}
