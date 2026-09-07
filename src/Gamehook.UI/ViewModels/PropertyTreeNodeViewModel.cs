using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Domain.Interface;

namespace Gamehook.UI.ViewModels;

// Leaf/folder node in the property tree. Built by splitting each dotted IProperty.Name
// (e.g. "player.team.0.level") the same way the mapper XML/JS already segments paths.
public sealed partial class PropertyTreeNodeViewModel(string name, IProperty? property = null) : ObservableObject
{
    private static readonly IComparer<string> PropertyNameComparer = new NumericPathComparer();

    public string Name { get; } = name;
    public IProperty? Property { get; } = property;
    public bool IsLeaf => Property is not null;
    public string? Description => Property?.Description;
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    // Address-less leaves are never backed by device memory - either a fixed constant (StaticValue)
    // or a value a mapper script computes/assigns at read time. Neither can be located in the hex
    // viewer or written to raw, so the tree flags them instead of leaving that unexplained.
    public bool HasNoAddress => IsLeaf && Property!.Address is null;
    public string? NoAddressTooltip => !HasNoAddress ? null : Property!.StaticValue is not null
        ? "Constant value defined by the mapper, not read from device memory."
        : "Computed by the mapper's script, not read from a fixed address.";

    // The info bubble icon is shared: it appears for either a mapper-authored Description or a
    // no-address note (or both stacked), rather than each getting its own icon in the row.
    public bool HasInfoBubble => HasDescription || HasNoAddress;
    public ObservableCollection<PropertyTreeNodeViewModel> Children { get; } = [];

    // Bound TwoWay to TreeViewItem.IsExpanded so a single click anywhere on the row
    // (wired up in MainWindow's code-behind) can toggle it, not just the tiny chevron glyph.
    [ObservableProperty]
    private bool isExpanded;

    // Leaf values are re-stamped every poll. Folder rows intentionally have no value.
    [ObservableProperty]
    private string? displayValue;

    // Keep volatile property data in the UI model. IProperty is deliberately not observable,
    // so bindings to it would otherwise retain their initial read value.
    [ObservableProperty]
    private string rawBytesHex = string.Empty;

    // Watched from the Pinned tab. Only leaves are pinnable; MainWindowViewModel listens
    // for this to keep its Pinned collection in sync with the tree.
    [ObservableProperty]
    private bool isPinned;

    public event Action<PropertyTreeNodeViewModel, bool>? PinnedChanged;

    partial void OnIsPinnedChanged(bool value) => PinnedChanged?.Invoke(this, value);

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    public static ObservableCollection<PropertyTreeNodeViewModel> Build(IEnumerable<IProperty> properties)
    {
        var roots = new ObservableCollection<PropertyTreeNodeViewModel>();
        var index = new Dictionary<string, PropertyTreeNodeViewModel>(StringComparer.Ordinal);

        foreach (var property in properties.OrderBy(p => p.Name, PropertyNameComparer))
        {
            var segments = property.Name.Split('.');
            var siblings = roots;
            var path = "";

            for (var i = 0; i < segments.Length; i++)
            {
                path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
                var isLeaf = i == segments.Length - 1;

                if (!index.TryGetValue(path, out var node))
                {
                    node = new PropertyTreeNodeViewModel(segments[i], isLeaf ? property : null);
                    index[path] = node;
                    siblings.Add(node);
                }

                siblings = node.Children;
            }
        }

        return roots;
    }

    // Depth-first: every leaf under `node`, so the tree can push fresh values into it every poll tick.
    public static IEnumerable<PropertyTreeNodeViewModel> EnumerateLeaves(IEnumerable<PropertyTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsLeaf)
            {
                yield return node;
            }
            foreach (var leaf in EnumerateLeaves(node.Children))
            {
                yield return leaf;
            }
        }
    }

    // Called for every leaf on every poll, so both fast paths are comparisons rather than formats:
    // a property's value and bytes are unchanged on the vast majority of frames, and formatting
    // either one first only to discover that would allocate per leaf per frame.
    private object? lastFormattedValue;
    private bool hasFormattedValue;
    private byte[] lastFormattedBytes = [];

    public void RefreshDisplayValue()
    {
        var current = Property?.Value;
        if (!hasFormattedValue || !Equals(lastFormattedValue, current))
        {
            lastFormattedValue = current;
            hasFormattedValue = true;
            var value = Property?.Type == "bitArray" ? "" : FormatValue(current);
            if (!string.Equals(DisplayValue, value, StringComparison.Ordinal))
            {
                DisplayValue = value;
            }
        }

        // Property.RawBytesHex builds a fresh string every call, so the bytes are compared here
        // instead - the copy only happens on the frames where they actually changed.
        var bytes = Property is { } property ? property.Bytes.Span : default;
        if (!bytes.SequenceEqual(lastFormattedBytes))
        {
            lastFormattedBytes = bytes.ToArray();
            RawBytesHex = Property?.RawBytesHex ?? string.Empty;
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private sealed class NumericPathComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var xSegments = x.Split('.');
            var ySegments = y.Split('.');
            var segmentCount = Math.Min(xSegments.Length, ySegments.Length);

            for (var i = 0; i < segmentCount; i++)
            {
                var comparison = CompareSegments(xSegments[i], ySegments[i]);
                if (comparison != 0) return comparison;
            }

            return xSegments.Length.CompareTo(ySegments.Length);
        }

        private static int CompareSegments(string x, string y)
        {
            var xNumeric = IsNumeric(x);
            var yNumeric = IsNumeric(y);
            if (xNumeric && yNumeric)
            {
                var xTrimmed = x.TrimStart('0');
                var yTrimmed = y.TrimStart('0');
                if (xTrimmed.Length != yTrimmed.Length)
                    return xTrimmed.Length.CompareTo(yTrimmed.Length);

                var comparison = StringComparer.Ordinal.Compare(xTrimmed, yTrimmed);
                if (comparison != 0) return comparison;
            }

            return StringComparer.Ordinal.Compare(x, y);
        }

        private static bool IsNumeric(string value) =>
            value.Length > 0 && value.All(char.IsAsciiDigit);
    }
}
