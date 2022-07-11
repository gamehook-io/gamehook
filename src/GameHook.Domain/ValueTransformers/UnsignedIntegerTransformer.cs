namespace GameHook.Domain.ValueTransformers
{
    public static class UnsignedIntegerTransformer
    {
        public static byte[] FromValue(uint value)
        {
            return BitConverter.GetBytes(value);
        }

        public static uint ToValue(byte[] data, bool FLIP_THOSE_BYTES = false)
        {
            byte[] toValueData = (byte[])data.Clone();

            if (FLIP_THOSE_BYTES)
            {
                Array.Reverse(toValueData);
            }

            byte[] value = new byte[8];
            Array.Copy(toValueData, value, data.Length);

            return BitConverter.ToUInt32(value, 0);
        }
    }
}
