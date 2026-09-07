using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using GameHook.Infrastructure.MapperUpdate;
using GameHook.Infrastructure.AppUpdate;
using Avalonia.Threading;
using System.Reflection;

namespace GameHook.UI.Views;

public partial class AboutWindow : Window
{
    private readonly AppUpdateStatusProvider? updateStatus;
    private readonly AppUpdateService? updateService;

    public AboutWindow()
    {
        InitializeComponent();
        AppVersionText.Text = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(AboutWindow).Assembly.GetName().Version?.ToString();
    }

    public AboutWindow(MapperUpdateService mapperUpdateService, AppUpdateStatusProvider? updateStatus = null,
        AppUpdateService? updateService = null) : this()
    {
        this.updateStatus = updateStatus;
        this.updateService = updateService;
        if (updateStatus is not null)
        {
            updateStatus.Changed += OnUpdateStatusChanged;
            Closed += (_, _) => updateStatus.Changed -= OnUpdateStatusChanged;
            OnUpdateStatusChanged();
        }
        // No manifest at all (e.g. a custom MapperDirectory, or nothing downloaded yet) leaves
        // the card hidden. There's no more human-friendly release tag to show now that mappers
        // are pinned/tracked by commit sha (see MapperUpdateService) - a short sha is what's
        // actually meaningful here.
        if (mapperUpdateService.GetInstalledManifest() is { } manifest)
        {
            MapperVersionText.Text = $"{manifest.Reference} ({manifest.CommitSha[..Math.Min(7, manifest.CommitSha.Length)]})";
            MapperVersionCard.IsVisible = true;
        }
    }

    private void OnUpdateStatusChanged() => Dispatcher.UIThread.Post(() =>
    {
        AppUpdatePanel.IsVisible = updateStatus is not null;
        AppUpdateText.Text = updateStatus?.Current.Message;
        RestartUpdateButton.IsVisible = updateStatus?.Current.State == AppUpdateState.ReadyToApply;
    });

    private void RestartUpdateButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        updateService?.ApplyUpdateAndRestart();

    private void Website_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://gamehook.io/") { UseShellExecute = true });

    private void Discord_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://discord.gg/rQbdcZF2AP") { UseShellExecute = true });
}
