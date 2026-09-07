using GameHook.Domain.Interface;
using GameHook.Domain;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;

namespace GameHook.UI.ViewModels.Tools;

public sealed class HexViewerToolViewModel : Tool
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    // Delay before the loading spinner appears once a region has no bytes yet - reads normally
    // land within a poll or two, so showing it immediately would just flicker on every region switch.
    private static readonly TimeSpan LoadingSpinnerDelay = TimeSpan.FromSeconds(1);

    public MainWindowViewModel Main { get; }
    public ReadOnlyMemory<byte> Bytes { get; private set; }
    public ulong StartingAddress { get; private set; }
    public int RefreshToken { get; private set; }
    public bool ShowLoadingSpinner { get; private set; }
    private Task? pendingRead;
    private DateTimeOffset lastReadStarted;
    private string? displayedRegion;
    private readonly DispatcherTimer loadingSpinnerTimer;

    public HexViewerToolViewModel(MainWindowViewModel main)
    {
        Main = main;
        Id = "HexViewer";
        Title = "Hex Viewer";
        loadingSpinnerTimer = new DispatcherTimer { Interval = LoadingSpinnerDelay };
        loadingSpinnerTimer.Tick += (_, _) =>
        {
            loadingSpinnerTimer.Stop();
            ShowLoadingSpinner = true;
            OnPropertyChanged(nameof(ShowLoadingSpinner));
        };
    }

    public Task RefreshAsync(bool force = false)
    {
        if (pendingRead is { IsCompleted: false })
        {
            return pendingRead;
        }

        if (!force && DateTimeOffset.UtcNow - lastReadStarted < RefreshInterval)
        {
            return Task.CompletedTask;
        }

        lastReadStarted = DateTimeOffset.UtcNow;
        pendingRead = RefreshCoreAsync();
        return pendingRead;
    }

    public static IReadOnlyList<string> GetRegions(GameSystem? system) => system?.RegionDefinitions
        .Where(region => region.Length is > 0)
        .Select(region => region.Id)
        .OrderBy(id => id, StringComparer.Ordinal).ToArray() ?? [];

    private async Task RefreshCoreAsync()
    {
        var system = Main.HexSystem;
        var driver = Main.HexDriver;
        var regionId = Main.SelectedRegionId;
        IDriver.MemorySegmentSnapshot? snapshot;
        try
        {
            if (system is null || driver is null || regionId is null ||
                system.RegionDefinitions.FirstOrDefault(region => region.Id == regionId)?.Length is not { } length)
            {
                return;
            }

            var response = await driver.Read(new IDriver.Request(system, [new IDriver.MemorySegmentRequest(regionId, 0, length)]))
                .ConfigureAwait(false);
            snapshot = response.Segments.SingleOrDefault();
        }
        catch (Exception)
        {
            return;
        }

        if (snapshot is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(driver, Main.HexDriver) || !ReferenceEquals(system, Main.HexSystem)
                || regionId != Main.SelectedRegionId) return;
            loadingSpinnerTimer.Stop();
            if (ShowLoadingSpinner)
            {
                ShowLoadingSpinner = false;
                OnPropertyChanged(nameof(ShowLoadingSpinner));
            }
            Bytes = snapshot.Bytes;
            StartingAddress = snapshot.StartingAddress;
            RefreshToken++;
            OnPropertyChanged(nameof(Bytes));
            OnPropertyChanged(nameof(StartingAddress));
            OnPropertyChanged(nameof(RefreshToken));
            Main.RefreshSelectedBytes(regionId, snapshot.StartingAddress, snapshot.Bytes);
            if (displayedRegion != regionId)
            {
                displayedRegion = regionId;
                Main.OnHexRegionLoaded();
            }
        });
    }

    public void Clear()
    {
        displayedRegion = null;
        Bytes = ReadOnlyMemory<byte>.Empty;
        StartingAddress = 0;
        RefreshToken++;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(StartingAddress));
        OnPropertyChanged(nameof(RefreshToken));

        loadingSpinnerTimer.Stop();
        if (ShowLoadingSpinner)
        {
            ShowLoadingSpinner = false;
            OnPropertyChanged(nameof(ShowLoadingSpinner));
        }
        loadingSpinnerTimer.Start();
    }
}
