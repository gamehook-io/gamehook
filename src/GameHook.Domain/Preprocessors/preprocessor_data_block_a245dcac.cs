﻿namespace GameHook.Domain.Preprocessors
{
    public class DataBlock_a245dcac
    {
        public int[] SubstructureOrder { get; init; } = new int[0];
        public byte[] DecryptedData { get; init; } = new byte[0];
    }

    public static partial class Preprocessors
    {
        private static byte[] ReorderByteArrays(int[] order, byte[] data)
        {
            byte[] reorderedData = new byte[data.Length];

            data[0..11].CopyTo(reorderedData, order[0] * 12);
            data[12..24].CopyTo(reorderedData, order[1] * 12);
            data[25..32].CopyTo(reorderedData, order[2] * 12);
            data[33..48].CopyTo(reorderedData, order[3] * 12);

            return reorderedData;
        }

        // Used beforehand to cache the data block.
        public static DataBlock_a245dcac decrypt_data_block_a245dcac(byte[] entireBlock, uint startingAddress)
        {
            var personalityValue = 0;
            var originalTrainerId = 0;

            // The order of the structures is determined by the personality value of the Pokémon modulo 24,
            // as shown below, where G, A, E, and M stand for the substructures growth, attacks, EVs and condition, and miscellaneous, respectively.
            var substructure = personalityValue % 24;

            var substructureOrder = substructure switch
            {
                0 =>  new int[4] { 0, 1, 2, 3 },
                1 =>  new int[4] { 0, 1, 2, 3 },
                2 =>  new int[4] { 0, 1, 2, 3 },
                3 =>  new int[4] { 0, 1, 2, 3 },
                4 =>  new int[4] { 0, 1, 2, 3 },
                5 =>  new int[4] { 0, 1, 2, 3 },
                6 =>  new int[4] { 0, 1, 2, 3 },
                7 =>  new int[4] { 0, 1, 2, 3 },
                8 =>  new int[4] { 0, 1, 2, 3 },
                9 =>  new int[4] { 0, 1, 2, 3 },
                10 => new int[4] { 0, 1, 2, 3 },
                11 => new int[4] { 0, 1, 2, 3 },
                12 => new int[4] { 0, 1, 2, 3 },
                13 => new int[4] { 0, 1, 2, 3 },
                14 => new int[4] { 0, 1, 2, 3 },
                15 => new int[4] { 0, 1, 2, 3 },
                16 => new int[4] { 0, 1, 2, 3 },
                17 => new int[4] { 0, 1, 2, 3 },
                18 => new int[4] { 0, 1, 2, 3 },
                19 => new int[4] { 0, 1, 2, 3 },
                20 => new int[4] { 0, 1, 2, 3 },
                21 => new int[4] { 0, 1, 2, 3 },
                22 => new int[4] { 0, 1, 2, 3 },
                23 => new int[4] { 0, 1, 2, 3 },
                24 => new int[4] { 0, 1, 2, 3 },
                _ => throw new Exception($"data_block_a245dcac returned a unknown substructure order given a personality value of {personalityValue} => {substructure}.")
            };

            var substructureReorderedData = ReorderByteArrays(substructureOrder, entireBlock);

            // To obtain the 32-bit decryption key, the entire Original Trainer ID number must be XORed with the personality value of the entry.
            var decryptionKey = originalTrainerId ^ personalityValue;

            // This key can then be used to decrypt the data by XORing it, 32 bits (or 4 bytes) at a time.
            var decryptedByteArray = entireBlock;

            // Return the byte array decrypted.
            return new DataBlock_a245dcac()
            {
                SubstructureOrder = substructureOrder,
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
