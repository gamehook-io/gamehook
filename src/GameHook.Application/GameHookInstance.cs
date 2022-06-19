using GameHook.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
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
        public IGameHookDriver Driver { get; }
        public Mapper? Mapper { get; private set; }
        public IPlatformOptions? PlatformOptions { get; private set; }

        public GameHookInstance(IServiceProvider serviceProvider, string mapperFilePath)
        {
            Logger = serviceProvider.GetRequiredService<ILogger<GameHookInstance>>();
            Driver = serviceProvider.GetRequiredService<IGameHookDriver>();

            var mapperFilename = Path.GetFileName(mapperFilePath);

            Logger.LogInformation($"Initializing instance for mapper '{mapperFilename}'...");
            Mapper = MapperFactory.ReadMapper(mapperFilePath);

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
