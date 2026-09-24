using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;
using Gamehook.UI.ViewModels.Tools;

namespace Gamehook.UI.ViewModels;

// One tab in the main window: the load screen and workspace for a single GamehookInstances entry.
public sealed partial class InstanceViewModel : ViewModelBase, IDisposable
{
    private static readonly IBrush HealthyStatusBrush = new SolidColorBrush(Color.Parse("#47C975"));
    private static readonly IBrush WarningStatusBrush = new SolidColorBrush(Color.Parse("#E0A339"));
    private static readonly IBrush ErrorStatusBrush = new SolidColorBrush(Color.Parse("#E95D5D"));

    private readonly GamehookSession session;
    private readonly GamehookRouter router;
    private readonly FilesystemProvider filesystemProvider;
    private readonly SettingsService settings;
    private readonly RetroArchConfigurationService retroArchConfiguration;
    private readonly DockFactory dockFactory;
    private readonly HexViewerToolViewModel hexViewer;
    private Dictionary<string, IProperty>? treeSourceProperties;
    private readonly List<PropertyTreeNodeViewModel> treeLeaves = [];
    private readonly Dictionary<string, PropertyTreeNodeViewModel> propertySearchIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? explorerSearchCancellation;
    private int explorerSearchVersion;
    private int sessionUpdateQueued;
    private bool disposed;
    private RawByteSelection? rawByteSelection;

    // Position in GamehookInstances - the {index} in /instances/{index} API routes. Kept current by
    // MainWindowViewModel as instances are added and removed.
    [ObservableProperty]
    private int index;

    // The tab shows its index only when there is more than one tab to tell apart.
    [ObservableProperty]
    private bool showIndex;

    // Only the selected tab is on screen; hidden tabs keep their session and tree up to date but
    // skip hex viewer driver reads.
    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private MapperChoice? selectedMapper;

    // The driver the router is on (from a load here or over the API). The load screen is prefilled
    // from it whenever it changes, and a Load that keeps the same driver and port reuses its exact
    // source, which may name a host the port box can't express.
    private (string Name, string? Source)? routerDriver;
    private bool syncingSelectedDriver;

    [ObservableProperty]
    private DriverChoice? selectedDriver;

    [ObservableProperty]
    private string? selectedSaveStatePath;

    // Port for network drivers (RetroArch, SuperShuckie). Prefilled with the port last used for
    // the selected driver, else its default; cleared means "use the default".
    [ObservableProperty]
    private decimal? driverPort;

    [ObservableProperty]
    private string status = "Choose a driver and mapper to load.";

    // True from the moment Load is clicked until the first read against the new mapper either
    // succeeds or fails - the workspace only has something real to show once that first read
    // lands, so it stays hidden behind a spinner rather than flashing an empty tree.
    [ObservableProperty]
    private bool isConnecting;

    [ObservableProperty]
    private IMapper? mapper;

    [ObservableProperty]
    private PropertyTreeNodeViewModel? selectedNode;

    [ObservableProperty]
    private string? selectedRegionId;

    [ObservableProperty]
    private IReadOnlyList<SelectionParseViewModel>? selectionParses;

    [ObservableProperty]
    private string? selectionAddressRange;

    // Set once, right after a mapper loads, when the driver came back without data for a region
    // some property needs (e.g. RetroArch has no memory map for it). Those properties read as null
    // rather than failing the whole connection - this just surfaces that so it isn't silently confusing.
    [ObservableProperty]
    private string? dataWarning;

    // Synced from Mapper.ConnectionWarning on every tick (unlike DataWarning, which is only
    // computed once at load) - it's a live status, so it comes back on its own if drops resume
    // after being dismissed.
    [ObservableProperty]
    private string? connectionWarning;

