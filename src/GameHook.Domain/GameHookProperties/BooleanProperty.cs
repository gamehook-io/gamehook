using GameHook.Domain.Interfaces;

namespace GameHook.Domain.GameHookProperties
{
    public class BooleanProperty : GameHookProperty<bool>
    {
        public BooleanProperty(IGameHookContainer mapper, string identifier, PropertyFields fields)
            : base(mapper, identifier, fields)
        {
        }

        protected override byte[] FromValue(bool value)
        {
            return value == true ? new byte[] { 0x01 } : new byte[] { 0x00 };
        }

        protected override bool ToValue(byte[] bytes)
        {
            if (PlatformOptions.EndianType == EndianTypes.LittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes.First() == 0x00 ? false : true;
        }
    }
}