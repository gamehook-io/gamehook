using GameHook.Domain.GameHookProperties;
using GameHook.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using YamlDotNet.Serialization;

namespace GameHook.Domain
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

    public class GameHookContainerFactory : IGameHookContainerFactory
    {
        private ILogger<GameHookContainerFactory> Logger { get; }
        private IMapperFilesystemProvider MapperFilesystemProvider { get; }
        private IGameHookDriver Driver { get; }
        private IEnumerable<IClientNotifier> ClientNotifiers { get; }
        public IGameHookContainer? LoadedMapper { get; private set; }
        public string? LoadedMapperId { get; private set; }

        public GameHookContainerFactory(ILogger<GameHookContainerFactory> logger, IMapperFilesystemProvider mapperFilesystemProvider, IGameHookDriver gameHookDriver, IEnumerable<IClientNotifier> clientNotifiers)
        {
            Logger = logger;
            MapperFilesystemProvider = mapperFilesystemProvider;
            Driver = gameHookDriver;
            ClientNotifiers = clientNotifiers;
        }

        private class MacroPointer
        {
            public MacroPointer(MemoryAddress address)
            {
                Address = address;
            }

            public MemoryAddress Address { get; }
        }
        private void ParseProperty(GameHookContainer container, IDictionary<object, object> source, string? key, MacroPointer? macroPointer)
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

            long address;
            if (macroPointer != null)
            {
                if (source.ContainsKey("offset") == false || string.IsNullOrEmpty(source["offset"].ToString()))
                    throw new Exception($"Property {key} is missing a required field: offset.");

                address = macroPointer.Address + offset ?? 0;
            }
            else
            {
                if (source.ContainsKey("address") == false || string.IsNullOrEmpty(source["address"].ToString()))
                    throw new Exception($"Property {key} is missing a required field: address.");

                address = (source["address"]?.ToString() ?? string.Empty).FromHexdecimalStringToUint();
            }

            var fields = new PropertyFields()
            {
                Type = type,
                Address = (MemoryAddress)address,
                Size = size,
                Position = position,
                Reference = reference,
                Description = description
            };

            if (type == "binaryCodedDecimal")
            {
                container.AddHookProperty(key, new BinaryCodedDecimalProperty(container, key, fields));
            }
            else if (type == "bool")
            {
                container.AddHookProperty(key, new BooleanProperty(container, key, fields));
            }
            else if (type == "bit")
            {
                container.AddHookProperty(key, new BitProperty(container, key, fields));
            }
            else if (type == "bitArray")
            {
                container.AddHookProperty(key, new BitFieldProperty(container, key, fields));
            }
            else if (type == "int")
            {
                container.AddHookProperty(key, new IntegerProperty(container, key, fields));
            }
            else if (type == "uint")
            {
                container.AddHookProperty(key, new UnsignedIntegerProperty(container, key, fields));
            }
            else if (type == "reference")
            {
                container.AddHookProperty(key, new ReferenceProperty(container, key, fields));
            }
            else if (type == "referenceArray")
            {
                container.AddHookProperty(key, new ReferenceArrayProperty(container, key, fields));
            }
            else if (type == "string")
            {
                container.AddHookProperty(key, new StringProperty(container, key, fields));
            }
            else if (type == "macro")
            {
                var nextLevel = container.Macros[macro ?? throw new Exception($"Property {key} is missing a required field: macro.")];
                TransverseProperties(container, nextLevel, key, new MacroPointer((MemoryAddress)address));
            }
            else
            {
                throw new Exception($"Unable to determine type '{type}' when parsing properties of {source}.");
            }
        }

        private void TransverseProperties(GameHookContainer container, IDictionary<object, object> source, string? key, MacroPointer? macroPointer)
        {
            var insideMacro = macroPointer != null;

            try
            {
                if ((insideMacro == false && source.ContainsKey("type") && source.ContainsKey("address")) || (insideMacro == true && source.ContainsKey("type")))
                {
                    ParseProperty(container, source, key, macroPointer);
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

                            TransverseProperties(container, nextLevel, path, macroPointer);
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
                                    TransverseProperties(container, nextLevelArrayChild, path, macroPointer);
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

        public async Task LoadGameMapper(string id)
        {
            Driver.StopWatchingAndReset();

            LoadedMapper = null;
            LoadedMapperId = null;

            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("ID was NULL or empty.", nameof(id));
            }

            var mapperFile = MapperFilesystemProvider.MapperFiles.SingleOrDefault(x => x.Id == id) ??
                throw new Exception($"Unable to determine a mapper with the ID of {id}.");

            if (File.Exists(mapperFile.AbsolutePath) == false)
            {
                throw new FileNotFoundException($"File was not found in the {mapperFile.Type} mapper folder.", mapperFile.DisplayName);
            }

            var contents = await File.ReadAllTextAsync(mapperFile.AbsolutePath);
            var deserializer = new DeserializerBuilder().Build();
            var data = deserializer.Deserialize<YamlRoot>(contents);

            if (data.meta.id == Guid.Empty) throw new ValidationException("Mapper ID is not defined in meta.");

            try
            {
                var meta = new GameHookMapperMeta(SchemaVersion: data.meta.schemaVersion, Id: data.meta.id, GameName: data.meta.gameName, GamePlatform: data.meta.gamePlatform);
                var properties = new Dictionary<string, IGameHookProperty>();

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

                var mapper = new GameHookContainer(Logger, ClientNotifiers, Driver, meta, data.macros, glossary);

                try
                {
                    TransverseProperties(mapper, data.properties, null, null);
                }
                catch (Exception ex)
                {
                    throw new MapperParsingException(mapperFile.DisplayName, ex);
                }

                await mapper.Initialize();

                LoadedMapper = mapper;
                LoadedMapperId = mapperFile.Id;

                foreach (var notifier in ClientNotifiers)
                {
                    await notifier.SendMapperLoaded();
                }
            }
            catch
            {
                Driver.StopWatchingAndReset();

                LoadedMapper = null;
                LoadedMapperId = null;

                throw;
            }
        }

        public async Task ReloadGameMapper()
        {
            if (LoadedMapperId != null)
            {
                await LoadGameMapper(LoadedMapperId);
            }
        }
    }
}