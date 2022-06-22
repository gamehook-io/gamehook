using GameHook.Domain;
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

        public GameHookPropertyProcessResult Process(byte[] data)
        {
            // Fast path - if the bytes match, then we can assume the property has not been
            // updated since last poll.
            if (Address != null && Bytes != null && data.Get((uint)Address, Size).SequenceEqual(Bytes) == true)
            {
                return new GameHookPropertyProcessResult() { PropertyUpdated = true };
            }

            if (Address != null)
            {
                var bytes = data.Get((uint)Address, Size);
                Bytes = bytes;
            }

            if (Bytes == null) throw new Exception($"Unable to calculate bytes for property '{Path}'");

            if (Type == "int") Value = IntegerTransformer.ToValue(Bytes);
            else Value = "no value yet";

            return new GameHookPropertyProcessResult() { PropertyUpdated = true };
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
