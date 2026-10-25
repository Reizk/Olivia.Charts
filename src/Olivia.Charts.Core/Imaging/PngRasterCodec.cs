using System.Buffers.Binary;
using System.IO.Compression;

namespace Olivia.Core.Imaging;

/// <summary>
/// A bounded, metadata-free PNG subset: exactly IHDR, one IDAT and IEND, RGBA8,
/// noninterlaced, standard compression/filter method, and filters 0–4. Other PNG
/// color types, metadata, split IDATs, dictionaries and trailing data are rejected.
/// At most 4096 deflate blocks are accepted; dynamic distance alphabets exclude the
/// two reserved descriptor slots (31/32-entry alphabets).
/// No native image decoder, file path, or source metadata is retained.
/// </summary>
public static class PngRasterCodec
{
    public const int MaximumEncodedBytes = 2 * 1024 * 1024;
    public const int MaximumDeflateBlocks = 4096;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Validates all framing, CRCs and output bounds before allocating pixel buffers.</summary>
    public static RasterImage Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > MaximumEncodedBytes) throw Invalid("The PNG exceeds the encoded byte budget.");
        if (encoded.Length < Signature.Length || !encoded[..Signature.Length].SequenceEqual(Signature))
            throw Invalid("The PNG signature is invalid.");
        var offset = Signature.Length;
        var header = ReadChunk(encoded, ref offset, "IHDR"u8);
        if (header.Length != 13) throw Invalid("The PNG header length is invalid.");
        var widthRaw = BinaryPrimitives.ReadUInt32BigEndian(header);
        var heightRaw = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (widthRaw is < 1 or > RasterImage.MaximumDimension || heightRaw is < 1 or > RasterImage.MaximumDimension ||
            (ulong)widthRaw * heightRaw > RasterImage.MaximumPixelCount)
            throw Invalid("The PNG dimensions exceed the image budget.");
        if (header[8] != 8 || header[9] != 6 || header[10] != 0 || header[11] != 0 || header[12] != 0)
            throw Invalid("Only noninterlaced RGBA8 PNG images without metadata are supported.");
        var compressed = ReadChunk(encoded, ref offset, "IDAT"u8);
        var end = ReadChunk(encoded, ref offset, "IEND"u8);
        if (end.Length != 0 || offset != encoded.Length) throw Invalid("The PNG has trailing or invalid end data.");

        var width = (int)widthRaw;
        var height = (int)heightRaw;
        var rowBytes = width * 4;
        var inflatedLength = (rowBytes + 1) * height;
        ValidateZlib(compressed, inflatedLength);
        var filtered = new byte[inflatedLength];
        using (var input = new MemoryStream(compressed.ToArray(), writable: false))
        using (var inflater = new ZLibStream(input, CompressionMode.Decompress))
        {
            var filled = 0;
            while (filled < filtered.Length)
            {
                var read = inflater.Read(filtered.AsSpan(filled));
                if (read == 0) throw Invalid("The PNG pixel stream is truncated.");
                filled += read;
            }
            if (inflater.ReadByte() != -1) throw Invalid("The PNG pixel stream exceeds its declared dimensions.");
        }
        if (Adler32(filtered) != BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]))
            throw Invalid("The PNG pixel checksum is invalid.");
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var filteredRow = filtered.AsSpan(y * (rowBytes + 1), rowBytes + 1);
            var filter = filteredRow[0];
            if (filter > 4) throw Invalid("The PNG row filter is invalid.");
            var row = pixels.AsSpan(y * rowBytes, rowBytes);
            for (var x = 0; x < rowBytes; x++)
            {
                var left = x >= 4 ? row[x - 4] : 0;
                var up = y > 0 ? pixels[(y - 1) * rowBytes + x] : 0;
                var upperLeft = x >= 4 && y > 0 ? pixels[(y - 1) * rowBytes + x - 4] : 0;
                var prediction = filter switch {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upperLeft),
                    _ => throw Invalid("The PNG row filter is invalid.")
                };
                row[x] = unchecked((byte)(filteredRow[x + 1] + prediction));
            }
        }
        return new(width, height, pixels);
    }

    /// <summary>Serializes normalized RGBA8 pixels with no metadata and one IDAT chunk.</summary>
    public static byte[] Encode(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pixels = image.CopyPixels();
        var rowBytes = image.Width * 4;
        var filtered = new byte[(rowBytes + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
            pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(filtered.AsSpan(y * (rowBytes + 1) + 1));
        using var compressed = new MemoryStream();
        using (var compressor = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(filtered);
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        if (output.Length > MaximumEncodedBytes) throw Invalid("The encoded PNG exceeds the byte budget.");
        return output.ToArray();
    }

    private static ReadOnlySpan<byte> ReadChunk(ReadOnlySpan<byte> encoded, ref int offset, ReadOnlySpan<byte> expectedType)
    {
        if (encoded.Length - offset < 12) throw Invalid("The PNG chunk is truncated.");
        var unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(encoded[offset..]);
        if (unsignedLength > (uint)(encoded.Length - offset - 12)) throw Invalid("The PNG chunk length is invalid.");
        var length = (int)unsignedLength;
        var type = encoded.Slice(offset + 4, 4);
        if (!type.SequenceEqual(expectedType)) throw Invalid("The PNG chunk order or type is unsupported.");
        var data = encoded.Slice(offset + 8, length);
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(offset + 8 + length, 4));
        if (Crc32(type, data) != checksum) throw Invalid("The PNG chunk checksum is invalid.");
        offset += length + 12;
        return data;
    }

    private static void ValidateZlib(ReadOnlySpan<byte> compressed, int inflatedLength)
    {
        if (compressed.Length < 8) throw Invalid("The PNG compressed stream is truncated.");
        var cmf = compressed[0];
        var flags = compressed[1];
        if ((cmf & 15) != 8 || (cmf >> 4) > 7 || (flags & 32) != 0 || ((cmf << 8) | flags) % 31 != 0)
            throw Invalid("The PNG zlib wrapper is invalid or requires a dictionary.");
        DeflateStructureValidator.Validate(compressed[2..^4], inflatedLength, 1 << ((cmf >> 4) + 8));
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var prediction = left + up - upperLeft;
        var leftDistance = Math.Abs(prediction - left);
        var upDistance = Math.Abs(prediction - up);
        var diagonalDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= upDistance && leftDistance <= diagonalDistance ? left : upDistance <= diagonalDistance ? up : upperLeft;
    }

    private static uint Adler32(ReadOnlySpan<byte> bytes)
    {
        uint a = 1, b = 0;
        foreach (var value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(type, data));
        output.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type) crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
            table[i] = value;
        }
        return table;
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
