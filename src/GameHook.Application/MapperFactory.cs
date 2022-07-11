using GameHook.Domain;
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

    record MacroPointer
    {
        public MemoryAddress Address { get; set; }
    }

    public static class MapperFactory
    {
        public static Mapper ReadMapper(GameHookInstance instance, IMapperFilesystemProvider provider, string filesystemId)
        {
            if (string.IsNullOrEmpty(filesystemId))
            {
                throw new ArgumentException("ID was NULL or empty.", nameof(filesystemId));
            }

            // Get the file path from the filesystem provider.
            var mapperFile = provider.MapperFiles.SingleOrDefault(x => x.Id == filesystemId) ??
                throw new Exception($"Unable to determine a mapper with the ID of {filesystemId}.");

            if (File.Exists(mapperFile.AbsolutePath) == false)
            {
                throw new FileNotFoundException($"File was not found in the {mapperFile.Type} mapper folder.", mapperFile.DisplayName);
            }

            var contents = File.ReadAllText(mapperFile.AbsolutePath);
            var deserializer = new DeserializerBuilder().Build();
            var data = deserializer.Deserialize<YamlRoot>(contents);

            if (data.meta.id == Guid.Empty)
            {
                throw new ValidationException("Mapper ID is not defined in file metadata.");
            }

            // Load metadata.
            var metadata = new MapperMetadata()
            {
                SchemaVersion = data.meta.schemaVersion,
                Id = data.meta.id,
                GameName = data.meta.gameName,
                GamePlatform = data.meta.gamePlatform
            };

            // Load properties.
            var properties = new List<GameHookProperty>();

            TranserveMapperFile(instance, data, properties, data.properties, null, null);

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

            return new Mapper(filesystemId, metadata, properties, glossary);
        }

        private static void TranserveMapperFile(GameHookInstance instance, YamlRoot root, List<GameHookProperty> properties, IDictionary<object, object> source, string? key, MacroPointer? macroPointer)
        {
            var insideMacro = macroPointer != null;

            try
            {
                if ((insideMacro == false && source.ContainsKey("type") && source.ContainsKey("address")) || (insideMacro == true && source.ContainsKey("type")))
                {
                    ParseProperty(instance, root, properties, source, key, macroPointer);
                }
                else
                {
                    foreach (string childKey in source.Keys)
                    {
                        var definedChildKey = childKey;

                        // Keys that contain only _ symbols are considered merge operators.
                        if (childKey.All(x => x == '_'))
                        {
                            // Setting child key to empty will force it to merge
                            // the transversed properties with it's parent.

                            definedChildKey = string.Empty;
                        }

                        // If the next level is an object.
                        var nextLevel = source[childKey] as IDictionary<object, object>;
                        if (nextLevel != null)
                        {
                            // Create a path based off of a combination of the key and child key.
                            var path = string.Join(".", new string?[] { key, definedChildKey }.Where(s => string.IsNullOrEmpty(s) == false));

                            TranserveMapperFile(instance, root, properties, nextLevel, path, macroPointer);
                        }

                        // If the next level is an array.
                        var nextLevelArray = source[childKey] as IEnumerable<object>;
                        if (nextLevelArray != null)
                        {
                            foreach (var (obj, index) in nextLevelArray.Select((obj, index) => (obj, index)))
                            {
                                // Create a path based off of a combination of the key and child key.
                                var path = string.Join(".", new string?[] { key, definedChildKey, index.ToString() }.Where(s => string.IsNullOrEmpty(s) == false));

                                var nextLevelArrayChild = obj as IDictionary<object, object>;

                                if (nextLevelArrayChild != null)
                                {
                                    TranserveMapperFile(instance, root, properties, nextLevelArrayChild, path, macroPointer);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not parse at {key}. (inside macro? {insideMacro})", ex);
            }
        }

        private static void ParseProperty(GameHookInstance instance, YamlRoot root, List<GameHookProperty> properties, IDictionary<object, object> source, string? key, MacroPointer? macroPointer)
        {
            if (string.IsNullOrEmpty(key))
                throw new Exception("Key cannot be null.");

            // Convert the property object into an IGameHookProperty.
            var type = source["type"].ToString() ?? throw new Exception("Type is required.");
            var size = (source.ContainsKey("size") ? int.Parse(source["size"].ToString() ?? string.Empty) : 1);
            var position = (source.ContainsKey("position") ? int.Parse(source["position"].ToString() ?? string.Empty) : 1);
            var description = source.ContainsKey("description") ? source["description"].ToString() : null;
            var reference = source.ContainsKey("reference") ? source["reference"].ToString() : null;
            var macro = source.ContainsKey("macro") ? source["macro"].ToString() : null;
            var offset = (int?)(source.ContainsKey("offset") ? int.Parse(source["offset"].ToString() ?? string.Empty) : null);
            var preprocessor = source.ContainsKey("preprocessor") ? source["preprocessor"].ToString() : null;
            var expression = source.ContainsKey("expression") ? source["expression"].ToString() : null;

            MemoryAddress address;
            if (macroPointer != null)
            {
                if (source.ContainsKey("offset") == false || string.IsNullOrEmpty(source["offset"].ToString()))
                    throw new Exception($"Property {key} is missing a required field: offset.");

                address = (MemoryAddress)(macroPointer.Address + (offset ?? 0));
            }
            else
            {
                if (source.ContainsKey("address") == false || string.IsNullOrEmpty(source["address"].ToString()))
                    throw new Exception($"Property {key} is missing a required field: address.");

                address = (source["address"]?.ToString() ?? string.Empty).FromHexdecimalStringToUint();
            }

            if (type == "macro")
            {
                var nextLevel = root.macros[macro ?? throw new Exception($"Property {key} is missing a required field: macro.")];
                TranserveMapperFile(instance, root, properties, nextLevel, key, new MacroPointer() {  Address = (MemoryAddress)address });
            }
            else
            {
                var variables = new GameHookMapperVariables()
                {
                    Path = key,

                    Preprocessor = preprocessor,
                    Type = type,
                    Address = address,
                    Size = size,
                    Position = position,
                    Reference = reference,
                    Expression = expression,
                    Description = description
                };

                properties.Add(new GameHookProperty(instance, variables));
            }
        }
    }
}
