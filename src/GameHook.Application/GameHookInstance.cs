using GameHook.Domain;
using GameHook.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameHook.Application
{
    public class Mapper
    {
        public Mapper(string filesystemId, MapperMetadata metadata, IEnumerable<GameHookProperty> properties, IDictionary<string, IEnumerable<GlossaryItem>> glossary)
        {
            FilesystemId = filesystemId;
            Metadata = metadata;
            Properties = properties;
            Glossary = glossary;
        }

        public string FilesystemId { get; init; }
        public MapperMetadata Metadata { get; init; }
        public IEnumerable<GameHookProperty> Properties { get; init; }
        public IDictionary<string, IEnumerable<GlossaryItem>> Glossary { get; init; }

        public GameHookProperty GetPropertyByPath(string path)
        {
            return Properties.Single(x => x.Path == path);
        }
    }

    public class MapperMetadata
    {
        public int SchemaVersion { get; init; } = 0;
        public Guid Id { get; init; } = Guid.Empty;
        public string GameName { get; init; } = string.Empty;
        public string GamePlatform { get; init; } = string.Empty;
    }

    // TODO: Use LastReadException instead.
    public enum LastReadHints
    {
        OK,
        DriverTimeout,
        DriverFailure,
        PropertyFailure
    }

    public class GameHookInstance
    {
        private const int LOAD_TIMEOUT_MS = 120 * 10;
        private ILogger<GameHookInstance> Logger { get; }
        private IMapperFilesystemProvider MapperFilesystemProvider { get; }
        private IClientNotifier ClientNotifier { get; }
        public bool Initalized { get; private set; } = false;
        public DateTime? LastSuccessfulRead { get; private set; }
        public LastReadHints LastReadHint { get; private set; }
        public Exception? LastReadException { get; private set; }
        public IGameHookDriver? Driver { get; private set; }
        public Mapper? Mapper { get; private set; }
        public IPlatformOptions? PlatformOptions { get; private set; }

        public GameHookInstance(ILogger<GameHookInstance> logger, IMapperFilesystemProvider provider, IClientNotifier clientNotifier)
        {
            Logger = logger;
            MapperFilesystemProvider = provider;
            ClientNotifier = clientNotifier;

            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    if (Initalized)
                    {
                        await Read();
                    }

                    await Task.Delay(5);
                }
            }, TaskCreationOptions.LongRunning);
        }

        public IPlatformOptions GetPlatformOptions() => PlatformOptions ?? throw new Exception("PlatformOptions is null.");

        public void ResetState()
        {
            Initalized = false;
            LastSuccessfulRead = null;

            Driver = null;
            Mapper = null;
            PlatformOptions = null;
        }

        public void Load(IGameHookDriver driver, string mapperId)
        {
            ResetState();

            try
            {
                Logger.LogInformation("Loading GameHook instance...");

                Driver = driver;
                Mapper = MapperFactory.ReadMapper(this, MapperFilesystemProvider, mapperId);
                PlatformOptions = Mapper.Metadata.GamePlatform switch
                {
                    "NES" => new NES_PlatformOptions(),
                    "SNES" => new SNES_PlatformOptions(),
                    "GB" => new GB_PlatformOptions(),
                    "GBA" => new GBA_PlatformOptions(),
                    _ => throw new Exception($"Unknown game platform {Mapper.Metadata.GamePlatform}.")
                };

                Initalized = true;

                // The loop will begin reading memory, wait until a successful run has completed.
                SpinWait.SpinUntil(() => LastSuccessfulRead != null, TimeSpan.FromMilliseconds(LOAD_TIMEOUT_MS));

                if (LastSuccessfulRead == null)
                {
                    ResetState();

                    if (LastReadHint == LastReadHints.DriverFailure) Logger.LogError("Instance could not successfully load due to a driver error.");
                    else if (LastReadHint == LastReadHints.DriverTimeout) Logger.LogError("Instance could not successfully load due to a driver timeout.");
                    else if (LastReadHint == LastReadHints.PropertyFailure) Logger.LogError("Instance could not successfully load due to a property translation error.");
                    else Logger.LogError("Instance could not successfully load due to an error.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An unknown error occured when loading the mapper file.");
                ResetState();
            }
        }

        public async Task Read()
        {
            if (Driver == null) throw new Exception("Driver is null.");
            if (PlatformOptions == null) throw new Exception("Platform options are null.");
            if (Mapper == null) throw new Exception("Mapper is null.");

            ReadBytesResult? driverResult = null;

            try
            {
                driverResult = await Driver.ReadBytes(PlatformOptions.Ranges);
            }
            catch (Exception ex)
            {
                if (ex is DriverTimeoutException) LastReadHint = LastReadHints.DriverTimeout;
                else if (ex is DriverDisconnectedException) LastReadHint = LastReadHints.DriverFailure;
                else if (ex is DriverShutdownException) LastReadHint = LastReadHints.DriverFailure;

                // The instance could have already uninitalized, so don't log an error.
                if (Initalized)
                {
                    Logger.LogError(ex, "Instance could not read from driver.");
                    await ClientNotifier.SendDriverError(new Domain.DTOs.ProblemDetailsForClientDTO() { Title = "Driver error.", Detail = ex.Message });
                }
            }

            if (driverResult == null)
            {
                return;
            }

            Parallel.ForEach(Mapper.Properties, x =>
            {
                try
                {
                    x.Process(driverResult);
                }
                catch (Exception ex)
                {
                    LastReadHint = LastReadHints.PropertyFailure;
                    Logger.LogError(ex, $"Unable to translate property {x.Path}.");
                }
            });

            LastSuccessfulRead = DateTime.UtcNow;
        }
    }
}
