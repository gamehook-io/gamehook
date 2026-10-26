using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Presentation;

namespace Gamehook.Android.ViewModels;

/// One list row for a property-tree node: a folder to open or a property with a live value.
/// Rows are rebuilt on navigation, so they unsubscribe from the (long-lived) node when dropped.
internal sealed partial class PropertyRow : ObservableObject, IDisposable
{
    private readonly Action<PropertyRow> open;

    public PropertyRow(PropertyTreeNodeViewModel node, string name, Action<PropertyRow> open)
    {
        Node = node;
        Name = name;
        this.open = open;
        node.PropertyChanged += OnNodeChanged;
    }

    public PropertyTreeNodeViewModel Node { get; }

    public string Name { get; }

    public bool IsFolder => Node.Children.Count > 0;

    public bool IsProperty => Node.IsLeaf;

    public string ChildCount => IsFolder ? Node.Children.Count.ToString() : string.Empty;

    public string Value => Node.DisplayValue ?? string.Empty;

    public string PinGlyph => Node.IsPinned ? "★" : "☆";

    [RelayCommand]
    private void Open() => open(this);

    [RelayCommand]
    private void TogglePin() => Node.IsPinned = !Node.IsPinned;

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PropertyTreeNodeViewModel.DisplayValue)) OnPropertyChanged(nameof(Value));
        else if (e.PropertyName == nameof(PropertyTreeNodeViewModel.IsPinned)) OnPropertyChanged(nameof(PinGlyph));
    }

    public void Dispose() => Node.PropertyChanged -= OnNodeChanged;
}
