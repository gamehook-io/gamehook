using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;
using Gamehook.Presentation;

namespace Gamehook.Android.ViewModels;

internal sealed record DriverChoice(string Name, int? DefaultPort = null);

internal sealed record MapperChoice(string FullPath, string DisplayName, bool IsCustom = false);

internal enum WorkspaceTab { Explorer, Pinned, Memory, Displays, Info }

/// One Gamehook instance on a phone screen: the load flow, then the workspace (property explorer,
/// pinned list, memory viewer, info) and the property inspector. Talks to the same GamehookRouter /
/// GamehookSession the desktop app and Web API use.
internal sealed partial class InstanceViewModel : ObservableObject, IDisposable
{
    private const int MaxSearchResults = 200;

    private readonly GamehookSession session;
    private readonly FilesystemProvider filesystemProvider;
    private readonly RetroArchConfigurationService retroArchConfiguration;
    private readonly SettingsService settings;
    private readonly IReadOnlyList<DriverRegistration> driverRegistrations;
    private readonly DispatcherTimer tick;
    private readonly List<PropertyTreeNodeViewModel> leaves = [];
    private readonly List<PropertyRow> rows = [];
    private readonly Stack<PropertyTreeNodeViewModel> folders = new();
    private IReadOnlyList<PropertyTreeNodeViewModel> tree = [];
    private Dictionary<string, IProperty>? treeSource;
    private int dirty;
    private bool disposed;
    private (string Name, string? Source)? routerDriver;

    public InstanceViewModel(
        GamehookRouter router,
        ShellViewModel shell,
        FilesystemProvider filesystemProvider,
        RetroArchConfigurationService retroArchConfiguration,
        SettingsService settings,
        IEnumerable<DriverRegistration> driverRegistrations)
    {
        Router = router;
        Shell = shell;
        session = router.Session;
        this.filesystemProvider = filesystemProvider;
        this.retroArchConfiguration = retroArchConfiguration;
        this.settings = settings;
        this.driverRegistrations = driverRegistrations.ToArray();
        Memory = new MemoryViewModel(router);
        isContinuousReadEnabled = session.IsContinuousReadEnabled;

        foreach (var registration in this.driverRegistrations.OrderBy(r => r.Name, StringComparer.Ordinal))
            Drivers.Add(new DriverChoice(registration.Name, registration.DefaultPort));

        RefreshMappers();
        if (filesystemProvider.GetLastDriver() is { } lastDriver)
            SelectedDriver = Drivers.FirstOrDefault(d => d.Name == lastDriver);
        SelectedDriver ??= Drivers.FirstOrDefault();
        if (filesystemProvider.GetLastMapperPath() is { } lastMapper)
            SelectedMapper = Mappers.FirstOrDefault(m => m.FullPath == lastMapper);

        session.Changed += OnSessionChanged;
        session.ContinuousReadChanged += OnContinuousReadChanged;
        tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        tick.Tick += (_, _) => Tick();
        tick.Start();

        // An instance added over the API may already be loading or loaded.
        Interlocked.Exchange(ref dirty, 1);
    }

    public GamehookRouter Router { get; }

    public ShellViewModel Shell { get; }

    public MemoryViewModel Memory { get; }

    public ObservableCollection<DriverChoice> Drivers { get; } = [];

    public ObservableCollection<MapperChoice> Mappers { get; } = [];

    public ObservableCollection<MapperChoice> FilteredMappers { get; } = [];

    public ObservableCollection<PropertyRow> Rows { get; } = [];

    public ObservableCollection<PropertyRow> PinnedRows { get; } = [];

    [ObservableProperty]
    private int index;

    // ---- Load flow -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveStateDriver), nameof(IsNetworkDriver), nameof(PortHint))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private DriverChoice? selectedDriver;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private MapperChoice? selectedMapper;

    [ObservableProperty]
    private string mapperFilter = string.Empty;

