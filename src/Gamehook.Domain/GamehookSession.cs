using Gamehook.Domain.Interface;
using Gamehook.Domain.Logic;
using Gamehook.Domain.NativeProcessors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gamehook.Domain;

public sealed record PropertyChange(string Path, object? Value, byte[] Bytes);

// Owns the connect/poll/read state machine shared by every host (Avalonia UI and REST API): load a
// mapper+driver, poll it on an interval, and surface read failures, driver connection drops, and
// missing-region data warnings as plain state a host can bind to.
public sealed class GamehookSession : IDisposable
{
    private readonly IMapperFactory mapperFactory;
    private readonly ILogger<GamehookSession> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? pollingCts;
    // Interval a host asked to poll at. Kept separately from pollingCts so disabling continuous
    // read mode can stop the loop without forgetting it, and re-enabling it resumes at the same
    // cadence.
    private TimeSpan? requestedPollingInterval;
    private int generation;
    private bool disposed;

    public IMapper? Mapper { get; private set; }

    public bool IsConnecting { get; private set; }

    public string Status { get; private set; } = "Choose a driver and mapper to load.";

    public string? DataWarning { get; private set; }

    public string? ConnectionWarning { get; private set; }

    public bool IsConnected => Mapper is not null && !Status.StartsWith("Error:", StringComparison.Ordinal);

    // Continuous read mode: poll the driver continuously and push changes to subscribers. When
    // off, the poll loop is stopped and the driver is only read on demand (see ReadOnDemandAsync);
    // GamehookRouter refuses writes and hosts are expected to refuse websocket subscriptions.
    public bool IsContinuousReadEnabled { get; private set; } = true;

    public event Action<bool>? ContinuousReadChanged;

    // Fires after any state change (load start/end, a read tick, a warning changing).
    public event Action? Changed;

    // Fires after every successful read tick with only the properties whose value/bytes changed
    // since the previous tick (empty on a tick with no changes; not fired at all on a failed read).
    // A websocket (or any other push transport) subscribes to this instead of re-diffing Properties
    // itself on a timer.
    public event Action<IReadOnlyList<PropertyChange>>? PropertiesChanged;

    private Dictionary<string, (object? Value, byte[] Bytes)> lastSnapshot = new(StringComparer.Ordinal);

