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
        private ILogger<GameHookInstance> Logger { get; }
        private IMapperFilesystemProvider MapperFilesystemProvider { get; }
        private IEnumerable<IClientNotifier> ClientNotifier { get; }
        public bool Initalized { get; private set; } = false;
        private CancellationTokenSource? ReadLoopToken { get; set; }
        public IGameHookDriver? Driver { get; private set; }
        public Mapper? Mapper { get; private set; }
        public IPlatformOptions? PlatformOptions { get; private set; }
        public IEnumerable<MemoryAddressBlock>? BlocksToRead { get; private set; }

        public GameHookInstance(ILogger<GameHookInstance> logger, IMapperFilesystemProvider provider, IEnumerable<IClientNotifier> clientNotifier)
        {
            Logger = logger;
            MapperFilesystemProvider = provider;
            ClientNotifier = clientNotifier;
        }

        public IPlatformOptions GetPlatformOptions() => PlatformOptions ?? throw new Exception("PlatformOptions is null.");

        public void ResetState()
        {
            if (ReadLoopToken != null && ReadLoopToken.Token.CanBeCanceled)
            {
                ReadLoopToken.Cancel();
            }

            Initalized = false;
            ReadLoopToken = null;

            Driver = null;
            Mapper = null;
            PlatformOptions = null;
            BlocksToRead = null;
        }

        public async void Load(IGameHookDriver driver, string mapperId)
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

                // Calculate the blocks to read from the mapper memory addresses.
                var addressesToWatch = Mapper.Properties.Where(x => x.MapperVariables.Address != null).Select(x => (uint)x.MapperVariables.Address).ToList();
                BlocksToRead = PlatformOptions.Ranges
                                .Where(x => addressesToWatch.Any(y => y.Between(x.StartingAddress, x.EndingAddress)))
                                .ToList();

                Logger.LogInformation($"Requested {BlocksToRead.Count()}/{PlatformOptions.Ranges.Count()} ranges of memory.");
                Logger.LogInformation($"Requested ranges: {string.Join(", ", BlocksToRead.Select(x => x.Name))}");

                await Read();

                Initalized = true;

                // Start the read loop once successfully running once.
                ReadLoopToken = new CancellationTokenSource();
                _ = Task.Run(ReadLoop, ReadLoopToken.Token);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An unknown error occured when loading the mapper file.");

                ResetState();
            }
        }

        public async Task ReadLoop()
        {
            while (ReadLoopToken != null && ReadLoopToken.IsCancellationRequested == false)
            {
                await Read();
                await Task.Delay(1);
            }
        }

        public async Task Read()
        {
            if (Driver == null) throw new Exception("Driver is null.");
            if (PlatformOptions == null) throw new Exception("Platform options are null.");
            if (Mapper == null) throw new Exception("Mapper is null.");
            if (BlocksToRead == null) throw new Exception("BlocksToRead is null.");

            var driverResult = await Driver.ReadBytes(BlocksToRead);

            Parallel.ForEach(Mapper.Properties, async x =>
            {
                try
                {
                    var result = x.Process(driverResult);

                    if (result.PropertyUpdated)
                    {
                        foreach (var notifier in ClientNotifier)
                        {
                            await notifier.SendPropertyChanged(x.Path, x.Value, x.Bytes, x.IsFrozen);
                        }
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            });
        }
    }
}
