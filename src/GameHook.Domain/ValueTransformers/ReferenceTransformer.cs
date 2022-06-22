namespace GameHook.Domain.ValueTransformers
{
    public static class ReferenceHelper
    {
        public static GlossaryItem? SingleOrDefaultByKey(this IEnumerable<GlossaryItem> glossaryItems, byte[] bytes)
        {
            // Translate the byte array into references using
            // the dictionary passed in.
            byte[] value = new byte[8];
            Array.Copy(bytes.Reverse().ToArray(), value, bytes.Length);

            var convertedValue = BitConverter.ToUInt32(value, 0);

            return glossaryItems.SingleOrDefault(x => x.Key == convertedValue);
        }

        public static GlossaryItem? SingleOrDefaultByValue(this IEnumerable<GlossaryItem> glossaryItems, string value)
        {
            var item = glossaryItems.SingleOrDefault(x => x.Value?.ToString() == value);

            if (item == null)
            {
                return null;
            }

            return item;
        }

        public static GlossaryItem? FirstOrDefaultByValue(this IEnumerable<GlossaryItem> glossaryItems, string? value)
        {
            var item = glossaryItems.FirstOrDefault(x => x.Value?.ToString() == value);

            if (item == null)
            {
                return null;
            }

            return item;
        }
    }

    public static class ReferenceTransformer
    {
        public static byte[] FromValue(bool value)
        {
            throw new NotImplementedException();
        }

        public static object? ToValue(byte[] data, IEnumerable<GlossaryItem> glossaryItems)
        {
            return glossaryItems.SingleOrDefaultByKey(data)?.Value;
        }
    }

    public static class ReferenceArrayTransformer
    {
        public static byte[] FromValue(bool value)
        {
            throw new NotImplementedException();
        }

        public static object? ToValue(byte[] data, IEnumerable<GlossaryItem> glossaryItems)
        {
            return data.Select(x => glossaryItems.SingleOrDefaultByKey(new byte[] { x })?.Value);
        }
    }
}
