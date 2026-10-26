using System.Reflection;
using Gamehook.Infrastructure;
using Gamehook.RestApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gamehook.Android;

/// The Gamehook engine and its local Web API, running inside this app's process: the same
/// composition the desktop app uses (AddGamehook + GamehookApiHostedService), minus the desktop UI.
/// Owned by <see cref="GamehookService"/>, which keeps the process alive in the background.
internal static class GamehookRuntime
{
    public const int Port = 61358;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IHost? host;

    /// The running host's services (null unless running); the UI resolves the shared engine from here.
    public static IServiceProvider? Services => host?.Services;

    public static RuntimeState State { get; private set; } = new(RuntimeStatus.Stopped, null);

    public static string ApiUrl => $"http://127.0.0.1:{Port}";

    public static event Action<RuntimeState>? StateChanged;

    public static async Task StartAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (host is not null) return;
            Publish(RuntimeStatus.Starting, null);

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            var settings = new Dictionary<string, string?>
            {
                ["Port"] = Port.ToString(),
                ["ContinuousRead"] = "true",
                ["RetroArch:AllowMultiFrameReads"] = "true",
                ["OverlaySettingsOrigins:0"] = "https://gamehook.io",
                ["OverlaySettingsOrigins:1"] = "https://*.gamehook.io",
                ["Mapper:Source"] = "Direct",
                ["Mapper:Branch"] = "main",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Warning",
                ["Serilog:MinimumLevel:Override:Gamehook.Infrastructure.Mapper"] = "Information",
            };

            // A release build pins the mapper commit it was built against (see the csproj); a
            // local build tracks the latest commit on the branch.
            if (BuildMetadata("MapperCommit") is { } commit)
            {
                settings["Mapper:Commit"] = commit;
                settings["Mapper:CommitDate"] = BuildMetadata("MapperCommitDate");
                settings["Mapper:Source"] = "Proxy";
            }

            builder.Configuration.AddInMemoryCollection(settings);
            RestApi.ApiVersion.Current = AppVersionName();

            builder.Services.AddGamehook(builder.Configuration);
            builder.Services.AddSingleton<ApiBindStatus>();
            builder.Services.AddHostedService<GamehookApiHostedService>();

            var built = builder.Build();
            built.Services.GetRequiredService<SettingsService>();
            built.Services.GetRequiredService<FilesystemProvider>().EnsureUserMapperDirectory();

            // Runs the hosted services: downloads official mappers, then starts the Web API.
            await built.StartAsync().ConfigureAwait(false);
            host = built;

            var bindError = built.Services.GetRequiredService<ApiBindStatus>().Error;
            Publish(bindError is null ? RuntimeStatus.Running : RuntimeStatus.Failed, bindError);
        }
        catch (Exception ex)
        {
            host?.Dispose();
            host = null;
            Publish(RuntimeStatus.Failed, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (host is null) return;
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                host.Dispose();
                host = null;
                Publish(RuntimeStatus.Stopped, null);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? BuildMetadata(string key) =>
        typeof(GamehookRuntime).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value is { Length: > 0 } value ? value : null;

    private static string AppVersionName()
    {
        var context = global::Android.App.Application.Context;
        return context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName ?? "0";
    }

    private static void Publish(RuntimeStatus status, string? error)
    {
        State = new RuntimeState(status, error);
        StateChanged?.Invoke(State);
    }
}

internal enum RuntimeStatus { Stopped, Starting, Running, Failed }

internal sealed record RuntimeState(RuntimeStatus Status, string? Error);