    // Mirrors GamehookSession.IsContinuousReadEnabled. While false nothing polls the driver: the
    // workspace stays usable but shows the values from the last read, and a banner offers "Read"
    // (ReadNowCommand) - an API ?read=true read updates it too. Writes stay refused (GamehookRouter).
    [ObservableProperty]
    private bool isContinuousReadEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadNowCommand))]
    private bool isReadingNow;

    public bool IsReadOnDemandBannerVisible => IsWorkspaceVisible && !IsContinuousReadEnabled;

    partial void OnIsContinuousReadEnabledChanged(bool value) => OnPropertyChanged(nameof(IsReadOnDemandBannerVisible));

    private bool CanReadNow() => Mapper is not null && !IsReadingNow;

    // One driver read, plus the hex viewer when it's showing, for continuous read mode off.
    [RelayCommand(CanExecute = nameof(CanReadNow))]
    private async Task ReadNowAsync()
    {
        IsReadingNow = true;
        try
        {
            await session.ReadOnDemandAsync().ConfigureAwait(true);
            if (hexViewer.IsActive)
            {
                await hexViewer.RefreshAsync(force: true).ConfigureAwait(true);
            }
        }
        finally
        {
            IsReadingNow = false;
        }
    }

    public ObservableCollection<MapperChoice> Mappers { get; } = [];
    public ObservableCollection<DriverChoice> Drivers { get; } = [];
    public ObservableCollection<PropertyTreeNodeViewModel> Tree { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<PropertySearchTreeNodeViewModel> searchTree = [];

    [ObservableProperty]
    private PropertySearchTreeNodeViewModel? selectedSearchNode;
    public ObservableCollection<string> RegionIds { get; } = [];
    public PropertiesToolViewModel PropertiesPanel { get; }
    public WorkspaceToolViewModel WorkspacePanel { get; }
    public HexViewerToolViewModel HexViewerPanel => hexViewer;
    public PropertyToolViewModel PropertyPanel { get; private set; } = null!;
    public PinnedToolViewModel PinnedPanel { get; }

    // Cross-panel mediation: the tree, hex viewer, and pinned list each live in their own
    // dockable Tool view now, so they can no longer reach each other's named elements directly.
    public event Action<PropertyTreeNodeViewModel>? CenterTreeItemRequested;
    public event Action? ScrollHexToSelectionRequested;
    public event Action<RawByteSelection>? RawSelectionUpdated;
    public event Func<IReadOnlyList<string>, Task<bool>>? RetroArchNetworkCommandsUnavailable;
    internal void OnHexRegionLoaded() => ScrollHexToSelectionRequested?.Invoke();

    public IRootDock Layout { get; }

    // Writes go through the router so continuous read mode's write block applies to the UI too.
    public GamehookRouter Router => router;

    // Holds the same node instances as Tree (not copies), so a poll tick that refreshes a
    // leaf's DisplayValue in Tree is already reflected here via shared bindings.
    public ObservableCollection<PropertyTreeNodeViewModel> Pinned { get; } = [];

    // Mirrors Pinned's underlying properties for the hex viewer, which only knows about
    // IProperty (domain), not the tree's ViewModel wrapper.
    public ObservableCollection<IProperty> PinnedProperties { get; } = [];

    // Compiled bindings only negate `bool`, so the empty-state visuals in MainWindow.axaml
    // bind to these instead of poking at Tree.Count / SelectedNode.Property directly.
    public bool HasProperties => Tree.Count > 0;
    // A mapped property takes precedence over any stale raw-byte parse state. This keeps every
    // consumer from displaying both inspectors while a selection changes between panels.
    public bool HasSelection => !HasSelectedProperty && SelectionParses is { Count: > 0 };
    public bool HasSelectedProperty => SelectedNode?.Property is not null;
    public bool IsPropertyPanelEmpty => !HasSelection && !HasSelectedProperty;
    public bool IsLoaded => Mapper is not null;
    public bool HasError => Status.StartsWith("Error:", StringComparison.Ordinal);
    public bool IsConnected => Mapper is not null && !HasError;
    public bool IsLoadScreenVisible => !IsConnected && !IsConnecting;
    public bool IsWorkspaceVisible => IsConnected && !IsConnecting;
    public bool IsSaveStateDriver => SelectedDriver?.Name == SaveStateDriver.Name;
    public bool IsNetworkDriver => SelectedDriver?.DefaultPort is not null;
    public string DriverPortHint => SelectedDriver?.DefaultPort is { } defaultPort
        ? $"Default: {defaultPort}"
        : string.Empty;
    private int? EffectiveDriverPort => IsNetworkDriver ? (int?)DriverPort ?? SelectedDriver!.DefaultPort : null;
    public bool CanSelectMapper => SelectedDriver is not null
        && (!IsSaveStateDriver || SelectedSaveStatePath is not null);
    public bool IsIdle => Mapper is null && !HasError;
    public bool HasPinnedProperties => Pinned.Count > 0;
    public bool HasDataWarning => DataWarning is not null;
    public bool HasConnectionWarning => ConnectionWarning is not null;
    internal RawByteSelection? RawSelection => rawByteSelection;
    public string WorkspaceName => SelectedMapper is null
        ? "No workspace loaded"
        : Path.GetFileNameWithoutExtension(SelectedMapper.FullPath).Replace('_', ' ');
    public bool HasReadingStatus => IsConnected;
    public IBrush FooterStatusBrush => Mapper?.ConsecutiveReadFailures switch
    {
        _ when Mapper?.HasConnectionRefusal is true => ErrorStatusBrush,
        >= 5 => ErrorStatusBrush,
        > 0 => WarningStatusBrush,
        _ => HealthyStatusBrush,
    };
    public string ReadingStatusText => IsConnected
        ? SelectedDriver?.Name ?? string.Empty
        : Status;
    public string TabTitle => Mapper?.GameName ?? (IsConnecting ? "Connecting…" : "New instance");
    public string WindowTitle => Mapper is null
        ? "Gamehook"
        : SelectedMapper?.IsCustom is true
            ? $"Gamehook - {Mapper.GameName} (User Mapper)"
            : $"Gamehook - {Mapper.GameName}";
    public string FooterDriverTime => FormatReadTime(Mapper?.LastReadMetrics.Driver);
    public string FooterPropertyTranslationTime => FormatReadTime(Mapper?.LastReadMetrics.PropertyTranslation);
    public string FooterInlineCalculationsTime => FormatReadTime(Mapper?.LastReadMetrics.InlineCalculations);
    public string FooterPostprocessorTime => FormatReadTime(Mapper?.LastReadMetrics.Postprocessor);
    public string FooterTotalTime => FormatReadTime(Mapper?.LastReadMetrics.Total);
    public bool HasFooterConnectionDetails => !string.IsNullOrEmpty(FooterConnectionDetails);
    public string FooterConnectionDetails => Mapper?.ConsecutiveReadFailures switch
    {
        0 or null => string.Empty,
        _ when Mapper.HasConnectionRefusal =>
            $"RetroArch rejected connection\nFailed reads in a row: {Mapper.ConsecutiveReadFailures}\n" +
            $"Check RetroArch is running and Network Commands are enabled.\nLast error: {Mapper.LastReadFailureMessage}",
        >= 5 =>
            $"RetroArch not responding\nFailed reads in a row: {Mapper.ConsecutiveReadFailures}\n" +
            $"Showing last successful values. Check RetroArch and Network Commands.\nLast error: {Mapper.LastReadFailureMessage}",
        _ =>
            $"RetroArch read delayed\nFailed reads in a row: {Mapper.ConsecutiveReadFailures}\n" +
            $"Showing last successful values. Last error: {Mapper.LastReadFailureMessage}",
    };

    private static string FormatReadTime(TimeSpan? elapsed) => $"{elapsed?.TotalMilliseconds ?? 0:0.00} ms";

    private void OnFooterMetricsChanged()
    {
        OnPropertyChanged(nameof(FooterDriverTime));
        OnPropertyChanged(nameof(FooterPropertyTranslationTime));
        OnPropertyChanged(nameof(FooterInlineCalculationsTime));
        OnPropertyChanged(nameof(FooterPostprocessorTime));
        OnPropertyChanged(nameof(FooterTotalTime));
        OnPropertyChanged(nameof(HasFooterConnectionDetails));
        OnPropertyChanged(nameof(FooterConnectionDetails));
    }

    public InstanceViewModel(
        GamehookRouter router,
        FilesystemProvider filesystemProvider,
        RetroArchConfigurationService retroArchConfiguration,
        SettingsService settings,
        IEnumerable<DriverRegistration> driverRegistrations)
    {
        session = router.Session;
        this.router = router;
        this.filesystemProvider = filesystemProvider;
        this.retroArchConfiguration = retroArchConfiguration;
        this.settings = settings;
        isContinuousReadEnabled = session.IsContinuousReadEnabled;
        session.Changed += OnSessionChanged;
        session.ContinuousReadChanged += OnContinuousReadChanged;

        try
        {
            // Official mappers first, then the user's own from the custom mapper folder.
            foreach (var mapper in filesystemProvider.GetMappers()
                         .OrderBy(entry => entry.Value.IsCustom)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var displayName = mapper.Value.IsCustom
                    ? mapper.Key[FilesystemProvider.CustomMapperKeyPrefix.Length..]
                    : mapper.Key;
                Mappers.Add(new MapperChoice(mapper.Value.Path, displayName, mapper.Value.IsCustom));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Error: Could not read the mapper directory. {ex.Message}";
        }

        foreach (var registration in driverRegistrations.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            Drivers.Add(new DriverChoice(registration.Name, registration.Name, registration.DefaultPort));
        }

        if (filesystemProvider.GetLastMapperPath() is { } lastMapperPath)
        {
            SelectedMapper = Mappers.FirstOrDefault(m => string.Equals(m.FullPath, lastMapperPath, StringComparison.Ordinal));
        }

        if (filesystemProvider.GetLastDriver() is { } lastDriver)
        {
            SelectedDriver = Drivers.FirstOrDefault(d => string.Equals(d.Name, lastDriver, StringComparison.Ordinal));
        }

        var properties = CreateLockedTool(new PropertiesToolViewModel(this));
        var workspace = CreateLockedDocument(new WorkspaceToolViewModel(this));
        hexViewer = CreateLockedDocument(new HexViewerToolViewModel(this));
        var property = CreateLockedTool(new PropertyToolViewModel(this));
        var pinned = CreateLockedTool(new PinnedToolViewModel(this));
        PropertiesPanel = properties;
        WorkspacePanel = workspace;
        PropertyPanel = property;
        PinnedPanel = pinned;

        dockFactory = new DockFactory(this, properties, workspace, hexViewer, property, pinned);
        Layout = dockFactory.CreateLayout();
        dockFactory.InitLayout(Layout);
        dockFactory.PropertyInspectorReplaced += SetDockedPropertyTool;
        hexViewer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(HexViewerToolViewModel.IsActive) && hexViewer.IsActive)
            {
                _ = hexViewer.RefreshAsync(force: true);
            }
        };

        // An instance added over the API may already be loading or loaded by the time its tab exists.
        if (session.Mapper is not null || session.IsConnecting)
        {
            ProcessSessionChanged();
        }
    }

    public string? GetLastOpenedSaveStateDirectory() => filesystemProvider.GetLastOpenedSaveStateDirectory();

    public void RememberLastOpenedSaveStateDirectory(string directory) =>
        filesystemProvider.RememberLastOpenedSaveStateDirectory(directory);

    public string? GetMapperDirectory() => filesystemProvider.GetPrimaryCustomMapperDirectory();


    public string? GetLastOpenedMapperDirectory() => filesystemProvider.GetLastOpenedMapperDirectory();

    public void SelectMapperFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var mapper = Mappers.FirstOrDefault(item => string.Equals(item.FullPath, fullPath, StringComparison.Ordinal));
        if (mapper is null)
        {
            // Browsed from anywhere on disk, so not an official mapper.
            mapper = new MapperChoice(fullPath, fullPath, IsCustom: true);
            Mappers.Add(mapper);
        }

        SelectedMapper = mapper;
    }

    public void RememberLastOpenedMapperDirectory(string directory) =>
        filesystemProvider.RememberLastOpenedMapperDirectory(directory);

    private bool CanLoad() => SelectedDriver is not null
        && SelectedMapper is not null
        && (!IsSaveStateDriver || SelectedSaveStatePath is not null)
        && (EffectiveDriverPort is not { } port || NetworkEndpoint.IsValidPort(port));

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (SelectedDriver is null || SelectedMapper is null || (IsSaveStateDriver && SelectedSaveStatePath is null))
        {
            Status = "Complete load setup first.";
            return;
        }

        // Stop here when another instance already uses this driver endpoint - before the RetroArch
        // probe below (which would happily answer for the other instance's emulator) and before
        // this tab unloads what it has.
        var (driverName, driverSource) = LoadDriver;
        if (router.FindDriverConflict(driverName, driverSource) is { } conflict)
        {
            Status = $"Error: {conflict}";
            return;
        }

        if (EffectiveDriverPort is { } port)
        {
            filesystemProvider.RememberLastDriverPort(SelectedDriver.Name, port);
        }

        var retroArchPort = EffectiveDriverPort ?? RetroArchDriver.DefaultPort;
        if (SelectedDriver.Name == RetroArchDriver.Name
            && !await retroArchConfiguration.NetworkCommandsAvailableAsync(retroArchPort).ConfigureAwait(true))
        {
            // Offering to turn Network Commands on only makes sense on the default port: that
            // setting is all the dialog changes, and a custom port means the user already set
            // Network Commands up - so there, nothing answering just means RetroArch isn't running.
            if (retroArchPort != RetroArchDriver.DefaultPort)
            {
                Status = $"Error: Nothing is answering on port {retroArchPort}. Start RetroArch with Network Commands on port {retroArchPort}, then try again.";
            }
            else if (RetroArchNetworkCommandsUnavailable is { } setup
                && await setup(retroArchConfiguration.FindConfigurationFiles()).ConfigureAwait(true))
            {
                Status = "RetroArch Network Commands enabled in its configuration. Restart RetroArch, then load again.";
            }
            else
            {
                Status = $"RetroArch is not answering on Network Commands (port {retroArchPort}). Start RetroArch, enable Settings > Network > Network Commands, check the port matches, then try again.";
            }
            return;
        }

        await LoadSelectedMapperAsync().ConfigureAwait(true);
    }

    private async Task LoadSelectedMapperAsync()
    {
        ClearWorkspace();
        // GamehookRouter.LoadAsync starts polling on success.
        var (driverName, driverSource) = LoadDriver;
        var (success, error) = await router.LoadAsync(SelectedMapper!.FullPath, driverName, driverSource).ConfigureAwait(true);
        SyncSelectedDriver();
        // A refused load (driver endpoint taken by another instance) never reaches the session,
        // so its error isn't in session.Status.
        if (!success && error is not null && error != session.Status)
        {
            Status = $"Error: {error}";
        }
    }

    private (string Name, string? Source) LoadDriver
    {
        get
        {
            var name = SelectedDriver!.Name;
            if (IsSaveStateDriver) return (name, SelectedSaveStatePath);
            if (EffectiveDriverPort is not { } port) return (name, null);

            return routerDriver is { } current && current.Name == name && ParsePort(current) == port
                ? current
                : (name, NetworkEndpoint.Format(null, port));
        }
    }

    // Prefills the load screen with the driver the router is actually on, whenever that changes.
    private void SyncSelectedDriver()
    {
        if (router.DriverName is not { } name) return;

        var current = (name, router.DriverSourcePath);
        if (routerDriver == current) return;
        routerDriver = current;

        syncingSelectedDriver = true;
        try
        {
            SelectedDriver = Drivers.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) ?? SelectedDriver;
            if (ParsePort(current) is { } port)
            {
                DriverPort = port;
            }
            else if (IsSaveStateDriver)
            {
                SelectedSaveStatePath = current.DriverSourcePath;
            }
        }
        finally
        {
            syncingSelectedDriver = false;
        }
    }

    private int? ParsePort((string Name, string? Source) driver)
    {
        if (Drivers.FirstOrDefault(d => string.Equals(d.Name, driver.Name, StringComparison.OrdinalIgnoreCase))?.DefaultPort is not { } defaultPort)
        {
            return null;
        }

        try
        {
            return NetworkEndpoint.Parse(driver.Source, defaultPort, driver.Name).Port;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool CanRefresh() => Mapper is not null;

    // Reloads what the instance actually has loaded, which may have come from the API rather than
    // this tab's load screen selections.
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ReloadAsync()
    {
        if (router.MapperPath is not { } mapperPath || router.DriverName is not { } driverName) return;
        ClearWorkspace();
        await router.LoadAsync(mapperPath, driverName, router.DriverSourcePath).ConfigureAwait(true);
    }

    // GamehookSession polls on a background loop (ConfigureAwait(false) throughout), so its Changed
    // event can land on a threadpool thread - hop back to the UI thread before touching bindings.
    private void OnSessionChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // A slow render must not leave old read notifications queued behind newer values.
            // Keep one UI update pending; it always reads current session state when it runs.
            if (Interlocked.Exchange(ref sessionUpdateQueued, 1) == 0)
            {
                Dispatcher.UIThread.Post(ProcessSessionChanged, DispatcherPriority.Render);
            }
            return;
        }

        ProcessSessionChanged();
    }

    private void ProcessSessionChanged()
    {
        if (disposed) return;
        Interlocked.Exchange(ref sessionUpdateQueued, 0);

        // Connection state stays current even with continuous read mode off, so loading a mapper still moves
        // between the load screen and the workspace.
        Mapper = session.Mapper;
        IsConnecting = session.IsConnecting;
        Status = session.Status;
        DataWarning = session.DataWarning;
        ConnectionWarning = session.ConnectionWarning;
        SyncSelectedDriver();

        // With continuous read mode off, Changed only fires for on-demand reads (Read, or an API
        // ?read=true), so the tree below always shows the latest read either way.
        OnPropertyChanged(nameof(FooterStatusBrush));
        OnFooterMetricsChanged();

        if (session.Mapper is not { } activeMapper)
        {
            return;
        }

        if (!ReferenceEquals(treeSourceProperties, activeMapper.Properties))
        {
            treeSourceProperties = activeMapper.Properties;
            RebuildTree(activeMapper);
        }

        SyncHexRegions();

        foreach (var leaf in treeLeaves)
        {
            leaf.RefreshDisplayValue();
        }

        // Raw memory reads share driver ownership and use their own display cadence. They are driver
        // reads of their own, so only continuous read mode triggers them from here.
        if (IsContinuousReadEnabled && IsSelected && hexViewer.IsActive)
        {
            _ = hexViewer.RefreshAsync();
        }
    }

    // Settings can also change via POST /settings on a Kestrel thread.
    private void OnContinuousReadChanged(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnContinuousReadChanged(session.IsContinuousReadEnabled));
            return;
        }

        if (disposed || IsContinuousReadEnabled == enabled) return;
        IsContinuousReadEnabled = enabled;
        // Catch up on whatever changed while frozen (a mapper loaded over the API, new values).
        if (enabled) ProcessSessionChanged();
    }

    [RelayCommand]
    private void ToggleContinuousRead() => settings.Update(!session.IsContinuousReadEnabled);

    private void RebuildTree(IMapper activeMapper)
    {
        foreach (var leaf in treeLeaves) leaf.PinnedChanged -= OnNodePinnedChanged;
        Tree.Clear();
        Pinned.Clear();
        PinnedProperties.Clear();
        treeLeaves.Clear();
        propertySearchIndex.Clear();
        OnPropertyChanged(nameof(HasPinnedProperties));
        foreach (var node in PropertyTreeNodeViewModel.Build(activeMapper.Properties.Values))
        {
            Tree.Add(node);
        }

        foreach (var leaf in PropertyTreeNodeViewModel.EnumerateLeaves(Tree))
        {
            treeLeaves.Add(leaf);
            leaf.PinnedChanged += OnNodePinnedChanged;
            propertySearchIndex[leaf.Property!.Name] = leaf;
        }

        OnPropertyChanged(nameof(HasProperties));
        ApplyExplorerSearch(PropertiesPanel.SearchText);
    }

    public void ApplyExplorerSearch(string searchText)
    {
        explorerSearchCancellation?.Cancel();
        var searchVersion = ++explorerSearchVersion;
        var query = NormalizeExplorerSearch(searchText);
        if (query.Length == 0)
        {
            ReplaceSearchTree([]);
            return;
        }

        var index = propertySearchIndex.ToArray();
        var cancellation = explorerSearchCancellation = new CancellationTokenSource();
        _ = Task.Run(
            () => BuildSearchTree(index, query, cancellation.Token),
            cancellation.Token).ContinueWith(
                task => Dispatcher.UIThread.Post(
                    () => ApplyExplorerMatches(task, searchVersion, cancellation)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    private void ApplyExplorerMatches(
        Task<IReadOnlyList<PropertySearchTreeNodeViewModel>> task,
        int searchVersion,
        CancellationTokenSource cancellation)
    {
        if (task.IsCanceled || task.IsFaulted || cancellation.IsCancellationRequested || searchVersion != explorerSearchVersion)
        {
            if (task.Status == TaskStatus.RanToCompletion)
            {
                foreach (var node in task.Result)
                {
                    node.Dispose();
                }
            }
            return;
        }

        ReplaceSearchTree(task.Result);
    }

    private static string NormalizeExplorerSearch(string searchText) => string.Join(
        '.',
        searchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IReadOnlyList<PropertySearchTreeNodeViewModel> BuildSearchTree(
        KeyValuePair<string, PropertyTreeNodeViewModel>[] index,
        string query,
        CancellationToken cancellationToken)
    {
        var roots = new Dictionary<string, SearchTreeBuilder>(StringComparer.Ordinal);
        foreach (var entry in index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var children = roots;
            SearchTreeBuilder? current = null;
            foreach (var segment in entry.Key.Split('.'))
            {
                if (!children.TryGetValue(segment, out current))
                {
                    current = new SearchTreeBuilder(segment);
                    children.Add(segment, current);
                }
                children = current.Children;
            }
            current!.Source = entry.Value;
        }
        return roots.Values.Select(SearchTreeBuilder.Build).ToArray();
    }

    private void ReplaceSearchTree(IReadOnlyList<PropertySearchTreeNodeViewModel> next)
    {
        foreach (var node in SearchTree)
        {
            node.Dispose();
        }
        SearchTree = next;
    }

    partial void OnSelectedSearchNodeChanged(PropertySearchTreeNodeViewModel? value)
    {
        SelectedNode = value?.Source;
    }

    private sealed class SearchTreeBuilder(string name)
    {
        public string Name { get; } = name;
        public PropertyTreeNodeViewModel? Source { get; set; }
        public Dictionary<string, SearchTreeBuilder> Children { get; } = new(StringComparer.Ordinal);

        public static PropertySearchTreeNodeViewModel Build(SearchTreeBuilder builder) =>
            new(builder.Name, builder.Source, builder.Children.Values.Select(Build).ToArray());
    }

    private void SyncHexRegions()
    {
        var regions = HexViewerToolViewModel.GetRegions(Mapper);
        if (RegionIds.SequenceEqual(regions, StringComparer.Ordinal))
        {
            return;
        }

        RegionIds.Clear();
        foreach (var regionId in regions)
        {
            RegionIds.Add(regionId);
        }

        if (SelectedRegionId is null || !RegionIds.Contains(SelectedRegionId))
        {
            SelectedRegionId = RegionIds.FirstOrDefault();
        }
    }

    // DockFactory replaces this panel whenever its current inspector becomes a floating
    // property snapshot. Keep the docked Property panel aimed at the in-layout replacement.
    internal void SetDockedPropertyTool(PropertyToolViewModel tool)
    {
        PropertyPanel = tool;
        OnPropertyChanged(nameof(PropertyPanel));
    }

    private static T CreateLockedTool<T>(T tool) where T : Tool
    {
        tool.CanClose = false;
        tool.CanPin = false;
        tool.CanFloat = false;
        tool.CanDockAsDocument = false;
        tool.CanDrag = false;
        tool.CanDrop = false;
        return tool;
    }

    private static T CreateLockedDocument<T>(T document) where T : Document
    {
        document.CanClose = false;
        document.CanPin = false;
        document.CanFloat = false;
        document.CanDockAsDocument = false;
        document.CanDrag = false;
        document.CanDrop = false;
        return document;
    }

    // Clicking a watched row in the Pinned panel jumps the Property panel and hex viewer
    // straight to its full detail - the same as selecting it in the tree would.
    [RelayCommand]
    private void SelectPinnedNode(PropertyTreeNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        SelectedNode = node;
    }

    private void OnNodePinnedChanged(PropertyTreeNodeViewModel node, bool isPinned)
    {
        if (isPinned)
        {
            if (!Pinned.Contains(node))
            {
                Pinned.Add(node);
            }

            if (node.Property is { } property && !PinnedProperties.Contains(property))
            {
                PinnedProperties.Add(property);
            }
        }
        else
        {
            Pinned.Remove(node);
            if (node.Property is { } property)
            {
                PinnedProperties.Remove(property);
            }
        }

        OnPropertyChanged(nameof(HasPinnedProperties));
    }

    [RelayCommand]
    private void DismissDataWarning() => DataWarning = null;

    [RelayCommand]
    private void DismissConnectionWarning() => ConnectionWarning = null;

    [RelayCommand]
    public void ShowLoadScreen()
    {
        router.Unload();
        ClearWorkspace();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        // Catch the hex viewer up on reads skipped while this tab was hidden.
        if (value && hexViewer.IsActive && Mapper is not null)
        {
            _ = hexViewer.RefreshAsync(force: true);
        }
    }

    partial void OnSelectedMapperChanged(MapperChoice? value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        OnFooterMetricsChanged();

        if (value is not null)
        {
            filesystemProvider.RememberLastMapperPath(value.FullPath);
        }
    }

    partial void OnSelectedSaveStatePathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanSelectMapper));
        LoadCommand.NotifyCanExecuteChanged();
    }

    partial void OnDriverPortChanged(decimal? value) => LoadCommand.NotifyCanExecuteChanged();

    partial void OnSelectedDriverChanged(DriverChoice? value)
    {
        DriverPort = value?.DefaultPort is { } defaultPort
            ? filesystemProvider.GetLastDriverPort(value.Name) ?? defaultPort
            : null;
        OnPropertyChanged(nameof(IsSaveStateDriver));
        OnPropertyChanged(nameof(IsNetworkDriver));
        OnPropertyChanged(nameof(DriverPortHint));
        OnPropertyChanged(nameof(CanSelectMapper));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        LoadCommand.NotifyCanExecuteChanged();

        // Only the user's own picks are remembered as the default for new tabs.
        if (value is not null && !syncingSelectedDriver)
        {
            filesystemProvider.RememberLastDriver(value.Name);
        }
    }

    partial void OnMapperChanged(IMapper? value)
    {
        ReloadCommand.NotifyCanExecuteChanged();
        ReadNowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsLoadScreenVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(IsReadOnDemandBannerVisible));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        OnFooterMetricsChanged();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TabTitle));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(IsLoadScreenVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(IsReadOnDemandBannerVisible));
    }

    partial void OnDataWarningChanged(string? value) => OnPropertyChanged(nameof(HasDataWarning));

    partial void OnConnectionWarningChanged(string? value)
    {
        OnPropertyChanged(nameof(HasConnectionWarning));
        OnPropertyChanged(nameof(FooterStatusBrush));
        OnFooterMetricsChanged();
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsLoadScreenVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(IsReadOnDemandBannerVisible));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        OnFooterMetricsChanged();
    }

    // Called by the hex viewer when a byte is clicked. Expanding its path makes the
    // selected leaf visible before the tree scrolls it into view.
    public PropertyTreeNodeViewModel? SelectProperty(IProperty property)
    {
        rawByteSelection = null;
        SelectionAddressRange = null;
        SelectionParses = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsPropertyPanelEmpty));
        var node = FindNode(Tree, property);
        SelectedNode = node ?? SelectedNode;
        if (node is not null)
        {
            CenterTreeItemRequested?.Invoke(node);
        }

        return node;
    }

    public void SelectBytes(string regionId, ulong startingAddress, ReadOnlyMemory<byte> bytes)
    {
        if (Mapper is not { } activeMapper)
        {
            return;
        }

        SelectedNode = null;
        SelectionAddressRange = FormatAddressRange(startingAddress, bytes.Length);
        rawByteSelection = new RawByteSelection(regionId, startingAddress, bytes, InspectBytes(activeMapper, bytes));
        SelectionParses = rawByteSelection.Parses;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsPropertyPanelEmpty));
        RawSelectionUpdated?.Invoke(rawByteSelection);
    }

    internal void RefreshSelectedBytes(string regionId, ulong startingAddress, ReadOnlyMemory<byte> bytes)
    {
        if (rawByteSelection is not { } selection || selection.RegionId != regionId ||
            selection.StartingAddress < startingAddress)
        {
            return;
        }

        var offset = selection.StartingAddress - startingAddress;
        if (offset > (ulong)bytes.Length || selection.Bytes.Length > bytes.Length - (int)offset)
        {
            return;
        }

        var selectedBytes = bytes.Slice((int)offset, selection.Bytes.Length);
        var updated = selection with { Bytes = selectedBytes, Parses = InspectBytes(Mapper!, selectedBytes) };
        rawByteSelection = updated;
        SelectionParses = updated.Parses;
        OnPropertyChanged(nameof(HasSelection));
        RawSelectionUpdated?.Invoke(updated);
    }

    public IDriver? HexDriver => session.HexDriver;
    public GameSystem? HexSystem => Mapper?.System;

    partial void OnSelectedRegionIdChanged(string? value)
    {
        hexViewer.Clear();
        if (value is not null)
        {
            _ = hexViewer.RefreshAsync(force: true);
        }
    }

    partial void OnSelectedNodeChanged(PropertyTreeNodeViewModel? value)
    {
        // Raw-byte parser results and a mapped property describe different selections. Clear all
        // raw-selection state, not only parses, so every inspector sees one selection type.
        if (value?.Property is not null &&
            (rawByteSelection is not null || SelectionAddressRange is not null || SelectionParses is not null))
        {
            rawByteSelection = null;
            SelectionAddressRange = null;
            SelectionParses = null;
            OnPropertyChanged(nameof(HasSelection));
        }

        OnPropertyChanged(nameof(HasSelectedProperty));
        OnPropertyChanged(nameof(IsPropertyPanelEmpty));

        if (value?.Property?.BuildRequest() is { } request && RegionIds.Contains(request.RegionId))
        {
            SelectedRegionId = request.RegionId;
        }

        ScrollHexToSelectionRequested?.Invoke();
    }

    private static PropertyTreeNodeViewModel? FindNode(IEnumerable<PropertyTreeNodeViewModel> nodes, IProperty property)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.Property, property))
            {
                return node;
            }

            var found = FindNode(node.Children, property);
            if (found is not null)
            {
                node.IsExpanded = true;
                return found;
            }
        }

        return null;
    }

    private void ClearWorkspace()
    {
        explorerSearchCancellation?.Cancel();
        explorerSearchVersion++;
        hexViewer.Clear();
        foreach (var leaf in treeLeaves) leaf.PinnedChanged -= OnNodePinnedChanged;
        treeSourceProperties = null;
        SelectedNode = null;
        SelectedRegionId = null;
        rawByteSelection = null;
        SelectionAddressRange = null;
        SelectionParses = null;
        DataWarning = null;
        ConnectionWarning = null;
        Tree.Clear();
        ReplaceSearchTree([]);
        propertySearchIndex.Clear();
        Pinned.Clear();
        PinnedProperties.Clear();
        treeLeaves.Clear();
        RegionIds.Clear();
        OnPropertyChanged(nameof(HasProperties));
        OnPropertyChanged(nameof(HasPinnedProperties));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSelectedProperty));
        OnPropertyChanged(nameof(IsPropertyPanelEmpty));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        explorerSearchCancellation?.Cancel();
        ReplaceSearchTree([]);
        session.Changed -= OnSessionChanged;
        session.ContinuousReadChanged -= OnContinuousReadChanged;
        session.StopPolling();
        PropertyPanel.Dispose();
        foreach (var leaf in treeLeaves) leaf.PinnedChanged -= OnNodePinnedChanged;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool[] bits => string.Join(" ", bits.Select(bit => bit ? '1' : '0')),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static IReadOnlyList<SelectionParseViewModel> InspectBytes(IMapper mapper, ReadOnlyMemory<byte> bytes) =>
        mapper.Inspect(bytes)
            .Select(result => new SelectionParseViewModel(result.Name, result.Type, FormatValue(result.Value), result.Error))
            .ToArray();

    private static string FormatAddressRange(ulong startingAddress, int length)
    {
        var endingAddress = startingAddress + (ulong)Math.Max(length, 1) - 1;
        return $"0x{startingAddress:X}–0x{endingAddress:X}";
    }
}

public sealed record DriverChoice(string Name, string DisplayName, int? DefaultPort = null);

public sealed record MapperChoice(string FullPath, string DisplayName, bool IsCustom = false);

public sealed record SelectionParseViewModel(string Name, string Type, string Value, string? Error)
{
    public bool HasError => Error is not null;
    public string DisplayValue => HasError ? Error! : Value;
}

public sealed record RawByteSelection(
    string RegionId,
    ulong StartingAddress,
    ReadOnlyMemory<byte> Bytes,
    IReadOnlyList<SelectionParseViewModel> Parses);
