namespace Gamehook.Domain.Interface;

/// What a driver connects to, for telling whether two instances would share it. Key is compared
/// for equality; Description names it in error messages (e.g. "127.0.0.1:55355").
public sealed record DriverResource(string Key, string Description);

/// Keeps two instances from using the same driver endpoint at once (see GamehookInstances). An
/// instance holds its reservation from the start of a load until the instance is removed or it
/// loads against a different endpoint. Unloading keeps it (the instance keeps its selected driver);
/// a failed load gives it up only if the load was what reserved it.
public interface IDriverReservations
{
    /// Error message when another instance holds what driverName/sourcePath would use, else null.
    string? FindConflict(GamehookRouter router, string driverName, string? sourcePath);

    /// Like FindConflict, but on success records the reservation for router (replacing its previous
    /// one). alreadyHeld is true when router already held this same endpoint.
    string? TryReserve(GamehookRouter router, string driverName, string? sourcePath, out bool alreadyHeld);

    void Release(GamehookRouter router);
}
