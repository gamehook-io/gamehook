namespace GameHook.Domain.ValueTransformers
{
    public static class IntegerTransformer
    {
        public static byte[] FromValue(int value)
        {
            return BitConverter.GetBytes(value);
        }

        public static int ToValue(byte[] data)
        {
            byte[] value = new byte[8];
            Array.Copy(data, value, data.Length);

            return BitConverter.ToInt32(value, 0);
        }
    }
}
