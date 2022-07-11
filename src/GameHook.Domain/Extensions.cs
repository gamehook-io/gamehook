using Microsoft.Extensions.Configuration;

namespace GameHook.Domain
{
    public static class Extensions
    {
        public static string ToHexdecimalString(this uint value) => $"0x{value:X2}";
        public static string ToHexdecimalString(this byte value) => ((uint)value).ToHexdecimalString();
        public static string ToHexdecimalString(this byte[] bytes) => string.Join(", ", bytes.ToHexdecimalStringArray());
        public static string[] ToHexdecimalStringArray(this byte[] bytes) => bytes.Select(x => x.ToHexdecimalString()).ToArray();

        public static IEnumerable<int> ToIntegerArray(this byte[] bytes) => bytes.Select(x => (int)x).ToList();

        public static byte FromHexdecimalStringToByte(this string value) => Convert.ToByte(value, 16);
        public static uint FromHexdecimalStringToUint(this string value) => Convert.ToUInt32(value, 16);

        public static string GetRequiredValue(this IConfiguration configuration, string key)
        {
            var value = configuration[key];

            if (value == null) throw new Exception($"Configuration '{key}' is missing from appsettings.json");
            if (string.IsNullOrWhiteSpace(value)) throw new Exception($"Configuration '{key}' is empty.");

            return value;
        }

        public static async Task<int> IncrementCondition(this int value, int maximumValueAllowed, Func<Task> executeAsync)
        {
            value++;

            if (value > maximumValueAllowed)
            {
                await executeAsync();
            }

            return value;
        }

        public static bool Between(this uint value, uint min, uint max)
        {
            return value >= min && value <= max;
        }

        public static byte[] Get(this byte[] data, uint skip, int take)
        {
            return data.Skip((int)skip).Take(take).ToArray();
        }

        public static MemoryAddressBlockResult GetResultWithinRange(this IEnumerable<MemoryAddressBlockResult> blocks, uint address)
        {
            return blocks.SingleOrDefault(x => address > x.StartingAddress && address < x.EndingAddress) ??
                throw new Exception($"Cannot GetResultWithinRange for address {address}.");
        }

        public static byte[] GetRelativeAddress(this MemoryAddressBlockResult block, MemoryAddress memoryAddress, int length)
        {
            var startingOffset = (int)(memoryAddress - block.StartingAddress);
            var endingOffset = startingOffset + length;

            return block.Data[startingOffset..endingOffset];
        }

        public static int GetIntParameterFromFunctionString(this string function, int position)
        {
            return int.Parse(function.Between("(", ")").Split(",")[position]);
        }

        public static string Between(this string str, string firstString, string lastString)
        {
            string FinalString;
            int Pos1 = str.IndexOf(firstString) + firstString.Length;
            int Pos2 = str.IndexOf(lastString);
            FinalString = str.Substring(Pos1, Pos2 - Pos1);
            return FinalString;
        }
    }
}
