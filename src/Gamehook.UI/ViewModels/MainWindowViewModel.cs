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

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    // Match display updates to a 60 Hz frame budget. RefreshAsync remains non-overlapping, so a
    // slower driver naturally backs this off instead of queuing reads.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(16);
    private static readonly IBrush HealthyStatusBrush = new SolidColorBrush(Color.Parse("#47C975"));
    private static readonly IBrush WarningStatusBrush = new SolidColorBrush(Color.Parse("#E0A339"));
    private static readonly IBrush ErrorStatusBrush = new SolidColorBrush(Color.Parse("#E95D5D"));

    private readonly GamehookSession session;
    private readonly FilesystemProvider filesystemProvider;
    private readonly DockFactory dockFactory;
    private readonly HexViewerToolViewModel hexViewer;
    private Dictionary<string, IProperty>? treeSourceProperties;
    private readonly List<PropertyTreeNodeViewModel> treeLeaves = [];
    private int sessionUpdateQueued;
    private bool disposed;
    private RawByteSelection? rawByteSelection;

    [ObservableProperty]
    private MapperChoice? selectedMapper;

    [ObservableProperty]
    private DriverChoice? selectedDriver;

    [ObservableProperty]
    private string? selectedSaveStatePath;

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

    public ObservableCollection<MapperChoice> Mappers { get; } = [];
    public ObservableCollection<DriverChoice> Drivers { get; } = [];
    public ObservableCollection<PropertyTreeNodeViewModel> Tree { get; } = [];
    public ObservableCollection<string> RegionIds { get; } = [];
    public PropertiesToolViewModel PropertiesPanel { get; }
    public HexViewerToolViewModel HexViewerPanel => hexViewer;
    public PropertyToolViewModel PropertyPanel { get; private set; } = null!;
    public PinnedToolViewModel PinnedPanel { get; }

    // Cross-panel mediation: the tree, hex viewer, and pinned list each live in their own
    // dockable Tool view now, so they can no longer reach each other's named elements directly.
    public event Action<PropertyTreeNodeViewModel>? CenterTreeItemRequested;
    public event Action? ScrollHexToSelectionRequested;
    public event Action<RawByteSelection>? RawSelectionUpdated;
    internal void OnHexRegionLoaded() => ScrollHexToSelectionRequested?.Invoke();

    public IRootDock Layout { get; }

    // Holds the same node instances as Tree (not copies), so a poll tick that refreshes a
    // leaf's DisplayValue in Tree is already reflected here via shared bindings.
    public ObservableCollection<PropertyTreeNodeViewModel> Pinned { get; } = [];

    // Mirrors Pinned's underlying properties for the hex viewer, which only knows about
    // IProperty (domain), not the tree's ViewModel wrapper.
    public ObservableCollection<IProperty> PinnedProperties { get; } = [];

    // Compiled bindings only negate `bool`, so the empty-state visuals in MainWindow.axaml
    // bind to these instead of poking at Tree.Count / SelectedNode.Property directly.
    public bool HasProperties => Tree.Count > 0;
    public bool HasSelection => SelectionParses is { Count: > 0 };
    public bool HasSelectedProperty => SelectedNode?.Property is not null;
    public bool IsPropertyPanelEmpty => !HasSelection && !HasSelectedProperty;
    public bool IsLoaded => Mapper is not null;
    public bool HasError => Status.StartsWith("Error:", StringComparison.Ordinal);
    public bool IsConnected => Mapper is not null && !HasError;
    public bool IsLoadScreenVisible => !IsConnected && !IsConnecting;
    public bool IsWorkspaceVisible => IsConnected && !IsConnecting;
    public bool IsSaveStateDriver => SelectedDriver?.Name == SaveStateDriver.Name;
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
    public bool HasCustomMapperDirectory => filesystemProvider.HasCustomMapperDirectory();
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
    public string WindowTitle => Mapper is null
        ? "Gamehook"
        : $"Gamehook - {Mapper.GameName}";
    public string? FooterMetricsBreakdown => IsConnected
        ? $"Driver: {Mapper!.LastReadMetrics.Driver.TotalMilliseconds:0.##} ms\n" +
          $"Property Translation: {Mapper!.LastReadMetrics.PropertyTranslation.TotalMilliseconds:0.##} ms\n" +
          $"Inline Calculations: {Mapper!.LastReadMetrics.InlineCalculations.TotalMilliseconds * 1000:0.#} µs\n" +
          $"Postprocessor: {Mapper!.LastReadMetrics.Postprocessor.TotalMilliseconds * 1000:0.#} µs\n" +
          $"Total: {Mapper!.LastReadMetrics.Total.TotalMilliseconds:0.##} ms" +
          FooterConnectionDetails
        : null;

    private string FooterConnectionDetails => Mapper?.ConsecutiveReadFailures switch
    {
        0 or null => string.Empty,
        _ when Mapper.HasConnectionRefusal =>
            $"\n\nRetroArch rejected connection\nFailed reads in a row: {Mapper.ConsecutiveReadFailures}\n" +
            $"Check RetroArch is running and Network Commands are enabled.\nLast error: {Mapper.LastReadFailureMessage}",
        >= 5 =>
            $"\n\nRetroArch not responding\nFailed reads in a row: {Mapper.ConsecutiveReadFailures}\n" +
            $"Showing last successful values. Check RetroArch and Network Commands.\nLast error: {Mapper.LastReadFailureMessage}",
        _ =>
            $"\n\nRetroArch read delayed\nFailed reads in a row: {Mapper.ConsecutiveReadFailures}\n" +
            $"Showing last successful values. Last error: {Mapper.LastReadFailureMessage}",
    };

    public MainWindowViewModel(
        GamehookSession session,
        FilesystemProvider filesystemProvider,
        IEnumerable<DriverRegistration> driverRegistrations)
    {
        this.session = session;
        this.filesystemProvider = filesystemProvider;
        session.Changed += OnSessionChanged;

        try
        {
            foreach (var mapper in filesystemProvider.GetMappers().OrderBy(entry => entry.Key, StringComparer.Ordinal))
                Mappers.Add(new MapperChoice(mapper.Value, mapper.Key));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Error: Could not read the mapper directory. {ex.Message}";
        }

        foreach (var registration in driverRegistrations.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            Drivers.Add(new DriverChoice(registration.Name, registration.Name));
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
        hexViewer = CreateLockedTool(new HexViewerToolViewModel(this));
        var property = CreateLockedTool(new PropertyToolViewModel(this));
        var pinned = CreateLockedTool(new PinnedToolViewModel(this));
        PropertiesPanel = properties;
        PropertyPanel = property;
        PinnedPanel = pinned;

        dockFactory = new DockFactory(this, properties, hexViewer, property, pinned);
        Layout = dockFactory.CreateLayout();
        dockFactory.InitLayout(Layout);
        dockFactory.PropertyInspectorReplaced += SetDockedPropertyTool;
    }

    public string? GetLastOpenedSaveStateDirectory() => filesystemProvider.GetLastOpenedSaveStateDirectory();

    public void RememberLastOpenedSaveStateDirectory(string directory) =>
        filesystemProvider.RememberLastOpenedSaveStateDirectory(directory);

    public string GetMapperDirectory() => filesystemProvider.GetMapperDirectory();

    public string? GetLastOpenedMapperDirectory() => filesystemProvider.GetLastOpenedMapperDirectory();

    public void SelectMapperFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var mapper = Mappers.FirstOrDefault(item => string.Equals(item.FullPath, fullPath, StringComparison.Ordinal));
        if (mapper is null)
        {
            mapper = new MapperChoice(fullPath, fullPath);
            Mappers.Add(mapper);
        }

        SelectedMapper = mapper;
    }

    public void RememberLastOpenedMapperDirectory(string directory) =>
        filesystemProvider.RememberLastOpenedMapperDirectory(directory);

    private bool CanLoad() => SelectedDriver is not null
        && SelectedMapper is not null
        && (!IsSaveStateDriver || SelectedSaveStatePath is not null);

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (SelectedDriver is null || SelectedMapper is null || (IsSaveStateDriver && SelectedSaveStatePath is null))
        {
            Status = "Complete load setup first.";
            return;
        }

        await LoadSelectedMapperAsync().ConfigureAwait(true);
    }

    private async Task LoadSelectedMapperAsync()
    {
        ClearWorkspace();
        var loaded = await session.LoadAsync(
            SelectedMapper!.FullPath,
            SelectedDriver!.Name,
            IsSaveStateDriver ? SelectedSaveStatePath : null).ConfigureAwait(true);

        if (loaded)
        {
            session.StartPolling(RefreshInterval);
        }
    }

    private bool CanRefresh() => Mapper is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ReloadAsync() => await LoadSelectedMapperAsync().ConfigureAwait(true);

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

        Mapper = session.Mapper;
        IsConnecting = session.IsConnecting;
        Status = session.Status;
        DataWarning = session.DataWarning;
        ConnectionWarning = session.ConnectionWarning;
        OnPropertyChanged(nameof(FooterStatusBrush));
        OnPropertyChanged(nameof(FooterMetricsBreakdown));

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

        // Raw memory reads share driver ownership and use their own display cadence.
        _ = hexViewer.RefreshAsync();
    }

    private void RebuildTree(IMapper activeMapper)
    {
        foreach (var leaf in treeLeaves) leaf.PinnedChanged -= OnNodePinnedChanged;
        Tree.Clear();
        Pinned.Clear();
        PinnedProperties.Clear();
        treeLeaves.Clear();
        OnPropertyChanged(nameof(HasPinnedProperties));
        foreach (var node in PropertyTreeNodeViewModel.Build(activeMapper.Properties.Values))
        {
            Tree.Add(node);
        }

        foreach (var leaf in PropertyTreeNodeViewModel.EnumerateLeaves(Tree))
        {
            treeLeaves.Add(leaf);
            leaf.PinnedChanged += OnNodePinnedChanged;
        }

        OnPropertyChanged(nameof(HasProperties));
    }

    private void SyncHexRegions()
    {
        var regions = HexViewerToolViewModel.GetRegions(Mapper?.System);
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
        session.Unload();
        ClearWorkspace();
    }

    partial void OnSelectedMapperChanged(MapperChoice? value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        OnPropertyChanged(nameof(FooterMetricsBreakdown));

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

    partial void OnSelectedDriverChanged(DriverChoice? value)
    {
        OnPropertyChanged(nameof(IsSaveStateDriver));
        OnPropertyChanged(nameof(CanSelectMapper));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        LoadCommand.NotifyCanExecuteChanged();

        if (value is not null)
        {
            filesystemProvider.RememberLastDriver(value.Name);
        }
    }

    partial void OnMapperChanged(IMapper? value)
    {
        ReloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsLoadScreenVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        OnPropertyChanged(nameof(FooterMetricsBreakdown));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLoadScreenVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
    }

    partial void OnDataWarningChanged(string? value) => OnPropertyChanged(nameof(HasDataWarning));

    partial void OnConnectionWarningChanged(string? value)
    {
        OnPropertyChanged(nameof(HasConnectionWarning));
        OnPropertyChanged(nameof(FooterStatusBrush));
        OnPropertyChanged(nameof(FooterMetricsBreakdown));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsLoadScreenVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(HasReadingStatus));
        OnPropertyChanged(nameof(FooterMetricsBreakdown));
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
        rawByteSelection = new RawByteSelection(regionId, startingAddress, bytes.Length, InspectBytes(activeMapper, bytes));
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
        if (offset > (ulong)bytes.Length || selection.Length > bytes.Length - (int)offset)
        {
            return;
        }

        var updated = selection with { Parses = InspectBytes(Mapper!, bytes.Slice((int)offset, selection.Length)) };
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
        // Raw-byte parser results and a mapped property describe different selections. Keeping
        // both made PropertyToolView render two scroll viewers in one Grid cell, overlapping.
        if (value?.Property is not null && SelectionParses is not null)
        {
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
        session.Changed -= OnSessionChanged;
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

public sealed record DriverChoice(string Name, string DisplayName);

public sealed record MapperChoice(string FullPath, string DisplayName);

public sealed record SelectionParseViewModel(string Name, string Type, string Value, string? Error)
{
    public bool HasError => Error is not null;
    public string DisplayValue => HasError ? Error! : Value;
}

public sealed record RawByteSelection(
    string RegionId,
    ulong StartingAddress,
    int Length,
    IReadOnlyList<SelectionParseViewModel> Parses);
