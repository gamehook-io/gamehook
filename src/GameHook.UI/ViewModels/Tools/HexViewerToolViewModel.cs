using GameHook.Domain.Interface;
using GameHook.Domain;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;

namespace GameHook.UI.ViewModels.Tools;

public sealed class HexViewerToolViewModel : Tool
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    public MainWindowViewModel Main { get; }
    public ReadOnlyMemory<byte> Bytes { get; private set; }
    public ulong StartingAddress { get; private set; }
    public int RefreshToken { get; private set; }
    private Task? pendingRead;
    private DateTimeOffset lastReadStarted;
    private string? displayedRegion;

    public HexViewerToolViewModel(MainWindowViewModel main)
    {
        Main = main;
        Id = "HexViewer";
        Title = "Hex Viewer";
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
    }
}
