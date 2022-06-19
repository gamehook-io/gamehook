using GameHook.Domain.Interfaces;

namespace GameHook.Domain.ValueTransformers
{
    public class IntegerTransformer : ITransformer<int>
    {
        public static int ToValue(byte[] data)
        {
            byte[] value = new byte[8];
            Array.Copy(data, value, data.Length);

            return BitConverter.ToInt32(value, 0);
        }

        public static byte[] FromValue(int value)
        {
            return BitConverter.GetBytes(value);
        }
    }
}
