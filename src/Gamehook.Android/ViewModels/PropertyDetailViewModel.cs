using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Presentation;

namespace Gamehook.Android.ViewModels;

internal sealed record DetailField(string Label, string Value);

internal sealed partial class BitItem(int index, bool isSet) : ObservableObject
{
    public string Label { get; } = $"Bit {index}";

    [ObservableProperty]
    private bool isSet = isSet;
}

/// Full-screen inspector for one property: its definition, live value and bytes, and (for
/// address-backed properties) editors for the value and the raw bytes. Edits are staged and only
/// written to the device when Save is pressed, as on desktop.
internal sealed partial class PropertyDetailViewModel : ObservableObject, IDisposable
{
    private readonly PropertyTreeNodeViewModel node;
    private readonly GamehookRouter router;

    public PropertyDetailViewModel(PropertyTreeNodeViewModel node, GamehookRouter router)
    {
        this.node = node;
        this.router = router;
        node.PropertyChanged += OnNodeChanged;

        var property = Property;
        if (property.Reference is { } reference && router.Mapper?.References.TryGetValue(reference, out var table) == true)
            ReferenceValues = table.Values.Values.Distinct(StringComparer.Ordinal).ToArray();

        Details = BuildDetails();
        LoadEditors();
    }

    public IProperty Property => node.Property!;

    public PropertyTreeNodeViewModel Node => node;

    public string Name => Property.Name;

    public string Value => node.DisplayValue ?? string.Empty;

    public string RawBytes => node.RawBytesHex;

    public string Description => Property.Description ?? string.Empty;

    public bool HasDescription => !string.IsNullOrEmpty(Property.Description);

    public string PinText => node.IsPinned ? "★ Pinned" : "☆ Pin";

    public IReadOnlyList<DetailField> Details { get; }

    public IReadOnlyList<string> ReferenceValues { get; } = [];

    // Which editor applies. Mirrors the desktop inspector: a reference dropdown wins over the
    // underlying type, and only address-backed properties are writable.
    private bool CanWrite => Property.IsWritable;
    public bool CanEditReference => CanWrite && ReferenceValues.Count > 0;
    public bool CanEditBoolean => CanWrite && !CanEditReference && Property.Type == "bool";
    public bool CanEditNumber => CanWrite && !CanEditReference && Property.Type is "int" or "uint" or "binaryCodedDecimal";
    public bool CanEditString => CanWrite && !CanEditReference && Property.Type == "string";
    public bool CanEditBitArray => CanWrite && !CanEditReference && Property.Type == "bitArray";
    public bool HasEditor => CanEditReference || CanEditBoolean || CanEditNumber || CanEditString || CanEditBitArray;
    public bool CanEditBytes => CanWrite;
    public bool IsReadOnly => !CanWrite;

    public string ReadOnlyReason => Property.StaticValue is not null
        ? "Constant value defined by the mapper; it is not read from device memory."
        : Property.Address is null
            ? "Computed by the mapper's script; it is not read from a fixed address."
            : "This property cannot be written.";

    [ObservableProperty]
    private string editText = string.Empty;

    [ObservableProperty]
    private bool editBoolean;

    [ObservableProperty]
    private ObservableCollection<BitItem> bits = [];

    [ObservableProperty]
    private string editBytes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? message;

    [ObservableProperty]
    private bool messageIsError;

    public bool HasMessage => Message is not null;

    [RelayCommand]
    private void Reset()
    {
        LoadEditors();
        Message = null;
    }

    [RelayCommand]
    private async Task SaveValueAsync()
    {
        object? value;
        if (CanEditReference || CanEditString)
        {
            value = EditText;
        }
        else if (CanEditBoolean)
        {
            value = EditBoolean;
        }
        else if (CanEditBitArray)
        {
            value = Bits.Select(bit => bit.IsSet).ToArray();
        }
        else if (decimal.TryParse(EditText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            value = number;
        }
        else
        {
            Report("Enter a number before saving.", true);
            return;
        }

        var (success, error) = await router.WritePropertyValueAsync(Property.Name, value).ConfigureAwait(true);
        Report(success ? "Saved." : error ?? "Could not write the value.", !success);
    }

    [RelayCommand]
    private async Task SaveBytesAsync()
    {
        var tokens = EditBytes.Split([' ', ',', '-', ':'], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                Report($"Byte {i} (\"{tokens[i]}\") is not valid hex.", true);
                return;
            }
        }

        if (bytes.Length != Property.Length)
        {
            Report($"This property is {Property.Length} byte(s); you entered {bytes.Length}.", true);
            return;
        }

        var (success, error) = await router.WritePropertyBytesAsync(Property, bytes).ConfigureAwait(true);
        Report(success ? "Saved." : error ?? "Could not write the bytes.", !success);
    }

    [RelayCommand]
    private void TogglePin() => node.IsPinned = !node.IsPinned;

    private void LoadEditors()
    {
        var value = Property.Value;
        EditText = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        EditBoolean = value is true;
        Bits = value is bool[] flags
            ? new ObservableCollection<BitItem>(flags.Select((isSet, index) => new BitItem(index, isSet)))
            : [];
        EditBytes = Property.RawBytesHex;
    }

    private void Report(string text, bool isError)
    {
        Message = text;
        MessageIsError = isError;
    }

    private List<DetailField> BuildDetails()
    {
        var p = Property;
        var details = new List<DetailField> { new("Type", p.Type) };
        if (p.Address is { } address) details.Add(new("Address", $"0x{address:X}"));
        if (p.Region is { } region) details.Add(new("Region", region));
        details.Add(new("Length", $"{p.Length} byte(s)"));
        if (p.Bits is { } bits) details.Add(new("Bits", bits));
        if (p.Reference is { } reference) details.Add(new("Reference", reference));
        if (p.MemoryContainer is { } container) details.Add(new("Memory container", container));
        if (p.StaticValue is { } constant) details.Add(new("Constant", constant));
        if (p.CharacterMap is { } map) details.Add(new("Character map", map));
        return details;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PropertyTreeNodeViewModel.DisplayValue): OnPropertyChanged(nameof(Value)); break;
            case nameof(PropertyTreeNodeViewModel.RawBytesHex): OnPropertyChanged(nameof(RawBytes)); break;
            case nameof(PropertyTreeNodeViewModel.IsPinned): OnPropertyChanged(nameof(PinText)); break;
        }
    }

    public void Dispose() => node.PropertyChanged -= OnNodeChanged;
}
