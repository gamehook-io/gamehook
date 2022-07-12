namespace GameHook.Domain.ValueTransformers
{
    public static class UnsignedIntegerTransformer
    {
        public static byte[] FromValue(uint value)
        {
            return BitConverter.GetBytes(value);
        }

        public static uint ToValue(byte[] passedInData, bool reverseBytes = false)
        {
            byte[] data = (byte[])passedInData.Clone();

            if (reverseBytes)
            {
                Array.Reverse(data);
            }

            byte[] value = new byte[8];
            Array.Copy(data, value, data.Length);

            return BitConverter.ToUInt32(value, 0);
        }
    }
}
