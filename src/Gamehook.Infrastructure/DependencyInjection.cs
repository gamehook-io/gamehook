using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Models;
using Gamehook.Infrastructure.AppUpdate;
using Gamehook.Infrastructure.MapperUpdate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Gamehook.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddGamehook(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<FilesystemProvider>();
        services.AddSingleton<Drivers.RetroArchConfigurationService>();
        services.AddGamehookLogging(configuration);
        services.AddSingleton<DriverRegistration>(new DriverRegistration(
            Drivers.RetroArchDriver.Name,
            (provider, sourcePath) => new Drivers.RetroArchDriver(sourcePath),
            Drivers.RetroArchDriver.DefaultPort));
        services.AddSingleton<DriverRegistration>(new DriverRegistration(
            Drivers.SuperShuckieDriver.Name,
            (provider, sourcePath) => new Drivers.SuperShuckieDriver(sourcePath),
            Drivers.SuperShuckieDriver.DefaultPort));
        services.AddSingleton<DriverRegistration>(new DriverRegistration(
            Drivers.SaveStateDriver.Name,
            (provider, sourcePath) =>
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    throw new ArgumentException($"State file path is required for {Drivers.SaveStateDriver.Name} driver.", nameof(sourcePath));
                }
                return ActivatorUtilities.CreateInstance<Drivers.SaveStateDriver>(provider, sourcePath);
            }));
        services.TryAddSingleton<IDriverFactory, DriverFactory>();
        services.TryAddSingleton<IMapperFactory, MapperFactory>();
        // Singleton: the REST API (Gamehook.RestApi) and the Avalonia UI must observe the same set of
        // instances. Each instance gets its own GamehookSession (own mapper, driver, poll loop).
        // No two instances may share a driver endpoint; DriverResources says what each driver uses.
        services.TryAddSingleton(provider =>
        {
            var registrations = provider.GetServices<DriverRegistration>().ToArray();
            return new GamehookInstances(
                () => new GamehookSession(
                    provider.GetRequiredService<IMapperFactory>(),
                    provider.GetRequiredService<IDriverFactory>(),
                    provider.GetService<ILogger<GamehookSession>>()),
                (driverName, sourcePath) => DriverResources.Identify(registrations, driverName, sourcePath));
        });
        services.TryAddSingleton<ApiBindStatus>();
        services.TryAddSingleton(provider => SettingsService.Create(
            provider.GetRequiredService<GamehookInstances>(),
            configuration,
            provider.GetService<ILogger<SettingsService>>()));

#if !DEBUG
        // A Debug build is a developer running from source, or a locally-built test binary -
        // never something that should be quietly downloading and applying updates onto itself.
        //
        // Registered (and so started) before MapperUpdateService below: the default Host starts
        // IHostedServices sequentially in registration order, awaiting each StartAsync fully
        // before the next begins. AppUpdateService.StartAsync only awaits the fast "is there an
        // update" check (the actual binary download continues in the background afterward), so
        // this delays mapper resolution by one network round-trip, not by a multi-hundred-MB
        // download - the mapper check then runs right after, same as if the app were up to date.
        services.AddSingleton<AppUpdateStatusProvider>();
        services.AddSingleton<AppUpdateService>();
        services.AddHostedService(provider => provider.GetRequiredService<AppUpdateService>());

        // Same Release-only rule for official mappers: Debug builds only ever use MapperDirectory
        // (see FilesystemProvider.OfficialMappersEnabled), so there is nothing to download.
        services.AddHttpClient(MapperUpdateService.HttpClientName, client =>
        {
            // Required by the GitHub API - requests without a User-Agent are rejected outright.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Gamehook");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<MapperUpdateStatusProvider>();
        services.AddSingleton<MapperUpdateService>();
        services.AddHostedService(provider => provider.GetRequiredService<MapperUpdateService>());
#endif

        return services;
    }

    public static IServiceCollection AddGamehookDriver<TDriver>(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, string?, TDriver> factory,
        int? defaultPort = null)
        where TDriver : class, IDriver
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton(new DriverRegistration(name, (provider, sourcePath) => factory(provider, sourcePath), defaultPort));
        return services;
    }

    // Wires Microsoft.Extensions.Logging's ILogger<T> through to a Serilog rolling file sink under
    // the Gamehook profile directory's "logs" folder, so every consumer (Mapper, FilesystemProvider,
    // the UI/console entry points) gets a real logger via plain DI without depending on Serilog directly.
    private static IServiceCollection AddGamehookLogging(this IServiceCollection services, IConfiguration configuration)
    {
        var logDirectory = FilesystemProvider.GetLogDirectory();
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, "gamehook-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(logging =>
        {
            // Use Serilog sinks for both console and rolling file output.
            logging.ClearProviders();
            logging.AddSerilog(serilogLogger, dispose: true);
        });
        return services;
    }
}
