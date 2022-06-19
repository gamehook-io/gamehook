using GameHook.Domain.Interfaces;

namespace GameHook.Domain.ValueTransformers
{
    internal class BinaryCodedDecimalTransformer : ITransformer<int>
    {
        public static byte[] FromValue(int value)
        {
            throw new NotImplementedException();
        }

        public static int ToValue(byte[] data)
        {
            int result = 0;

            foreach (byte bcd in data)
            {
                result *= 100;
                result += (10 * (bcd >> 4));
                result += bcd & 0xf;
            }

            return result;
        }
    }
}
