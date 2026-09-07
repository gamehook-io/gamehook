using Gamehook.Domain.Interface;
using Gamehook.Infrastructure.Drivers;
using Microsoft.Extensions.DependencyInjection;

namespace Gamehook.Infrastructure;

internal sealed class DriverFactory(IServiceProvider services, IEnumerable<DriverRegistration> registrations) : IDriverFactory
{
    private readonly IServiceProvider services = services;
    private readonly IReadOnlyDictionary<string, DriverRegistration> registrations = registrations
        .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public IDriver Create(string name, string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!registrations.TryGetValue(name, out var registration))
        {
            throw new NotSupportedException($"Unsupported driver '{name}'.");
        }

        return registration.Factory(services, sourcePath);
    }
}

internal sealed class MapperFactory(IServiceProvider services, IDriverFactory drivers) : IMapperFactory
{
    public IMapper Create(string mapperPath, string driverName, string? driverSourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        var driver = drivers.Create(driverName, driverSourcePath);
        try
        {
            return new Mapper(mapperPath, driver, services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Mapper>>());
        }
        catch
        {
            (driver as IDisposable)?.Dispose();
            throw;
        }
    }
}
