using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.AppUpdate;
using Gamehook.UI;
using Gamehook.UI.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using Velopack;

#if !DEBUG
// Handle installer lifecycle events before recording normal application launches.
VelopackApp.Build().Run();

// `--recover` (a Start Menu/desktop "Gamehook (Repair)" shortcut) always goes straight to
// RecoveryRunner - no Host, no DI, no Avalonia - so it still works even if none of that can start.
if (args.Contains("--recover"))
{
    return RecoveryRunner.Run(args);
}

// If the last few launches never made it to App.MarkHealthy() (see App.axaml.cs), a bad build is
// presumably crash-looping - stop trying to start it and fall back to the same recovery path
// `--recover` uses, so a shipped-bad-binary recovers itself instead of leaving users stuck.
if (CrashGuard.RecordLaunchAttempt() > CrashGuard.CrashLoopThreshold)
{
    // If recovery is unavailable, allow normal startup so transient failures do not
    // permanently lock users out of unpackaged or offline installations.
    RecoveryRunner.Run(args);
    CrashGuard.MarkHealthy();
}

#endif

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

#if !DEBUG
// Loaded last, so it overrides the bundled appsettings.json (and env vars/command-line args)
// for every key it sets, not just the Mapper:* ones - lets a user pin/override anything without
// touching the install. Optional: silently absent for everyone who hasn't dropped one in.
// Debug-only build guard, same reasoning as the updater itself (see CrashGuard/AppUpdateService)
// - a developer's local run shouldn't pick up whatever's sitting in their real Gamehook profile.
var profileDirectory = FilesystemProvider.GetGamehookProfileDirectory(builder.Configuration);
builder.Configuration.AddJsonFile(Path.Combine(profileDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
#endif

builder.Services.AddGamehook(builder.Configuration);
builder.Services.AddSingleton<MainWindowViewModel>();
using var host = builder.Build();

// Blocking: runs registered IHostedServices to completion, in registration order, before the
// window is created - AppUpdateService's check (Release only) first, then MapperUpdateService -
// so MainWindowViewModel's initial FilesystemProvider.GetMappers() call sees freshly downloaded
// mappers rather than racing the update.
host.Start();

App.Services = host.Services;

// StartWithClassicDesktopLifetime hands the main thread to Avalonia's own message loop, which
// doesn't observe Ctrl+C/SIGTERM on its own - without this, `dotnet run` in a terminal would sit
// there ignoring Ctrl+C until the window is closed by hand.
using var sigintRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
using var sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);

BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

host.StopAsync().GetAwaiter().GetResult();
return 0;

static void RequestShutdown(PosixSignalContext context)
{
    context.Cancel = true;
    Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    });
}

static AppBuilder BuildAvaloniaApp()
{
    var builder = AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont();

#if DEBUG
    builder.LogToTrace();
#endif

    return builder;
}
