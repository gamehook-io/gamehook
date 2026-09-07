using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure;

public sealed record DriverRegistration(
    string Name,
    Func<IServiceProvider, string?, IDriver> Factory);