    // Cleared means "the driver's default port".
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private string portText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyPropertyChangedFor(nameof(SaveStateName))]
    private string? saveStatePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsConnected), nameof(IsLoadScreenVisible), nameof(IsWorkspaceVisible), nameof(StatusIsError), nameof(HeaderTitle), nameof(ShowTabs))]
    private string status = "Choose a driver and mapper to load.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadScreenVisible), nameof(IsWorkspaceVisible), nameof(Title), nameof(HeaderTitle), nameof(ShowTabs))]
    private bool isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsLoadScreenVisible), nameof(IsWorkspaceVisible), nameof(Title), nameof(HeaderTitle), nameof(ShowTabs), nameof(GameName))]
    private IMapper? mapper;

    [ObservableProperty]
    private string? dataWarning;

    [ObservableProperty]
    private string? connectionWarning;

    [ObservableProperty]
    private bool isContinuousReadEnabled;

    [ObservableProperty]
    private bool isReadingNow;

    public bool IsSaveStateDriver => SelectedDriver?.Name == SaveStateDriver.Name;
    public bool IsNetworkDriver => SelectedDriver?.DefaultPort is not null;
    public string PortHint => SelectedDriver?.DefaultPort is { } port ? $"Port (default {port})" : "Port";
    public string SaveStateName => SaveStatePath is { } path ? Path.GetFileName(path) : "No file chosen";
    public bool HasError => Status.StartsWith("Error:", StringComparison.Ordinal);
    public bool StatusIsError => HasError;
    public bool IsConnected => Mapper is not null && !HasError;
    public bool IsLoadScreenVisible => !IsConnected && !IsConnecting;
    public bool IsWorkspaceVisible => IsConnected && !IsConnecting;
    public string GameName => Mapper?.GameName ?? string.Empty;
    public bool HasMapperChoices => Mappers.Count > 0;
    public bool NoMapperChoices => Mappers.Count == 0;

    public bool ShowTabs => IsWorkspaceVisible && !HasDetail && !IsFullscreenActive;
    public bool ShowTopBar => !IsFullscreenActive;
    private bool IsFullscreenActive => IsDisplaysFullscreen && IsDisplaysTab;
    public bool ShowBack => HasDetail || (IsWorkspaceVisible && IsExplorerTab && InFolder);
    public string HeaderTitle => Detail?.Name ?? Title;

    public string Title => IsConnecting ? "Connecting…" : IsConnected ? GameName : "Load a mapper";

    partial void OnMapperFilterChanged(string value) => ApplyMapperFilter();

    partial void OnSelectedDriverChanged(DriverChoice? value)
    {
        if (value is null) return;
        var remembered = filesystemProvider.GetLastDriverPort(value.Name);
        PortText = value.DefaultPort is null ? string.Empty : (remembered ?? value.DefaultPort).ToString()!;
    }

    private int? EffectivePort
    {
        get
        {
            if (!IsNetworkDriver) return null;
            if (string.IsNullOrWhiteSpace(PortText)) return SelectedDriver!.DefaultPort;
            return int.TryParse(PortText.Trim(), out var port) ? port : -1;
        }
    }

    private bool CanLoad() => SelectedDriver is not null
        && SelectedMapper is not null
        && (!IsSaveStateDriver || SaveStatePath is not null)
        && (EffectivePort is not { } port || NetworkEndpoint.IsValidPort(port));

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (SelectedDriver is null || SelectedMapper is null) return;

        var (driverName, driverSource) = LoadDriver;
        // Stop before probing RetroArch: another instance may already own this endpoint.
        if (Router.FindDriverConflict(driverName, driverSource) is { } conflict)
        {
            Status = $"Error: {conflict}";
            return;
        }

        if (EffectivePort is { } chosenPort) filesystemProvider.RememberLastDriverPort(SelectedDriver.Name, chosenPort);

        if (SelectedDriver.Name == RetroArchDriver.Name)
        {
            var port = EffectivePort ?? RetroArchDriver.DefaultPort;
            if (!await retroArchConfiguration.NetworkCommandsAvailableAsync(port).ConfigureAwait(true))
            {
                Status = $"Error: RetroArch is not answering on port {port}. Start RetroArch, turn on Settings > Network > Network Commands, check the port matches, then try again.";
                return;
            }
        }

        ClearWorkspace();
        var (success, error) = await Router.LoadAsync(SelectedMapper.FullPath, driverName, driverSource).ConfigureAwait(true);
        SyncSelectedDriver();
        // A refused load never reaches the session, so its error isn't in session.Status.
        if (!success && error is not null && error != session.Status) Status = $"Error: {error}";
    }

    private (string Name, string? Source) LoadDriver
    {
        get
        {
            var name = SelectedDriver!.Name;
            if (IsSaveStateDriver) return (name, SaveStatePath);
            if (EffectivePort is not { } port) return (name, null);

            // Reuse the router's exact source if it already points at this port (it may name a host).
            return routerDriver is { } current && current.Name == name
                && DriverResources.GetPort(driverRegistrations, current.Name, current.Source) == port
                ? current
                : (name, NetworkEndpoint.Format(null, port));
        }
    }

    // Prefills the load screen with the driver the router is actually on (it may have been set over the API).
    private void SyncSelectedDriver()
    {
        if (Router.DriverName is not { } name) return;
        var current = (name, Router.DriverSourcePath);
        if (routerDriver == current) return;
        routerDriver = current;

        SelectedDriver = Drivers.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) ?? SelectedDriver;
        if (DriverResources.GetPort(driverRegistrations, name, current.Item2) is { } port) PortText = port.ToString();
        else if (IsSaveStateDriver) SaveStatePath = current.Item2;
    }

    [RelayCommand]
    private async Task PickSaveStateAsync()
    {
        var directory = Path.Combine(filesystemProvider.GamehookProfileDirectory, "save-states");
        var picked = await FilePicker.PickIntoAsync("Choose a save state", false, directory);
        if (picked.Count > 0) SaveStatePath = picked[0];
    }

    [RelayCommand]
    private async Task ImportMappersAsync()
    {
        var directory = filesystemProvider.GetPrimaryCustomMapperDirectory();
        if (directory is null)
        {
            Status = "Error: This build has no user mapper folder.";
            return;
        }

        // A mapper is an .xml plus its companion .js; pick both together.
        var picked = await FilePicker.PickIntoAsync("Choose mapper .xml and .js files", true, directory);
        if (picked.Count == 0) return;

        RefreshMappers();
        var xml = picked.FirstOrDefault(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        if (xml is not null) SelectedMapper = Mappers.FirstOrDefault(m => m.FullPath == Path.GetFullPath(xml));
    }

    [RelayCommand]
    private void RefreshMappers()
    {
        Mappers.Clear();
        try
        {
            // Official mappers first, then the user's own.
            foreach (var entry in filesystemProvider.GetMappers()
                         .OrderBy(entry => entry.Value.IsCustom)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var name = entry.Value.IsCustom ? entry.Key[FilesystemProvider.CustomMapperKeyPrefix.Length..] : entry.Key;
                Mappers.Add(new MapperChoice(entry.Value.Path, name, entry.Value.IsCustom));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Error: Could not read the mapper directory. {ex.Message}";
        }

        var keep = SelectedMapper?.FullPath;
        ApplyMapperFilter();
        if (keep is not null) SelectedMapper = Mappers.FirstOrDefault(m => m.FullPath == keep);
        OnPropertyChanged(nameof(HasMapperChoices));
        OnPropertyChanged(nameof(NoMapperChoices));
    }

    private void ApplyMapperFilter()
    {
        var query = MapperFilter.Trim();
        FilteredMappers.Clear();
        foreach (var choice in Mappers)
        {
            if (query.Length == 0 || choice.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                FilteredMappers.Add(choice);
        }
    }

    // ---- Session state ---------------------------------------------------------------------

    // Changed fires on the poll thread, often. Just note it; the UI-thread timer applies it at
    // 10 Hz, which is plenty for a phone screen and keeps rendering cheap.
    private void OnSessionChanged() => Interlocked.Exchange(ref dirty, 1);

    private void OnContinuousReadChanged(bool enabled)
    {
        Dispatcher.UIThread.Post(() => IsContinuousReadEnabled = session.IsContinuousReadEnabled);
        Interlocked.Exchange(ref dirty, 1);
    }

    private void Tick()
    {
        if (disposed) return;
        if (Interlocked.Exchange(ref dirty, 0) == 1) ProcessSessionChanged();
        if (IsWorkspaceVisible && SelectedTab == WorkspaceTab.Memory && IsContinuousReadEnabled) Memory.Tick();
    }

    private void ProcessSessionChanged()
    {
        Mapper = session.Mapper;
        IsConnecting = session.IsConnecting;
        Status = session.Status;
        DataWarning = session.DataWarning;
        ConnectionWarning = session.ConnectionWarning;
        IsContinuousReadEnabled = session.IsContinuousReadEnabled;
        SyncSelectedDriver();
        RefreshFooter();

        if (session.Mapper is not { } active) return;

        if (!ReferenceEquals(treeSource, active.Properties))
        {
            treeSource = active.Properties;
            RebuildTree(active);
            Memory.SyncRegions();
        }

        RefreshVisibleValues();
    }

    [RelayCommand]
    private async Task ReadNowAsync()
    {
        IsReadingNow = true;
        try
        {
            await session.ReadOnDemandAsync().ConfigureAwait(true);
            if (SelectedTab == WorkspaceTab.Memory) await Memory.RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsReadingNow = false;
        }
    }

    [RelayCommand]
    private void ToggleContinuousRead() => settings.Update(!session.IsContinuousReadEnabled);

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (Router.MapperPath is not { } mapperPath || Router.DriverName is not { } driverName) return;
        ClearWorkspace();
        await Router.LoadAsync(mapperPath, driverName, Router.DriverSourcePath).ConfigureAwait(true);
    }

    /// Back to the load screen to pick another mapper or driver.
    [RelayCommand]
    private void Unload()
    {
        ClearWorkspace();
        Router.Unload();
        Interlocked.Exchange(ref dirty, 1);
    }

    private void ClearWorkspace()
    {
        CloseDetail();
        treeSource = null;
        ResetTree();
        SelectedTab = WorkspaceTab.Explorer;
    }

    // ---- Footer ----------------------------------------------------------------------------

    [ObservableProperty]
    private string footerText = string.Empty;

    [ObservableProperty]
    private string footerDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFooterDetail))]
    private int readFailures;

    [ObservableProperty]
    private bool connectionRefused;

    public bool HasFooterDetail => FooterDetail.Length > 0;

    // 0 = healthy, 1 = warning, 2 = error: bound to a colored dot.
    public IBrush HealthBrush => ConnectionRefused || ReadFailures >= 5 ? ErrorBrush : ReadFailures > 0 ? WarningBrush : HealthyBrush;

    private static readonly IBrush HealthyBrush = new SolidColorBrush(Color.Parse("#47C975"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E0A339"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E95D5D"));

    private void RefreshFooter()
    {
        ReadFailures = Mapper?.ConsecutiveReadFailures ?? 0;
        ConnectionRefused = Mapper?.HasConnectionRefusal ?? false;
        OnPropertyChanged(nameof(HealthBrush));
        FooterText = Mapper is null
            ? string.Empty
            : $"{Router.DriverName}  ·  {Mapper.LastReadMetrics.Total.TotalMilliseconds:0.0} ms";

        if (Mapper is not { ConsecutiveReadFailures: > 0 } active)
        {
            FooterDetail = string.Empty;
            OnPropertyChanged(nameof(HasFooterDetail));
            return;
        }

        var driver = Router.DriverName ?? "Driver";
        FooterDetail = active.HasConnectionRefusal ? $"{driver} rejected the connection. Check it is running and, for RetroArch, that Network Commands are on."
            : active.ConsecutiveReadFailures >= 5 ? $"{driver} is not responding. Showing the last successful values."
            : $"{driver} read delayed. Showing the last successful values.";
        FooterDetail += $"\nFailed reads in a row: {active.ConsecutiveReadFailures}\nLast error: {active.LastReadFailureMessage}";
        OnPropertyChanged(nameof(HasFooterDetail));
    }

    // ---- Workspace tabs & explorer ---------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorerTab), nameof(IsPinnedTab), nameof(IsMemoryTab), nameof(IsDisplaysTab), nameof(IsInfoTab), nameof(ShowBack), nameof(ShowTabs), nameof(ShowTopBar))]
    private WorkspaceTab selectedTab;

    public bool IsExplorerTab => SelectedTab == WorkspaceTab.Explorer;
    public bool IsPinnedTab => SelectedTab == WorkspaceTab.Pinned;
    public bool IsMemoryTab => SelectedTab == WorkspaceTab.Memory;
    public bool IsDisplaysTab => SelectedTab == WorkspaceTab.Displays;
    public bool IsInfoTab => SelectedTab == WorkspaceTab.Info;

    /// Displays tab only: hides the app's top bar, tab bar and the browser header so the browser fills
    /// the shell. No on-screen exit button (a native WebView would cover it anyway): Android back leaves.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTabs), nameof(ShowTopBar))]
    private bool isDisplaysFullscreen;

    [RelayCommand]
    private void ToggleDisplaysFullscreen() => IsDisplaysFullscreen = !IsDisplaysFullscreen;

    partial void OnSelectedTabChanged(WorkspaceTab value)
    {
        if (value != WorkspaceTab.Displays) IsDisplaysFullscreen = false;
        Memory.IsActive = value == WorkspaceTab.Memory;
        if (value == WorkspaceTab.Memory) _ = Memory.RefreshAsync();
    }

    [RelayCommand]
    private void ReloadDisplays() => DisplayBrowser.Reload();

    [RelayCommand]
    private void DisplaysBack() => DisplayBrowser.GoBack();

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = Enum.Parse<WorkspaceTab>(tab);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    private string searchText = string.Empty;

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value) => RebuildRows();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProperties))]
    private int propertyCount;

    public bool HasProperties => PropertyCount > 0;

    /// "player › team" style trail for the open folder.
    public string FolderPath => string.Join(" › ", folders.Reverse().Select(folder => folder.Name));

    public bool InFolder => folders.Count > 0;

    public bool HasPinned => PinnedRows.Count > 0;

    public bool NoPinned => PinnedRows.Count == 0;

    private void RebuildTree(IMapper active)
    {
        ResetTree();
        tree = PropertyTreeNodeViewModel.Build(active.Properties.Values);
        foreach (var leaf in PropertyTreeNodeViewModel.EnumerateLeaves(tree))
        {
            leaves.Add(leaf);
            leaf.PinnedChanged += OnNodePinnedChanged;
        }

        PropertyCount = leaves.Count;
        RebuildRows();
    }

    private void ResetTree()
    {
        foreach (var leaf in leaves) leaf.PinnedChanged -= OnNodePinnedChanged;
        leaves.Clear();
        folders.Clear();
        tree = [];
        foreach (var row in PinnedRows) row.Dispose();
        PinnedRows.Clear();
        SearchText = string.Empty;
        RebuildRows();
        PropertyCount = 0;
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(NoPinned));
    }

    private void RebuildRows()
    {
        foreach (var row in rows) row.Dispose();
        rows.Clear();
        Rows.Clear();

        IEnumerable<PropertyRow> next;
        if (HasSearch)
        {
            // Spaces stand in for the dots of a property path, as on desktop.
            var query = PropertySearch.NormalizeQuery(SearchText);
            next = leaves
                .Where(leaf => leaf.Property!.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSearchResults)
                .Select(leaf => new PropertyRow(leaf, leaf.Property!.Name, OpenRow));
        }
        else
        {
            var source = folders.Count == 0 ? tree : folders.Peek().Children;
            next = source.Select(node => new PropertyRow(node, node.Name, OpenRow));
        }

        foreach (var row in next)
        {
            row.Node.RefreshDisplayValue();
            rows.Add(row);
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(InFolder));
        OnPropertyChanged(nameof(ShowBack));
    }

    private void OpenRow(PropertyRow row)
    {
        if (row.IsFolder)
        {
            folders.Push(row.Node);
            RebuildRows();
        }
        else if (row.IsProperty)
        {
            OpenDetail(row.Node);
        }
    }

    private void OnNodePinnedChanged(PropertyTreeNodeViewModel node, bool pinned)
    {
        if (pinned)
        {
            var row = new PropertyRow(node, node.Property!.Name, OpenRow);
            node.RefreshDisplayValue();
            PinnedRows.Add(row);
        }
        else if (PinnedRows.FirstOrDefault(row => ReferenceEquals(row.Node, node)) is { } existing)
        {
            PinnedRows.Remove(existing);
            existing.Dispose();
        }

        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(NoPinned));
    }

    // Only what is on screen is refreshed; a folder's values are caught up when it is opened.
    private void RefreshVisibleValues()
    {
        foreach (var row in rows) row.Node.RefreshDisplayValue();
        foreach (var row in PinnedRows) row.Node.RefreshDisplayValue();
        Detail?.Node.RefreshDisplayValue();
    }

    // ---- Property inspector ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowBack), nameof(ShowTabs), nameof(HeaderTitle))]
    private PropertyDetailViewModel? detail;

    public bool HasDetail => Detail is not null;

    private void OpenDetail(PropertyTreeNodeViewModel node) => Detail = new PropertyDetailViewModel(node, Router);

    [RelayCommand]
    public void CloseDetail()
    {
        Detail?.Dispose();
        Detail = null;
    }

    /// Hardware/gesture back: peel one layer. Returns false when there is nothing left to peel.
    public bool GoBack()
    {
        if (IsDisplaysFullscreen) { IsDisplaysFullscreen = false; return true; }
        if (HasDetail) { CloseDetail(); return true; }
        if (!IsWorkspaceVisible) return false;
        if (HasSearch) { SearchText = string.Empty; return true; }
        if (IsExplorerTab && folders.Count > 0) { GoUp(); return true; }
        if (IsDisplaysTab && DisplayBrowser.CanGoBack) { DisplayBrowser.GoBack(); return true; }
        if (!IsExplorerTab) { SelectedTab = WorkspaceTab.Explorer; return true; }
        return false;
    }

    [RelayCommand]
    private void Back() => GoBack();

    [RelayCommand]
    private void GoUp()
    {
        if (folders.Count == 0) return;
        folders.Pop();
        RebuildRows();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tick.Stop();
        session.Changed -= OnSessionChanged;
        session.ContinuousReadChanged -= OnContinuousReadChanged;
        CloseDetail();
        ResetTree();
    }
}
