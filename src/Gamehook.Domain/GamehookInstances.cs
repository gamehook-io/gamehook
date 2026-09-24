using Gamehook.Domain.Interface;

namespace Gamehook.Domain;

/// The set of independent Gamehook instances (one mapper + driver session each) running inside
/// this process - e.g. Pokemon Red over RetroArch in one and Pokemon Blue over SuperShuckie in
/// another. Instances are addressed purely by position: 0..Count-1, in creation order. Removing
/// an instance shifts every later instance down by one. There is always at least one instance.
///
/// Continuous read mode is a process-wide setting: every instance, including ones added later,
/// follows IsContinuousReadEnabled.
///
/// No two instances may use the same driver endpoint (e.g. RetroArch on 127.0.0.1:55355): each
/// instance's GamehookRouter reserves its endpoint here when it loads.
public sealed class GamehookInstances : IDriverReservations, IDisposable
{
    private readonly Func<GamehookSession> sessionFactory;
    private readonly Func<string, string?, DriverResource?> identifyDriver;
    private readonly Dictionary<GamehookRouter, (DriverResource Resource, string DriverName)> reservations = [];
    private readonly object gate = new();
    private readonly List<GamehookRouter> instances = [];
    private bool continuousRead = true;
    private bool disposed;

    /// identifyDriver maps a driver name + source to what it connects to, or null for a driver
    /// that can be shared freely. Omitted, no driver is exclusive.
    public GamehookInstances(Func<GamehookSession> sessionFactory, Func<string, string?, DriverResource?>? identifyDriver = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        this.sessionFactory = sessionFactory;
        this.identifyDriver = identifyDriver ?? ((_, _) => null);
        Add();
    }

    // Raised outside the lock, from whichever thread made the change (UI thread or a Kestrel
    // request thread). Index is the instance's position at the time of the change.
    public event Action<int, GamehookRouter>? InstanceAdded;
    public event Action<int, GamehookRouter>? InstanceRemoved;
    public event Action<bool>? ContinuousReadChanged;

    public bool IsContinuousReadEnabled
    {
        get { lock (gate) return continuousRead; }
    }

    public int Count
    {
        get { lock (gate) return instances.Count; }
    }

    public IReadOnlyList<GamehookRouter> Snapshot()
    {
        lock (gate) return instances.ToArray();
    }

    public bool TryGet(int index, out GamehookRouter router)
    {
        lock (gate)
        {
            if (index >= 0 && index < instances.Count)
            {
                router = instances[index];
                return true;
            }
        }

        router = null!;
        return false;
    }

    /// Current index of an instance, or -1 once it has been removed.
    public int IndexOf(GamehookRouter router)
    {
        lock (gate) return instances.IndexOf(router);
    }

    public (int Index, GamehookRouter Router) Add()
    {
        var session = sessionFactory();
        int index;
        GamehookRouter router;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session.SetContinuousRead(continuousRead);
            router = new GamehookRouter(session, this);
            instances.Add(router);
            index = instances.Count - 1;
        }

        InstanceAdded?.Invoke(index, router);
        return (index, router);
    }

    /// Removes and disposes the instance at index. Refuses to remove the last remaining instance.
    public bool Remove(int index, out string? error)
    {
        GamehookRouter router;
        lock (gate)
        {
            if (index < 0 || index >= instances.Count)
            {
                error = $"Instance {index} does not exist.";
                return false;
            }

            if (instances.Count == 1)
            {
                error = "The last instance cannot be removed.";
                return false;
            }

            router = instances[index];
            instances.RemoveAt(index);
            reservations.Remove(router);
        }

        InstanceRemoved?.Invoke(index, router);
        router.Session.Dispose();
        error = null;
        return true;
    }

    public string? FindConflict(GamehookRouter router, string driverName, string? sourcePath)
    {
        var resource = identifyDriver(driverName, sourcePath);
        lock (gate) return resource is null ? null : FindConflictLocked(router, resource);
    }

    public string? TryReserve(GamehookRouter router, string driverName, string? sourcePath, out bool alreadyHeld)
    {
        var resource = identifyDriver(driverName, sourcePath);
        lock (gate)
        {
            alreadyHeld = resource is not null
                && reservations.TryGetValue(router, out var held)
                && held.Resource.Key == resource.Key;
            if (resource is null)
            {
                reservations.Remove(router);
                return null;
            }

            if (FindConflictLocked(router, resource) is { } conflict) return conflict;
            reservations[router] = (resource, driverName);
            return null;
        }
    }

    public void Release(GamehookRouter router)
    {
        lock (gate) reservations.Remove(router);
    }

    private string? FindConflictLocked(GamehookRouter router, DriverResource resource)
    {
        foreach (var (holder, reservation) in reservations)
        {
            if (!ReferenceEquals(holder, router) && reservation.Resource.Key == resource.Key)
            {
                return $"{resource.Description} is already in use by instance {instances.IndexOf(holder)} ({reservation.DriverName}). Each instance needs its own driver endpoint.";
            }
        }

        return null;
    }

    public void SetContinuousRead(bool enabled)
    {
        GamehookRouter[] current;
        lock (gate)
        {
            if (continuousRead == enabled) return;
            continuousRead = enabled;
            current = instances.ToArray();
        }

        foreach (var router in current) router.Session.SetContinuousRead(enabled);
        ContinuousReadChanged?.Invoke(enabled);
    }

    public void Dispose()
    {
        GamehookRouter[] current;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            current = instances.ToArray();
            instances.Clear();
        }

        foreach (var router in current) router.Session.Dispose();
    }
}
