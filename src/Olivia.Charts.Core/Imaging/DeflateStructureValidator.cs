namespace Olivia.Core.Imaging;

/// <summary>
/// Checks the complete RFC1951 structure before framework inflation. A short input may produce
/// an EOF from DeflateStream without proving a final block was present; this validator proves
/// that block, the exact byte boundary, the output budget and every backward-copy distance.
/// It retains only bounded Huffman tables, and never materializes decompressed data.
/// </summary>
internal static class DeflateStructureValidator
{
    private static readonly int[] LengthBases = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtras = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBases = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtras = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly HuffmanTable FixedLiterals = CreateFixedLiterals();
    private static readonly HuffmanTable FixedDistances = new(Enumerable.Repeat(5, 32).ToArray());

    internal static void Validate(ReadOnlySpan<byte> deflate, int expectedLength, int windowSize)
    {
        var reader = new BitReader(deflate);
        var produced = 0;
        var blocks = 0;
        var final = false;
        while (!final)
        {
            if (++blocks > PngRasterCodec.MaximumDeflateBlocks) throw Invalid();
            final = reader.Read(1) != 0;
            var kind = reader.Read(2);
            if (kind == 0)
            {
                reader.Align();
                var length = reader.Read(16);
                if ((length ^ reader.Read(16)) != 65535) throw Invalid();
                AddOutput(ref produced, length, expectedLength);
                reader.SkipBytes(length);
                continue;
            }
            if (kind == 3) throw Invalid();
            var literals = FixedLiterals;
            var distances = FixedDistances;
            if (kind == 2) ReadDynamicTables(ref reader, out literals, out distances);
            while (true)
            {
                var symbol = literals.ReadSymbol(ref reader);
                if (symbol < 256) { AddOutput(ref produced, 1, expectedLength); continue; }
                if (symbol == 256) break;
                if (symbol > 285) throw Invalid();
                var index = symbol - 257;
                var length = LengthBases[index] + reader.Read(LengthExtras[index]);
                var distanceSymbol = distances.ReadSymbol(ref reader);
                if (distanceSymbol >= 30) throw Invalid();
                var distance = DistanceBases[distanceSymbol] + reader.Read(DistanceExtras[distanceSymbol]);
                if (distance > produced || distance > windowSize) throw Invalid();
                AddOutput(ref produced, length, expectedLength);
            }
        }
        if (produced != expectedLength || reader.ConsumedBytes != deflate.Length) throw Invalid();
    }

    private static void ReadDynamicTables(ref BitReader reader, out HuffmanTable literals, out HuffmanTable distances)
    {
        var literalCount = reader.Read(5) + 257;
        var distanceCount = reader.Read(5) + 1;
        var codeCount = reader.Read(4) + 4;
        // Codes 286/287 and distance codes 30/31 are reserved; the normalized subset excludes them.
        if (literalCount > 286 || distanceCount > 30) throw Invalid();
        var codeLengths = new int[19];
        for (var i = 0; i < codeCount; i++) codeLengths[CodeLengthOrder[i]] = reader.Read(3);
        var codeTable = new HuffmanTable(codeLengths, requireComplete: true);
        var lengths = new int[literalCount + distanceCount];
        var count = 0;
        while (count < lengths.Length)
        {
            var symbol = codeTable.ReadSymbol(ref reader);
            if (symbol <= 15) { lengths[count++] = symbol; continue; }
            int repeated, repetitions;
            if (symbol == 16)
            {
                if (count == 0) throw Invalid();
                repeated = lengths[count - 1];
                repetitions = reader.Read(2) + 3;
            }
            else if (symbol == 17) { repeated = 0; repetitions = reader.Read(3) + 3; }
            else if (symbol == 18) { repeated = 0; repetitions = reader.Read(7) + 11; }
            else throw Invalid();
            if (repetitions > lengths.Length - count) throw Invalid();
            Array.Fill(lengths, repeated, count, repetitions);
            count += repetitions;
        }
        if (lengths[256] == 0) throw Invalid();
        literals = new HuffmanTable(lengths.AsSpan(0, literalCount));
        distances = new HuffmanTable(lengths.AsSpan(literalCount, distanceCount), allowEmpty: true);
    }

    private static HuffmanTable CreateFixedLiterals()
    {
        var lengths = new int[288];
        Array.Fill(lengths, 8, 0, 144);
        Array.Fill(lengths, 9, 144, 112);
        Array.Fill(lengths, 7, 256, 24);
        Array.Fill(lengths, 8, 280, 8);
        return new(lengths);
    }

    private static void AddOutput(ref int produced, int count, int budget)
    {
        if (count > budget - produced) throw Invalid();
        produced += count;
    }

    private static InvalidDataException Invalid() => new("The PNG deflate stream is invalid, incomplete or outside the output budget.");

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        internal int ConsumedBytes => (_position + 7) / 8;
        internal int Read(int bits)
        {
            if (bits > _data.Length * 8 - _position) throw Invalid();
            var value = 0;
            for (var bit = 0; bit < bits; bit++, _position++)
                value |= ((_data[_position / 8] >> (_position % 8)) & 1) << bit;
            return value;
        }
        internal void Align() => _position = (_position + 7) & ~7;
        internal void SkipBytes(int count)
        {
            if (count > (_data.Length * 8 - _position) / 8) throw Invalid();
            _position += count * 8;
        }
    }

    private sealed class HuffmanTable
    {
        private readonly int[] _counts = new int[16];
        private readonly int[] _firstCodes = new int[16];
        private readonly int[] _firstSymbols = new int[16];
        private readonly int[] _symbols;
        private readonly int _maximumLength;

        internal HuffmanTable(ReadOnlySpan<int> lengths, bool requireComplete = false, bool allowEmpty = false)
        {
            foreach (var length in lengths)
            {
                if (length is < 0 or > 15) throw Invalid();
                if (length == 0) continue;
                _counts[length]++;
                _maximumLength = Math.Max(_maximumLength, length);
            }
            if (_maximumLength == 0 && !allowEmpty) throw Invalid();
            var unused = 1;
            var code = 0;
            var symbolIndex = 0;
            for (var length = 1; length <= 15; length++)
            {
                unused = (unused << 1) - _counts[length];
                if (unused < 0) throw Invalid();
                code = (code + _counts[length - 1]) << 1;
                _firstCodes[length] = code;
                _firstSymbols[length] = symbolIndex;
                symbolIndex += _counts[length];
            }
            if (unused > 0 && (_maximumLength > 1 || requireComplete)) throw Invalid();
            _symbols = new int[symbolIndex];
            var offsets = (int[])_firstSymbols.Clone();
            for (var symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] > 0) _symbols[offsets[lengths[symbol]]++] = symbol;
        }

        internal int ReadSymbol(ref BitReader reader)
        {
            var code = 0;
            for (var length = 1; length <= _maximumLength; length++)
            {
                // Huffman codes are emitted most-significant bit first within the little-endian bit stream.
                code = (code << 1) | reader.Read(1);
                var delta = code - _firstCodes[length];
                if (delta >= 0 && delta < _counts[length]) return _symbols[_firstSymbols[length] + delta];
            }
            throw Invalid();
        }
    }
}
