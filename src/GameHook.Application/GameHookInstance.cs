using GameHook.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameHook.Application
{
    public class Mapper
    {
        public Mapper(MapperMetadata metadata, IEnumerable<GameHookPropertyNEW> properties, IDictionary<string, IEnumerable<GlossaryItem>> glossary)
        {
            Metadata = metadata;
            Properties = properties;
            Glossary = glossary;
        }

        public MapperMetadata Metadata { get; init; }
        public IEnumerable<GameHookPropertyNEW> Properties { get; init; }
        public IDictionary<string, IEnumerable<GlossaryItem>> Glossary { get; init; }
    }

    public class MapperMetadata
    {
        public int SchameVersion { get; init; } = 0;
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

        public GameHookInstance(ILogger<GameHookInstance> logger, IMapperFilesystemProvider provider)
        {
            Logger = logger;
            MapperFilesystemProvider = provider;
        }

        public void Load(IGameHookDriver driver, string mapperId)
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
        }

        public async Task Read()
        {
            await Task.CompletedTask;
        }
    }
}
