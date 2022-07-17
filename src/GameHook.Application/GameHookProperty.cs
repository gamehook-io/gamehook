using GameHook.Domain;
using GameHook.Domain.Preprocessors;
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
        public List<string> FieldsChanged { get; init; } = new List<string>();
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
                if (string.IsNullOrEmpty(MapperVariables.Preprocessor) == false) return true;

                return false;
            }
        }

        private byte[] ReverseBytesIfLE(byte[] bytes)
        {
            if (bytes.Length == 1) { return bytes; }

            var workingBytes = (byte[]) bytes.Clone();

            // Little Endian has the least signifant byte first, so we need to reverse the byte array
            // when translating it to a value.
            if (GameHookInstance.PlatformOptions?.EndianType == EndianTypes.LittleEndian) Array.Reverse(workingBytes);

            return workingBytes;
        }

        public async Task<GameHookPropertyProcessResult> Process(IEnumerable<MemoryAddressBlockResult> driverResult, PreprocessorCache preprocessorCache)
        {
            var result = new GameHookPropertyProcessResult();

            uint? address = null;
            byte[]? bytes = null;

            // Preprocessors.
            if (MapperVariables.Preprocessor != null && MapperVariables.Preprocessor.Contains("data_block_a245dcac"))
            {
                var baseAddress = MapperVariables.Address ?? throw new Exception($"Property {Path} does not have a base address.");
                var decryptedDataBlock = preprocessorCache.data_block_a245dcac?[baseAddress] ?? throw new Exception($"Unable to retrieve data_block_a245dcac for property {Path} and address {Address}.");

                var structureIndex = MapperVariables.Preprocessor.GetIntParameterFromFunctionString(0);
                var offset = MapperVariables.Preprocessor.GetIntParameterFromFunctionString(1);

                var preprocessorResult = Preprocessors.data_block_a245dcac(structureIndex, offset, MapperVariables.Size, decryptedDataBlock);

                address = preprocessorResult.Address;
                bytes = preprocessorResult.Bytes;
            }
            else if (MapperVariables.Address != null)
            {
                // Calculate the bytes from the driver range and address property.
                address = MapperVariables.Address;
                bytes = driverResult.GetAddress(MapperVariables.Address.Value, MapperVariables.Size);
            }

            // Once preprocessors are ran, we can begin finding the value.
            if (address == null)
            {
                throw new Exception($"Unable to retrieve address for property '{Path}'");
            }

            if (bytes == null)
            {
                throw new Exception($"Unable to retrieve bytes for property '{Path}' at address {address.Value.ToHexdecimalString()}. Is the address within the drivers' memory address block ranges?");
            }

            // Determine if we need to reset a frozen property.
            // If this is the case, return early.
            if (bytes != Bytes && IsFrozen)
            {
                await GameHookInstance.GetDriver().WriteBytes(address ?? 0, BytesFrozen ?? throw new Exception("Attempted to force a frozen bytes, but BytesFrozen was NULL."));
                return result;
            }

            object? value;
            if (Address == address && Bytes?.SequenceEqual(bytes) == true)
            {
                // Fast path - if the bytes match, then we can assume the property has not been
                // updated since last poll.

                // Do nothing, we don't need to calculate the new value as
                // the bytes are the same.

                value = Value;
            }
            else
            {
                value = Type switch
                {
                    "binaryCodedDecimal" => BinaryCodedDecimalTransformer.ToValue(bytes),
                    "bitArray" => BitFieldTransformer.ToValue(bytes),
                    "bit" => BitTransformer.ToValue(bytes, MapperVariables.Position ?? throw new Exception("Missing property variable: Position")),
                    "bool" => BooleanTransformer.ToValue(bytes),
                    "int" => IntegerTransformer.ToValue(ReverseBytesIfLE(bytes)),
                    "reference" => ReferenceTransformer.ToValue(ReverseBytesIfLE(bytes), GameHookInstance.GetMapper().Glossary[MapperVariables.Reference ?? throw new Exception("Missing property variable: reference")]),
                    "string" => StringTransformer.ToValue(bytes, GameHookInstance.GetMapper().Glossary[MapperVariables.Reference ?? "defaultCharacterMap"]),
                    "uint" => UnsignedIntegerTransformer.ToValue(ReverseBytesIfLE(bytes)),
                    _ => throw new Exception($"Unknown type defined for {Path}, {Type}")
                };
            }

            if (Address?.Equals(address) == false)
            {
                result.FieldsChanged.Add("address");
            }

            if (Bytes?.SequenceEqual(bytes) == false)
            {
                result.FieldsChanged.Add("bytes");
            }

            // Depending on the data type, we might need to compare values differently.
            if (Type == "bitArray")
            {
                if (Value != null && value != null && ((bool[])Value).SequenceEqual(((bool[])value)) == false)
                {
                    result.FieldsChanged.Add("value");
                }
            }
            else
            {
                if (Value?.Equals(value) == false)
                {
                    result.FieldsChanged.Add("value");
                }
            }

            Address = address;
            Bytes = bytes;
            Value = value;

            if (result.FieldsChanged.Count > 0)
            {
                foreach (var notifier in GameHookInstance.ClientNotifiers)
                {
                    _ = notifier.SendPropertyChanged(Path, Address, Value, Bytes, IsFrozen, result.FieldsChanged.ToArray());
                }
            }

            return result;
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

            await GameHookInstance.GetDriver().WriteBytes((uint)Address, bytes);

            if (freeze == true)
            {
                await FreezeProperty(bytes);
            }
            else if (freeze == false)
            {
                await UnfreezeProperty();
            }
        }

        public async Task FreezeProperty(byte[] bytesFrozen)
        {
            BytesFrozen = bytesFrozen;

            foreach (var notifier in GameHookInstance.ClientNotifiers)
            {
                await notifier.SendPropertyChanged(Path, Address, Value, Bytes, IsFrozen, new string[] { "frozen" });
            }
        }

        public async Task UnfreezeProperty()
        {
            BytesFrozen = null;

            foreach (var notifier in GameHookInstance.ClientNotifiers)
            {
                await notifier.SendPropertyChanged(Path, Address, Value, Bytes, IsFrozen, new string[] { "frozen" });
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
