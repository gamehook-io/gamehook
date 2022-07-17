using GameHook.Domain.ValueTransformers;

namespace GameHook.Domain.Preprocessors
{
    public class DataBlock_a245dcac
    {
        public MemoryAddress Address { get; init; }
        public int[] SubstructureOrdering { get; init; } = new int[0];
        public byte[] DecryptedData { get; init; } = new byte[0];
    }

    public class DataBlock_a245dcac_PropertyResult
    {
        public MemoryAddress Address { get; init; }
        public byte[] Bytes { get; init; } = new byte[0];
    }

    public static partial class Preprocessors
    {
        // Used beforehand to cache the data block.
        public static DataBlock_a245dcac decrypt_data_block_a245dcac(IEnumerable<MemoryAddressBlockResult> blocks, uint startingAddress)
        {
            // Starting Address is the start of the P data structure.
            var block = blocks.GetResultWithinRange(startingAddress) ?? throw new Exception($"Unable to retrieve memory block for address {startingAddress.ToHexdecimalString()}.");
            var pStructure = block.GetRelativeAddress(startingAddress, 48 + 32);

            var personalityValue = UnsignedIntegerTransformer.ToValue(block.GetRelativeAddress(startingAddress, 4));
            var originalTrainerId = UnsignedIntegerTransformer.ToValue(block.GetRelativeAddress(startingAddress + 4, 4));

            // The order of the structures is determined by the personality value of the P modulo 24,
            // as shown below, where G, A, E, and M stand for the substructures growth, attacks, EVs and condition, and miscellaneous, respectively.
            var substructureType = personalityValue % 24;

            var substructureOrder = substructureType switch
            {
                0 => new int[4] { 0, 1, 2, 3 },
                1 => new int[4] { 0, 1, 3, 2 },
                2 => new int[4] { 0, 2, 1, 3 },
                3 => new int[4] { 0, 3, 1, 2 },
                4 => new int[4] { 0, 2, 3, 1 },
                5 => new int[4] { 0, 3, 2, 1 },
                6 => new int[4] { 1, 0, 2, 3 },
                7 => new int[4] { 1, 0, 3, 2 },
                8 => new int[4] { 2, 0, 1, 3 },
                9 => new int[4] { 3, 0, 1, 2 },
                10 => new int[4] { 2, 0, 3, 1 },
                11 => new int[4] { 3, 0, 2, 1 },
                12 => new int[4] { 1, 2, 0, 3 },
                13 => new int[4] { 1, 3, 0, 2 },
                14 => new int[4] { 2, 1, 0, 3 },
                15 => new int[4] { 3, 1, 0, 2 },
                16 => new int[4] { 2, 3, 0, 1 },
                17 => new int[4] { 3, 2, 0, 1 },
                18 => new int[4] { 1, 2, 3, 0 },
                19 => new int[4] { 1, 3, 2, 0 },
                20 => new int[4] { 2, 1, 3, 0 },
                21 => new int[4] { 3, 1, 2, 0 },
                22 => new int[4] { 2, 3, 1, 0 },
                23 => new int[4] { 3, 2, 1, 0 },
                _ => throw new Exception($"data_block_a245dcac returned a unknown substructure order given a personality value of {personalityValue} => {substructureType}.")
            };

            // To obtain the 32-bit decryption key, the entire OTID number must be XORed with the personality value of the entry.
            var decryptionKey = originalTrainerId ^ personalityValue;

            // This key can then be used to decrypt the encrypted data block (starting at offset 32)
            // by XORing it, 32 bits (or 4 bytes) at a time.
            var decryptedByteArray = pStructure
                .Skip(32).Chunk(4)
                .SelectMany(x => UnsignedIntegerTransformer.FromValue(UnsignedIntegerTransformer.ToValue(x) ^ decryptionKey))
                .ToArray();

            // Return the byte array decrypted.
            return new DataBlock_a245dcac()
            {
                Address = startingAddress,
                SubstructureOrdering = substructureOrder,
                DecryptedData = decryptedByteArray
            };
        }

        public static DataBlock_a245dcac_PropertyResult data_block_a245dcac(int structureIndex, int offset, int size, DataBlock_a245dcac decryptedDataBlock)
        {
            var structurePositionForProperty = decryptedDataBlock.SubstructureOrdering[structureIndex];
            var propertyStartingOffset = (structurePositionForProperty * 12) + offset;
            var propertyEndingOffset = propertyStartingOffset + size;

            return new DataBlock_a245dcac_PropertyResult()
            {
                Address = (MemoryAddress)(decryptedDataBlock.Address + propertyStartingOffset),
                Bytes = decryptedDataBlock.DecryptedData[propertyStartingOffset..propertyEndingOffset]
            };
        }
    }
}
