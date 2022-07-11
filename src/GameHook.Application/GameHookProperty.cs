using GameHook.Domain;
using GameHook.Domain.ValueTransformers;

namespace GameHook.Application
{
    public class GameHookMapperVariables
    {
        public string Path { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;
        public MemoryAddress? Address { get; init; }
        public int Size { get; init; } = 1;
        public int? Position { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }

        public string? Expression { get; init; }
        public string? Preprocessor { get; init; }
    }

    public class GameHookPropertyProcessResult
    {
        public bool PropertyUpdated { get; init; }
    }

    public class GameHookProperty
    {
        public GameHookProperty(GameHookInstance gameHookInstance, GameHookMapperVariables mapperVariables)
        {
            GameHookInstance = gameHookInstance;
            MapperVariables = mapperVariables;
        }

        protected GameHookInstance GameHookInstance { get; }
        public GameHookMapperVariables MapperVariables { get; }

        public string Path => MapperVariables.Path;
        public string Type => MapperVariables.Type;
        public int Size => MapperVariables.Size;
        public uint? Address { get; private set; }
        public bool IsDynamicAddress => MapperVariables.Address == null;

        public object? Value { get; private set; }
        public byte[]? Bytes { get; private set; }
        public byte[]? BytesFrozen { get; private set; }
        public bool IsFrozen => BytesFrozen != null;

        public bool IsReadOnly
        {
            get
            {
                if (Address == null) return true;

                return false;
            }
        }

        private static MemoryAddressBlock? GetBlockForAddress(MemoryAddress address, IEnumerable<MemoryAddressBlock> ranges)
        {
            foreach (var range in ranges)
            {
                if (address >= range.StartingAddress && address <= range.EndingAddress)
                {
                    return range;
                }
            }

            return null;
        }

        public async Task<GameHookPropertyProcessResult> Process(IEnumerable<MemoryAddressBlockResult> driverResult, PreprocessorCache preprocessorCache)
        {
            byte[]? oldBytes = Bytes;
            object? oldValue = Value;

            uint? address = null;
            byte[]? bytes = null;

            // Preprocessors.
            if (MapperVariables.Preprocessor != null && MapperVariables.Preprocessor.Contains("data_block_a245dcac"))
            {
                var baseAddress = MapperVariables.Address ?? throw new Exception($"Property {Path} does not have a base address.");
                var substructureOrdering = preprocessorCache.data_block_a245dcac?[MapperVariables.Address ?? 0].SubstructureOrdering
                    ?? throw new Exception($"Unable to determine substructure order for {Path} at address {MapperVariables.Address}.");

                // Do regex.
                // \((\d+),(\d+)\)
                var substructureOrder = 0;
                var offsetStart = 0;
                var offsetEnd = offsetStart + MapperVariables.Size;

                var propertyBlockOrder = substructureOrdering[substructureOrder];

                address = 0;// baseAddress + (propertyBlockOrder * 13) + offsetStart);
                bytes = preprocessorCache.data_block_a245dcac[baseAddress].DecryptedData[offsetStart..offsetEnd];
            }
            else if (MapperVariables.Address != null)
            {
                // Calculate the bytes from the driver range and address property.
                address = MapperVariables.Address;

                var block = GetBlockForAddress((uint)address, GameHookInstance.GetPlatformOptions().Ranges);
                if (block != null)
                {
                    var offsetaddress = address - block.StartingAddress;
                    bytes = driverResult.GetResultWithinRange(address ?? 0).Data.Skip((int)offsetaddress).Take(Size).ToArray();
                }
            }

            // Once preprocessors are ran, we can begin finding the value.
            if (address == null)
            {
                throw new Exception($"Unable to calculate address for property '{Path}'");
            }

            if (bytes == null)
            {
                throw new Exception($"Unable to calculate bytes for property '{Path}'");
            }

            Address = address;
            Bytes = bytes;

            // TODO: HACK: Reverse the endian-ness for certain types.
            var bytes2 = (byte[]) bytes.Clone();
            if (GameHookInstance.GetPlatformOptions().EndianType == EndianTypes.BigEndian)
                Array.Reverse(bytes2);

            // Fast path - if the bytes match, then we can assume the property has not been
            // updated since last poll.
            if (bytes != null && oldBytes != null && bytes.SequenceEqual(oldBytes) == true)
            {
                return new GameHookPropertyProcessResult() { PropertyUpdated = false };
            }

            object? value = Type switch
            {
                "binaryCodedDecimal" => BinaryCodedDecimalTransformer.ToValue(bytes),
                "bitArray" => BitFieldTransformer.ToValue(bytes2),
                "bit" => BitTransformer.ToValue(bytes2, MapperVariables.Position ?? throw new Exception("Missing property variable: Position")),
                "bool" => BooleanTransformer.ToValue(bytes2),
                "int" => IntegerTransformer.ToValue(bytes2),
                "reference" => ReferenceTransformer.ToValue(bytes2, GameHookInstance.GetMapper().Glossary[MapperVariables.Reference ?? throw new Exception("Missing property variable: reference")]),
                "string" => StringTransformer.ToValue(bytes, GameHookInstance.GetMapper().Glossary[MapperVariables.Reference ?? "defaultCharacterMap"]),
                "uint" => UnsignedIntegerTransformer.ToValue(bytes2),
                _ => throw new Exception($"Unknown type defined for {Path}, {Type}")
            };

            Value = value;

            if (value != oldValue)
            {
                if (IsFrozen)
                {
                    await GameHookInstance.GetDriver().WriteBytes(address ?? 0, BytesFrozen ?? throw new Exception("Attempted to force a frozen bytes, but BytesFrozen was NULL."));
                }

                foreach (var notifier in GameHookInstance.ClientNotifiers)
                {
                    await notifier.SendPropertyChanged(Path, Value, Bytes, IsFrozen);
                }

                return new GameHookPropertyProcessResult() { PropertyUpdated = true };
            }
            else
            {
                return new GameHookPropertyProcessResult() { PropertyUpdated = false };
            }
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task WriteValue(object value, bool? freeze)
        {
            if (IsReadOnly) throw new Exception($"Property '{Path}' is read-only and cannot be modified.");

            throw new NotSupportedException();
        }
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

        public async Task WriteBytes(byte[] bytes, bool? freeze)
        {
            if (IsReadOnly) throw new Exception($"Property '{Path}' is read-only and cannot be modified.");

            if (Address == null)
            {
                throw new Exception($"Property '{Path}' address is NULL.");
            }

            if (freeze == true)
            {
                await FreezeProperty(bytes);
            }
            else if (freeze == false)
            {
                await UnfreezeProperty();
            }

            await GameHookInstance.GetDriver().WriteBytes((uint)Address, bytes);
        }

        public async Task FreezeProperty(byte[] bytesFrozen)
        {
            BytesFrozen = bytesFrozen;

            foreach (var notifier in GameHookInstance.ClientNotifiers)
            {
                await notifier.SendPropertyFrozen(Path);
            }
        }

        public async Task UnfreezeProperty()
        {
            BytesFrozen = null;

            foreach (var notifier in GameHookInstance.ClientNotifiers)
            {
                await notifier.SendPropertyUnfrozen(Path);
            }
        }

        public override string ToString()
        {
            if (Bytes == null || Bytes.Any() == false)
            {
                return "N/A";
            }

            return $"{Value} [{string.Join(' ', Bytes)}]" ?? string.Empty;
        }
    }
}
