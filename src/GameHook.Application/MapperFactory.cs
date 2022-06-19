using GameHook.Domain.Interfaces;
using System.ComponentModel.DataAnnotations;
using YamlDotNet.Serialization;

namespace GameHook.Application
{
    record YamlRoot
    {
        public YamlMeta meta { get; init; }
        public IDictionary<object, object> properties { get; init; }
        public IDictionary<string, IDictionary<object, dynamic>> macros { get; init; }
        public IDictionary<string, IDictionary<uint, dynamic>> glossary { get; init; }
    }

    record YamlMeta
    {
        public int schemaVersion { get; init; }
        public Guid id { get; init; }
        public string gameName { get; init; }
        public string gamePlatform { get; init; }
    }

    record MacroEntry
    {
        public string type { get; init; }
        public int? address { get; init; }
        public string macro { get; init; }
        public string? reference { get; init; }
        public int? length { get; init; }
    }

    public static class MapperFactory
    {
        public static async Task<Mapper> ReadMapper(IMapperFilesystemProvider provider, string mapperId)
        {
            if (string.IsNullOrEmpty(mapperId))
            {
                throw new ArgumentException("ID was NULL or empty.", nameof(mapperId));
            }

            // Get the file path from the filesystem provider.
            var mapperFile = provider.MapperFiles.SingleOrDefault(x => x.Id == mapperId) ??
                throw new Exception($"Unable to determine a mapper with the ID of {mapperId}.");

            if (File.Exists(mapperFile.AbsolutePath) == false)
            {
                throw new FileNotFoundException($"File was not found in the {mapperFile.Type} mapper folder.", mapperFile.DisplayName);
            }

            var contents = await File.ReadAllTextAsync(mapperFile.AbsolutePath);
            var deserializer = new DeserializerBuilder().Build();
            var data = deserializer.Deserialize<YamlRoot>(contents);

            if (data.meta.id == Guid.Empty)
            {
                throw new ValidationException("Mapper ID is not defined in file metadata.");
            }

            // Load metadata.
            var metadata = new MapperMetadata()
            {
                SchameVersion = data.meta.schemaVersion,
                Id = data.meta.id,
                GameName = data.meta.gameName,
                GamePlatform = data.meta.gamePlatform
            };

            // Load properties.
            var properties = new List<GameHookPropertyNEW>();

            // Load glossary.
            var glossary = new Dictionary<string, IEnumerable<GlossaryItem>>();
            foreach (var x in data.glossary)
            {
                var list = new List<GlossaryItem>();

                if (x.Value != null)
                {
                    foreach (var y in x.Value)
                    {
                        list.Add(new GlossaryItem(y.Key, y.Value));
                    }
                }

                glossary.Add(x.Key, list);
            }

            return new Mapper(metadata, properties, glossary);
        }
    }
}
