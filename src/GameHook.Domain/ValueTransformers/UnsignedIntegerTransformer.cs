namespace GameHook.Domain.ValueTransformers
{
    public static class UnsignedIntegerTransformer
    {
        public static byte[] FromValue(uint? value)
        {
            throw new NotImplementedException();
        }

        public static uint ToValue(byte[] data)
        {
            byte[] value = new byte[8];
            Array.Copy(data, value, data.Length);

            return BitConverter.ToUInt32(value, 0);
        }
    }
}
