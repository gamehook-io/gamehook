using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure;

// DefaultPort is set for network drivers, whose source is "host", "host:port", or empty for
// localhost on this port.
public sealed record DriverRegistration(
    string Name,
    Func<IServiceProvider, string?, IDriver> Factory,
    int? DefaultPort = null);
