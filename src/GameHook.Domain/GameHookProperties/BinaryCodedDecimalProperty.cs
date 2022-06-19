using GameHook.Domain.Interfaces;

namespace GameHook.Domain.GameHookProperties
{
    public class BinaryCodedDecimalProperty : GameHookProperty<int?>
    {
        public BinaryCodedDecimalProperty(IGameHookContainer mapper, string identifier, PropertyFields fields)
            : base(mapper, identifier, fields)
        {
        }

        protected override byte[] FromValue(int? value)
        {
            throw new NotImplementedException();
        }

        protected override int? ToValue(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            if (PlatformOptions.EndianType == EndianTypes.LittleEndian)
            {
                Array.Reverse(bytes);
            }

            int result = 0;
            foreach (byte bcd in bytes)
            {
                result *= 100;
                result += (10 * (bcd >> 4));
                result += bcd & 0xf;
            }

            return result;
        }
    }
}