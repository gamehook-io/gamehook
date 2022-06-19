using GameHook.Domain.Interfaces;

namespace GameHook.Domain.GameHookProperties
{
    public class UnsignedIntegerProperty : GameHookProperty<uint?>
    {
        public UnsignedIntegerProperty(IGameHookContainer mapper, string identifier, PropertyFields fields)
            : base(mapper, identifier, fields)
        {
        }

        protected override byte[] FromValue(uint? value)
        {
            throw new NotImplementedException();
        }

        protected override uint? ToValue(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            if (PlatformOptions.EndianType == EndianTypes.BigEndian)
                Array.Reverse(bytes);

            byte[] value = new byte[8];
            Array.Copy(bytes, value, bytes.Length);

            return BitConverter.ToUInt32(value, 0);
        }
    }
}