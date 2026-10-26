using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Domain;

namespace Gamehook.Android.ViewModels;

/// Raw memory viewer: pick a region, page through it, and poke bytes. Phone-sized: eight bytes a
/// row, one page at a time, rather than the desktop's scrolling hex grid.
internal sealed partial class MemoryViewModel(GamehookRouter router) : ObservableObject
{
    private const int PageSize = 256;
    private const int BytesPerRow = 8;
    private bool busy;
    private DateTime lastAutoRefresh;

    public ObservableCollection<string> Regions { get; } = [];

    [ObservableProperty]
    private string? selectedRegion;

    [ObservableProperty]
    private string offsetText = "0";

    [ObservableProperty]
    private string dump = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? message;

    [ObservableProperty]
    private bool messageIsError;

    [ObservableProperty]
    private string writeAddressText = string.Empty;

    [ObservableProperty]
    private string writeBytesText = string.Empty;

    public bool HasMessage => Message is not null;

    /// True while the Memory tab is on screen; only then is the device read.
    public bool IsActive { get; set; }

    public void SyncRegions()
    {
        var ids = router.RegionIds;
        if (Regions.SequenceEqual(ids, StringComparer.Ordinal)) return;

        Regions.Clear();
        foreach (var id in ids) Regions.Add(id);
        if (SelectedRegion is null || !Regions.Contains(SelectedRegion)) SelectedRegion = Regions.FirstOrDefault();
    }

    partial void OnSelectedRegionChanged(string? value) => _ = RefreshAsync();

    /// Called on session ticks: keeps a visible page live without hammering the driver.
    public void Tick()
    {
        if (!IsActive || DateTime.UtcNow - lastAutoRefresh < TimeSpan.FromMilliseconds(500)) return;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (busy || SelectedRegion is not { } region) return;
        if (!TryParseOffset(OffsetText, out var offset))
        {
            Report("Offset must be hexadecimal.", true);
            return;
        }

        busy = true;
        lastAutoRefresh = DateTime.UtcNow;
        try
        {
            var (bytes, error) = await router.ReadRegionAsync(region, offset, PageSize).ConfigureAwait(true);
            if (bytes is { } data)
            {
                Dump = Format(offset, data.Span);
                Message = null;
            }
            else
            {
                Report(error ?? "Could not read memory.", true);
            }
        }
        finally
        {
            busy = false;
        }
    }

    [RelayCommand]
    private Task PreviousPageAsync() => MoveAsync(-PageSize);

    [RelayCommand]
    private Task NextPageAsync() => MoveAsync(PageSize);

    private Task MoveAsync(int delta)
    {
        if (!TryParseOffset(OffsetText, out var offset)) offset = 0;
        var next = delta < 0 ? (offset > (ulong)-delta ? offset - (ulong)-delta : 0) : offset + (ulong)delta;
        OffsetText = next.ToString("X", CultureInfo.InvariantCulture);
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (SelectedRegion is not { } region) return;
        if (!TryParseOffset(WriteAddressText, out var address))
        {
            Report("Write address must be hexadecimal.", true);
            return;
        }

        var tokens = WriteBytesText.Split([' ', ',', '-', ':'], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                Report($"\"{tokens[i]}\" is not a valid hex byte.", true);
                return;
            }
        }

        if (bytes.Length == 0)
        {
            Report("Enter at least one byte to write.", true);
            return;
        }

        var (success, error) = await router.WriteDriverRegionAsync(region, address, bytes).ConfigureAwait(true);
        if (success)
        {
            Report($"Wrote {bytes.Length} byte(s) at {address:X}.", false);
            await RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            Report(error ?? "Could not write memory.", true);
        }
    }

    private void Report(string text, bool isError)
    {
        Message = text;
        MessageIsError = isError;
    }

    private static bool TryParseOffset(string text, out ulong offset)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
    }

    private static string Format(ulong start, ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        for (var row = 0; row < data.Length; row += BytesPerRow)
        {
            var line = data.Slice(row, Math.Min(BytesPerRow, data.Length - row));
            text.Append((start + (ulong)row).ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < BytesPerRow; i++)
            {
                text.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(' ');
            }

            text.Append(' ');
            foreach (var b in line) text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            text.Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }
}
