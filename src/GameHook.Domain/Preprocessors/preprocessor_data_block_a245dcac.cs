namespace GameHook.Domain.Preprocessors
{
    public class DataBlock_a245dcac
    {
        public int[] SubstructureOrdering { get; init; } = new int[0];
        public byte[] DecryptedData { get; init; } = new byte[0];
    }

    public static partial class Preprocessors
    {
        // Used beforehand to cache the data block.
        public static DataBlock_a245dcac decrypt_data_block_a245dcac(IEnumerable<MemoryAddressBlockResult> blocks, uint startingAddress)
        {
            var wramBlock = blocks.GetResultWithinRange(startingAddress);

            var structureStartingAddress = (int)(startingAddress - wramBlock.StartingAddress - 48);
            var structureEndingAddress = structureStartingAddress + 48;

            var dataBlock = wramBlock.Data[structureStartingAddress..structureEndingAddress];

            var personalityValueBytes = new byte[8];
            var originalTrainerIdBytes = new byte[8];
            Array.Copy(dataBlock[0..3], personalityValueBytes, 3);
            Array.Copy(dataBlock[4..7], originalTrainerIdBytes, 3);

            var personalityValue = BitConverter.ToInt32(personalityValueBytes);
            var originalTrainerId = BitConverter.ToInt32(originalTrainerIdBytes);

            // The order of the structures is determined by the personality value of the Pokémon modulo 24,
            // as shown below, where G, A, E, and M stand for the substructures growth, attacks, EVs and condition, and miscellaneous, respectively.
            var substructureType = personalityValue % 24;

            var substructureOrder = substructureType switch
            {
                0 => new int[4] { 1, 2, 3, 4 },
                1 => new int[4] { 1, 2, 4, 3 },
                2 => new int[4] { 1, 3, 2, 4 },
                3 => new int[4] { 1, 3, 4, 2 },
                4 => new int[4] { 1, 4, 2, 3 },
                5 => new int[4] { 1, 4, 3, 2 },
                6 => new int[4] { 2, 1, 3, 4 },
                7 => new int[4] { 2, 1, 4, 3 },
                8 => new int[4] { 2, 3, 1, 4 },
                9 => new int[4] { 2, 3, 4, 1 },
                10 => new int[4] { 2, 4, 1, 3 },
                11 => new int[4] { 2, 4, 3, 1 },
                12 => new int[4] { 3, 1, 2, 4 },
                13 => new int[4] { 3, 1, 4, 2 },
                14 => new int[4] { 3, 2, 1, 4 },
                15 => new int[4] { 3, 2, 4, 1 },
                16 => new int[4] { 3, 4, 1, 2 },
                17 => new int[4] { 3, 4, 2, 1 },
                18 => new int[4] { 4, 1, 2, 3 },
                19 => new int[4] { 4, 1, 3, 2 },
                20 => new int[4] { 4, 2, 1, 3 },
                21 => new int[4] { 4, 2, 3, 1 },
                22 => new int[4] { 4, 3, 1, 2 },
                23 => new int[4] { 4, 3, 2, 1 },
                _ => throw new Exception($"data_block_a245dcac returned a unknown substructure order given a personality value of {personalityValue} => {substructureType}.")
            };

            // To obtain the 32-bit decryption key, the entire Original Trainer ID number must be XORed with the personality value of the entry.
            var decryptionKey = originalTrainerId ^ personalityValue;

            // This key can then be used to decrypt the data by XORing it, 32 bits (or 4 bytes) at a time.
            var decryptedByteArray = (byte[])dataBlock.Clone();

            // Return the byte array decrypted.
            return new DataBlock_a245dcac()
            {
                SubstructureOrdering = substructureOrder,
                DecryptedData = decryptedByteArray
            };
        }

        public static byte[] data_block_a245dcac(int structureIndex, int offset, Dictionary<int, byte[]> decryptedDataBlock)
        {
            var structure = decryptedDataBlock[21];
            return structure[offset..32];
        }
    }
}
