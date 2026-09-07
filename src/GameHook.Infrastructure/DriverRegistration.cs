using GameHook.Domain.Interface;

namespace GameHook.Infrastructure;

public sealed record DriverRegistration(
    string Name,
    Func<IServiceProvider, string?, IDriver> Factory);
