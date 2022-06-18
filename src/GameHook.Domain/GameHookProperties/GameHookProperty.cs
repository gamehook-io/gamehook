using GameHook.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameHook.Domain.GameHookProperties
{
    public record PropertyFields
    {
        public string Type { get; init; } = string.Empty;
        public MemoryAddress Address { get; init; }
        public int Size { get; init; } = 1;
        public int? Position { get; init; }
        public string? Reference { get; init; }
        public string? Description { get; init; }
        public string? Preprocessor { get; init; }
    }

    public abstract class GameHookProperty<T> : IGameHookProperty
    {
        public GameHookProperty(IGameHookContainer mapper, string identifier, PropertyFields fields)
        {
            Mapper = mapper;
            Identifier = identifier;
            Fields = fields;

            Bytes = Array.Empty<byte>();
        }

        protected IGameHookContainer Mapper { get; }
        protected ILogger Logger => Mapper.Logger;
        protected IGameHookDriver Driver => Mapper.Driver;
        protected IPlatformOptions PlatformOptions => Mapper.PlatformOptions;

        public string Identifier { get; private set; }
        public PropertyFields Fields { get; private set; }
        public string Type => Fields.Type;
        public MemoryAddress Address => Fields.Address;
        public int Size => Fields.Size;

        public T? Value { get; private set; }
        object? IGameHookProperty.Value => Value;
        public byte[] Bytes { get; private set; }
        public bool Frozen => FreezeToBytes != null;
        public byte[]? FreezeToBytes { get; private set; }

        protected abstract byte[]? FromValue(T? value);
        protected abstract T? ToValue(byte[] bytes);

        public void OnDriverMemoryChanged(byte[] bytes)
        {
            Bytes = bytes;

            var oldValue = Value;
            var newValue = ToValue(bytes);

            if (Equals(oldValue, newValue) == false)
            {
                Value = newValue;
            }
        }

        public async Task WriteValue(T? value, bool? freeze)
        {
            var fromValue = FromValue(value);
            if (fromValue == null)
            {
                throw new Exception("Cannot write null value.");
            }
            else
            {
                await WriteBytes(fromValue, freeze);
            }
        }

        public async Task WriteBytes(byte[] values, bool? freeze)
        {
            if (values.Length > Fields.Size)
            {
                throw new InvalidOperationException($"Attempted to write past the property length of {Fields.Size} the values '{string.Join(' ', values.Select(x => x.ToHexdecimalString()))}'.");
            }

            if (freeze == true)
            {
                FreezeToBytes = values;
            }
            else
            {
                FreezeToBytes = null;
            }

            if (values.Length != 0)
            {
                await Driver.WriteBytes(Fields.Address, values);
            }
        }

        public void Unfreeze()
        {
            FreezeToBytes = null;
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