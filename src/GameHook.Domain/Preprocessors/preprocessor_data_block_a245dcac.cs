namespace GameHook.Domain.Preprocessors
{
    public static partial class Preprocessors
    {
        private static byte[] ReorderByteArrays(byte[] a, byte[] b, byte[] c, byte[] d) => a.Concat(b).Concat(c).Concat(d).ToArray();

        // Used beforehand to cache the data block.
        public static byte[] decrypt_data_block_a245dcac(byte[] entireBlock, int originalTrainerId, int personalityValue)
        {
            // The order of the structures is determined by the personality value of the Pokémon modulo 24,
            // as shown below, where G, A, E, and M stand for the substructures growth, attacks, EVs and condition, and miscellaneous, respectively.
            var substructure = personalityValue % 24;

            var b0 = entireBlock[0..11];
            var b1 = entireBlock[12..24];
            var b2 = entireBlock[25..36];
            var b3 = entireBlock[36..48];

            var substructureOrder = substructure switch
            {
                0 =>  ReorderByteArrays(b0, b1, b2, b3),
                1 =>  ReorderByteArrays(b0, b1, b2, b3),
                2 =>  ReorderByteArrays(b0, b1, b2, b3),
                3 =>  ReorderByteArrays(b0, b1, b2, b3),
                4 =>  ReorderByteArrays(b0, b1, b2, b3),
                5 =>  ReorderByteArrays(b0, b1, b2, b3),
                6 =>  ReorderByteArrays(b0, b1, b2, b3),
                7 =>  ReorderByteArrays(b0, b1, b2, b3),
                8 =>  ReorderByteArrays(b0, b1, b2, b3),
                9 =>  ReorderByteArrays(b0, b1, b2, b3),
                10 => ReorderByteArrays(b0, b1, b2, b3),
                11 => ReorderByteArrays(b0, b1, b2, b3),
                12 => ReorderByteArrays(b0, b1, b2, b3),
                13 => ReorderByteArrays(b0, b1, b2, b3),
                14 => ReorderByteArrays(b0, b1, b2, b3),
                15 => ReorderByteArrays(b0, b1, b2, b3),
                16 => ReorderByteArrays(b0, b1, b2, b3),
                17 => ReorderByteArrays(b0, b1, b2, b3),
                18 => ReorderByteArrays(b0, b1, b2, b3),
                19 => ReorderByteArrays(b0, b1, b2, b3),
                20 => ReorderByteArrays(b0, b1, b2, b3),
                21 => ReorderByteArrays(b0, b1, b2, b3),
                22 => ReorderByteArrays(b0, b1, b2, b3),
                23 => ReorderByteArrays(b0, b1, b2, b3),
                24 => ReorderByteArrays(b0, b1, b2, b3),
                _ => throw new Exception($"data_block_a245dcac returned a unknown substructure order given a personality value of {personalityValue} => {substructure}.")
            };

            // To obtain the 32-bit decryption key, the entire Original Trainer ID number must be XORed with the personality value of the entry.
            var decryptionKey = originalTrainerId ^ personalityValue;

            // This key can then be used to decrypt the data by XORing it, 32 bits (or 4 bytes) at a time.
            var decryptedByteArray = new byte[0];

            // Return the byte array decrypted.
            return entireBlock;
        }

        public static byte[] data_block_a245dcac(int structureIndex, int offset, Dictionary<int, byte[]> decryptedDataBlock)
        {
            var structure = decryptedDataBlock[21];
            return structure[offset..32];
        }
    }
}
