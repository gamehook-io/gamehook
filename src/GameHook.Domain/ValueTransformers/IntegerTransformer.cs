namespace GameHook.Domain.ValueTransformers
{
    public static class IntegerTransformer
    {
        public static byte[] FromValue(int value, bool reverseBytes)
        {
            return BitConverter.GetBytes(value);
        }

        public static int ToValue(byte[] data)
        {
            // Integers cannot currently exceed int32
            byte[] value = new byte[8];
            Array.Copy(data, value, data.Length);

            return BitConverter.ToInt32(value, 0);
        }
    }
}
