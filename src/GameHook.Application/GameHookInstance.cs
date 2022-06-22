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

    public class GameHookInstance
    {
        private ILogger<GameHookInstance> Logger { get; }
        private IMapperFilesystemProvider MapperFilesystemProvider { get; }
        public IGameHookDriver? Driver { get; private set; }
        public Mapper? Mapper { get; private set; }
        public IPlatformOptions? PlatformOptions { get; private set; }
        public bool Initalized { get; private set; } = false;

        public GameHookInstance(ILogger<GameHookInstance> logger, IMapperFilesystemProvider provider)
        {
            Logger = logger;
            MapperFilesystemProvider = provider;
        }

        public async Task Load(IGameHookDriver driver, string mapperId)
        {
            Logger.LogInformation("Initializing GameHook instance...");

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

            await Read();

            Initalized = true;
        }

        public async Task Read()
        {
            if (Driver == null)             throw new Exception("Driver is null.");
            if (PlatformOptions == null)    throw new Exception("Platform options are null.");
            if (Mapper == null) throw new Exception("Mapper is null.");

            var driverResult = await Driver.ReadBytes(PlatformOptions.Ranges);

            foreach (var property in Mapper.Properties)
            {
                property.Process(driverResult.Bytes.First().Value);
            }

            await Task.CompletedTask;
        }
    }
}
