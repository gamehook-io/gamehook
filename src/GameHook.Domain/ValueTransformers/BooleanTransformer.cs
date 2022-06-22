namespace GameHook.Domain.ValueTransformers
{
    public static class BooleanTransformer
    {
        public static byte[] FromValue(bool value)
        {
            return value == true ? new byte[] { 0x01 } : new byte[] { 0x00 };
        }

        public static bool ToValue(byte[] data)
        {
            return data.First() == 0x00 ? false : true;
        }
    }
}
