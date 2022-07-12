namespace GameHook.Domain.ValueTransformers
{
    public static class IntegerTransformer
    {
        public static byte[] FromValue(int value, bool reverseBytes)
        {
            return BitConverter.GetBytes(value);
        }

        public static int ToValue(byte[] data, bool reverseBytes)
        {
            byte[] clonedData = (byte[])data.Clone();

            if (reverseBytes)
            {
                Array.Reverse(clonedData);
            }

            byte[] value = new byte[8];
            Array.Copy(clonedData, value, data.Length);

            return BitConverter.ToInt32(value, 0);
        }
    }
}
