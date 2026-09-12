using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Gamehook.UI.ViewModels;

namespace Gamehook.UI.ViewModels.Tools;

public sealed partial class PropertiesToolViewModel : Tool
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);
    private readonly DispatcherTimer searchTimer;

    public MainWindowViewModel Main { get; }

    [ObservableProperty]
    private string searchText = string.Empty;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public PropertiesToolViewModel(MainWindowViewModel main)
    {
        Main = main;
        Id = "Properties";
        Title = "Explorer";
        searchTimer = new DispatcherTimer { Interval = SearchDelay };
        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            Main.ApplyExplorerSearch(SearchText);
        };
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        searchTimer.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            Main.ApplyExplorerSearch(value);
        }
        else
        {
            searchTimer.Start();
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;
}