    public GamehookSession(IMapperFactory mapperFactory, ILogger<GamehookSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mapperFactory);
        this.mapperFactory = mapperFactory;
        this.logger = logger ?? NullLogger<GamehookSession>.Instance;
    }

    public async Task<bool> LoadAsync(string mapperPath, string driverName, string? driverSourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        ObjectDisposedException.ThrowIf(disposed, this);

        Unload();
        var loadGeneration = generation;
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        refreshGate.Release();
        if (loadGeneration != generation) return false;
        DataWarning = null;
        ConnectionWarning = null;
        IsConnecting = true;
        Changed?.Invoke();

        try
        {
            var mapper = mapperFactory.Create(mapperPath, driverName, driverSourcePath);
            Mapper = mapper;
            AttachNativeProcessor(mapper);
            if (!await RefreshAsync(cancellationToken).ConfigureAwait(false))
            {
                if (loadGeneration == generation) UnloadFailedMapper();
                return false;
            }

            if (loadGeneration != generation) return false;

            Status = $"Loaded {Path.GetFileName(mapperPath)}.";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (loadGeneration == generation) UnloadFailedMapper();
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
            or TimeoutException or System.Xml.XmlException or FormatException or OverflowException or InvalidOperationException
            or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            if (loadGeneration != generation) return false;
            UnloadFailedMapper();
            Status = FormatExceptionStatus(ex);
            logger.LogWarning(ex, "Failed to load mapper {Mapper} with driver {Driver}.", mapperPath, driverName);
            return false;
        }
        finally
        {
            if (loadGeneration == generation)
            {
                IsConnecting = false;
                Changed?.Invoke();
            }
        }
    }

    // Safe to call re-entrantly (e.g. a host's own timer racing a manual "refresh now"): a refresh
    // already in flight makes this a no-op instead of racing the same driver read.
    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(waitForInFlightRead: false, cancellationToken);

    // A point-in-time read for when continuous read mode is off: unlike RefreshAsync, waits for an
    // in-flight read instead of skipping, so a caller always gets values read at (or after) its request.
    public Task<bool> ReadOnDemandAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(waitForInFlightRead: true, cancellationToken);

    private async Task<bool> RefreshCoreAsync(bool waitForInFlightRead, CancellationToken cancellationToken)
    {
        if (Mapper is not { } activeMapper)
        {
            return false;
        }

        if (waitForInFlightRead)
        {
            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            if (!ReferenceEquals(Mapper, activeMapper)) return false;
            bool readSucceeded;
            try
            {
                readSucceeded = await activeMapper.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(Mapper, activeMapper)) return false;
                if (readSucceeded) DataWarning = ComputeDataWarning(activeMapper);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(Mapper, activeMapper)) return false;
                // Stop the loop but keep the requested interval, so toggling continuous read mode back on
                // retries the read instead of leaving the session stuck until a reload.
                StopPollLoop();
                Status = FormatExceptionStatus(ex);
                logger.LogError(ex, "Read failed for mapper {Mapper}.", activeMapper.GameName);
                Changed?.Invoke();
                return false;
            }

            if (!ReferenceEquals(Mapper, activeMapper)) return false;
            ConnectionWarning = activeMapper.ConnectionWarning;
            if (!readSucceeded)
            {
                Changed?.Invoke();
                return false;
            }

            Status = $"Read in {activeMapper.LastReadMetrics.Total.TotalMilliseconds:0.00} ms";
            if (PropertiesChanged is { } handler)
            {
                var changes = ComputePropertyChanges(activeMapper);
                if (changes.Count > 0) handler.Invoke(changes);
            }
            Changed?.Invoke();
            return true;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void StartPolling(TimeSpan interval)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        StopPollLoop();
        requestedPollingInterval = interval;
        if (IsContinuousReadEnabled) StartPollLoop(interval);
    }

    public void StopPolling()
    {
        requestedPollingInterval = null;
        StopPollLoop();
    }

    public void SetContinuousRead(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsContinuousReadEnabled == enabled) return;

        IsContinuousReadEnabled = enabled;
        if (!enabled)
        {
            StopPollLoop();
        }
        else if (requestedPollingInterval is { } interval && Mapper is not null)
        {
            StartPollLoop(interval);
        }

        ContinuousReadChanged?.Invoke(enabled);
    }

    private void StartPollLoop(TimeSpan interval)
    {
        pollingCts = new CancellationTokenSource();
        _ = PollLoopAsync(interval, pollingCts.Token);
    }

    private void StopPollLoop()
    {
        pollingCts?.Cancel();
        pollingCts?.Dispose();
        pollingCts = null;
    }

    private async Task PollLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Unload()
    {
        generation++;
        StopPolling();
        UnloadFailedMapper();
        lastSnapshot = new(StringComparer.Ordinal);
        IsConnecting = false;
        DataWarning = null;
        ConnectionWarning = null;
        Status = "Choose a driver and mapper to load.";
        Changed?.Invoke();
    }

    // Driver messages name the driver and what to check; nothing here knows which driver it was.
    private static string FormatExceptionStatus(Exception ex) => $"Error: {ex.Message}";

    private void AttachNativeProcessor(IMapper mapper)
    {
        if (mapper is not INativeProcessorHost { NativeProcessorId: { Length: > 0 } processorId } host) return;
        host.SetNativeProcessor(NativeProcessorFactory.Create(processorId, this));
    }

    // Diffs the mapper's current property values/bytes against the previous tick's snapshot.
    // lastSnapshot is replaced wholesale each tick (not mutated in place) so a property removed by
    // a mapper reload can't linger and get reported as "changed" against a stale entry.
    private List<PropertyChange> ComputePropertyChanges(IMapper mapper)
    {
        var snapshot = new Dictionary<string, (object? Value, byte[] Bytes)>(mapper.Properties.Count, StringComparer.Ordinal);
        var changes = new List<PropertyChange>();

        foreach (var (path, property) in mapper.Properties)
        {
            var bytes = property.Bytes.ToArray();
            var entry = (property.Value, bytes);
            snapshot[path] = entry;

            if (!lastSnapshot.TryGetValue(path, out var previous) ||
                !Equals(previous.Value, entry.Value) || !previous.Bytes.AsSpan().SequenceEqual(bytes))
            {
                changes.Add(new PropertyChange(path, entry.Value, bytes));
            }
        }

        lastSnapshot = snapshot;
        return changes;
    }

    private static string? ComputeDataWarning(IMapper mapper)
    {
        var regionIds = mapper.LastMemorySegments
            .Select(segment => segment.RegionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var requestedRegions = mapper.Properties.Values
            .Select(property => property.BuildRequest()?.RegionId)
            .Where(regionId => regionId is not null)
            .Select(regionId => regionId!)
            .Distinct(StringComparer.Ordinal);

        var missingRegions = requestedRegions.Except(regionIds, StringComparer.Ordinal)
            .OrderBy(regionId => regionId, StringComparer.Ordinal)
            .ToArray();

        return missingRegions.Length > 0
            ? $"Driver returned no data for: {string.Join(", ", missingRegions)}. Affected properties will show as null."
            : null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Unload();
        // In-flight reads still release this gate; it owns no OS handle.
    }

    private void UnloadFailedMapper()
    {
        var previous = Mapper;
        Mapper = null;
        if (previous is IDisposable disposable) _ = RetireMapperAsync(disposable);
    }

    private async Task RetireMapperAsync(IDisposable mapper)
    {
        await refreshGate.WaitAsync().ConfigureAwait(false);
        try { mapper.Dispose(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not dispose mapper driver."); }
        finally { refreshGate.Release(); }
    }
}
