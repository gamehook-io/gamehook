using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace Gamehook.UI.ViewModels.Tools;

public sealed partial class HexViewerToolViewModel : Document
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    // Delay before the loading spinner appears once a region has no bytes yet - reads normally
    // land within a poll or two, so showing it immediately would just flicker on every region switch.
    private static readonly TimeSpan LoadingSpinnerDelay = TimeSpan.FromSeconds(1);

    public InstanceViewModel Main { get; }
    [ObservableProperty] private ReadOnlyMemory<byte> bytes;
    [ObservableProperty] private ulong startingAddress;
    [ObservableProperty] private ulong addressBase;
    // Bumped on every refresh so the hex view re-renders even when the bytes compare equal.
    [ObservableProperty] private int refreshToken;
    [ObservableProperty] private bool showLoadingSpinner;
    private Task? pendingRead;
    private DateTimeOffset lastReadStarted;
    private string? displayedRegion;
    private readonly DispatcherTimer loadingSpinnerTimer;

    public HexViewerToolViewModel(InstanceViewModel main)
    {
        Main = main;
        Id = "HexViewer";
        Title = "Hex Viewer";
        loadingSpinnerTimer = new DispatcherTimer { Interval = LoadingSpinnerDelay };
        loadingSpinnerTimer.Tick += (_, _) =>
        {
            loadingSpinnerTimer.Stop();
            ShowLoadingSpinner = true;
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

    private async Task RefreshCoreAsync()
    {
        var mapper = Main.Mapper;
        var regionId = Main.SelectedRegionId;
        if (mapper is null || regionId is null) return;

        ReadOnlyMemory<byte>? bytes;
        try
        {
            (bytes, _) = await Main.Router.ReadRegionAsync(regionId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        if (bytes is not { } regionBytes) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(mapper, Main.Mapper) || regionId != Main.SelectedRegionId) return;
            loadingSpinnerTimer.Stop();
            ShowLoadingSpinner = false;
            Bytes = regionBytes;
            StartingAddress = 0;
            AddressBase = mapper.System.FindRegion(regionId)?.BusAddress ?? 0;
            RefreshToken++;
            Main.RefreshSelectedBytes(regionId, 0, regionBytes);
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
        AddressBase = 0;
        RefreshToken++;

        loadingSpinnerTimer.Stop();
        ShowLoadingSpinner = false;
        loadingSpinnerTimer.Start();
    }
}
