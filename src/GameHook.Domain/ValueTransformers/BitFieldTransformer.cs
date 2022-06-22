using GameHook.Domain.Interfaces;
using System.Collections;

namespace GameHook.Domain.ValueTransformers
{
    public static class BitFieldTransformer
    {
        public static byte[] FromValue(IEnumerable<bool> value)
        {
            throw new NotImplementedException();
        }

        public static IEnumerable<bool> ToValue(byte[] data)
        {
            var bitArray = new BitArray(data);

            var boolArray = new bool[bitArray.Length];
            bitArray.CopyTo(boolArray, 0);

            return boolArray;
        }
    }
}
