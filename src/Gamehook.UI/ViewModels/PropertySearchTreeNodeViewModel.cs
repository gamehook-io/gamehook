using System.ComponentModel;
using System.Windows.Input;

namespace Gamehook.UI.ViewModels;

// Separate search-only tree. It never changes the live Explorer tree or its child collections.
public sealed class PropertySearchTreeNodeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PropertyTreeNodeViewModel? source;

    public string Name { get; }
    public IReadOnlyList<PropertySearchTreeNodeViewModel> Children { get; }
    public PropertyTreeNodeViewModel? Source => source;
    public bool IsLeaf => source is not null;
    public string? DisplayValue => source?.DisplayValue;
    public bool IsPinned => source?.IsPinned == true;
    public ICommand? TogglePinCommand => source?.TogglePinCommand;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PropertySearchTreeNodeViewModel(
        string name,
        PropertyTreeNodeViewModel? source,
        IReadOnlyList<PropertySearchTreeNodeViewModel> children)
    {
        Name = name;
        this.source = source;
        Children = children;
        if (source is not null)
        {
            source.PropertyChanged += SourcePropertyChanged;
        }
    }

    public void Dispose()
    {
        if (source is not null)
        {
            source.PropertyChanged -= SourcePropertyChanged;
        }
        foreach (var child in Children)
        {
            child.Dispose();
        }
    }

    private void SourcePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PropertyTreeNodeViewModel.DisplayValue))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
        }
        else if (args.PropertyName == nameof(PropertyTreeNodeViewModel.IsPinned))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }
}
