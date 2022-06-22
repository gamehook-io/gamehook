using System.Collections;

namespace GameHook.Domain.ValueTransformers
{
    public static class BitTransformer
    {
        public static byte[] FromValue(byte[] data, int position, bool value)
        {
            var bitArray = new BitArray(data);
            bitArray.Set(position, value);

            byte[] bytes = new byte[1];
            bitArray.CopyTo(bytes, 0);

            return bytes;
        }

        public static bool ToValue(byte[] data, int position)
        {
            return new BitArray(data).Get(position);
        }
    }
}
