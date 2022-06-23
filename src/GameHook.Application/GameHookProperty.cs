using GameHook.Domain;
using GameHook.Domain.Interfaces;
using GameHook.Domain.ValueTransformers;

namespace GameHook.Application
{
    public class GameHookMapperVariables
    {
        public string Path { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;
        public uint? Address { get; init; }
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

        public GameHookPropertyProcessResult Process(ReadBytesResult driverResult)
        {
            byte[]? oldBytes = Bytes;
            object? oldValue = Value;
            byte[]? bytes = null;
            object? value;

            // Calculate the required address.
            var address = MapperVariables.Address;

            // Calculate the bytes from the driver range.
            if (address != null)
            {
                // If the address is determined, grab the byte array.
                var block = GetBlockForAddress((uint)address, GameHookInstance.GetPlatformOptions().Ranges);
                if (block != null)
                {
                    var offsetaddress = address - block.StartingAddress;
                    bytes = driverResult.Bytes[block.Name].Skip((int)offsetaddress).Take(Size).ToArray();
                }
            }

            // TODO: HACK: Reverse the endian-ness for certain types.
            if (Type != "string" && Type != "binaryCodedDecimal")
            {
                if (GameHookInstance.GetPlatformOptions().EndianType == EndianTypes.BigEndian)
                    Array.Reverse(bytes);
            }

            // Fast path - if the bytes match, then we can assume the property has not been
            // updated since last poll.
            if (bytes != null && oldBytes != null && bytes.SequenceEqual(oldBytes) == true)
            {
                return new GameHookPropertyProcessResult() { PropertyUpdated = false };
            }

            if (bytes == null)
            {
                throw new Exception($"Unable to calculate bytes for property '{Path}'");
            }

            value = Type switch
            {
                "binaryCodedDecimal" => BinaryCodedDecimalTransformer.ToValue(bytes),
                "bitArray" => BitFieldTransformer.ToValue(bytes),
                "bit" => BitTransformer.ToValue(bytes, MapperVariables.Position ?? throw new Exception("Missing property variable: Position")),
                "bool" => BooleanTransformer.ToValue(bytes),
                "int" => IntegerTransformer.ToValue(bytes),
                "reference" => ReferenceTransformer.ToValue(bytes, GameHookInstance.Mapper.Glossary[MapperVariables.Reference ?? throw new Exception("Missing property variable: reference")]),
                "string" => StringTransformer.ToValue(bytes, GameHookInstance.Mapper.Glossary[MapperVariables.Reference ?? "defaultCharacterMap"]),
                "uint" => UnsignedIntegerTransformer.ToValue(bytes),
                _ => throw new Exception($"Unknown type defined for {Path}, {Type}")
            };

            Bytes = bytes;
            Value = value;

            return new GameHookPropertyProcessResult() { PropertyUpdated = value != oldValue };
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
                BytesFrozen = bytes;
            }

            await GameHookInstance.Driver.WriteBytes((uint)Address, bytes);
        }

        public void UnfreezeProperty()
        {
            BytesFrozen = null;
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
