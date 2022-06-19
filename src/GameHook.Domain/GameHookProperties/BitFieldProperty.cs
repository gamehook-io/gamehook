using GameHook.Domain.Interfaces;
using System.Collections;

namespace GameHook.Domain.GameHookProperties
{
    // TODO: Rename from bitArray to bitField in mapper files.
    public class BitFieldProperty : GameHookProperty<IEnumerable<bool>>
    {
        public BitFieldProperty(IGameHookContainer mapper, string identifier, PropertyFields fields)
            : base(mapper, identifier, fields)
        {
        }

        protected override byte[] FromValue(IEnumerable<bool>? value)
        {
            throw new NotImplementedException();
        }

        protected override IEnumerable<bool> ToValue(byte[] bytes)
        {
            if (PlatformOptions.EndianType == EndianTypes.LittleEndian)
            {
                Array.Reverse(bytes);
            }

            var bitArray = new BitArray(bytes);
            var boolArray = new bool[bitArray.Length];
            bitArray.CopyTo(boolArray, 0);

            return boolArray;
        }
    }
}