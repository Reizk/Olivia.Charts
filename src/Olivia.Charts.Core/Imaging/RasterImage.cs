using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Olivia.Core.Editing;

namespace Olivia.Core.Imaging;

/// <summary>An immutable, tightly packed, straight-alpha RGBA8 image in row-major order.
/// JSON exposes only dimensions and checksum; use PngRasterCodec for pixel serialization.</summary>
[JsonConverter(typeof(RasterImageJsonConverter))]
public sealed class RasterImage
{
    public const int MaximumDimension = 1024;
    public const int MaximumPixelCount = 262144;
    private readonly byte[] _pixels;

    /// <summary>Copies the caller's bytes. No mutable storage is shared with the caller.</summary>
    public RasterImage(int width, int height, ReadOnlySpan<byte> rgbaPixels)
    {
        ValidateDimensions(width, height);
        if (rgbaPixels.Length != width * height * 4)
            throw new ArgumentException("RGBA storage must contain exactly four bytes per pixel.", nameof(rgbaPixels));
        Width = width;
        Height = height;
        _pixels = rgbaPixels.ToArray();
        PixelChecksum = Convert.ToHexString(SHA256.HashData(_pixels));
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Uppercase SHA-256 of only the RGBA pixel bytes; dimensions and metadata are excluded.</summary>
    public string PixelChecksum { get; }
    public byte[] CopyPixels() => (byte[])_pixels.Clone();

    public Rgba8 PixelAt(int x, int y)
    {
        if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0 || y >= Height) throw new ArgumentOutOfRangeException(nameof(y));
        var offset = (y * Width + x) * 4;
        return new(_pixels[offset], _pixels[offset + 1], _pixels[offset + 2], _pixels[offset + 3]);
    }

    /// <summary>Crops by integer pixel coordinates. Empty, negative or out-of-image rectangles are rejected.</summary>
    public RasterImage Crop(PixelRect rectangle)
    {
        // Subtraction avoids overflow even for hostile Int32 rectangle values.
        if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 ||
            rectangle.Width > Width || rectangle.Height > Height ||
            rectangle.X > Width - rectangle.Width || rectangle.Y > Height - rectangle.Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "Crop must be wholly inside the image.");
        var cropped = new byte[rectangle.Width * rectangle.Height * 4];
        for (var y = 0; y < rectangle.Height; y++)
            _pixels.AsSpan(((rectangle.Y + y) * Width + rectangle.X) * 4, rectangle.Width * 4)
                .CopyTo(cropped.AsSpan(y * rectangle.Width * 4));
        return new(rectangle.Width, rectangle.Height, cropped);
    }

    /// <summary>Rotates exactly 90 degrees clockwise without interpolation or color conversion.</summary>
    public RasterImage RotateClockwise()
    {
        var rotated = new byte[_pixels.Length];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            _pixels.AsSpan((y * Width + x) * 4, 4)
                .CopyTo(rotated.AsSpan((x * Height + Height - 1 - y) * 4, 4));
        return new(Height, Width, rotated);
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > MaximumDimension) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > MaximumDimension) throw new ArgumentOutOfRangeException(nameof(height));
        if ((long)width * height > MaximumPixelCount)
            throw new ArgumentOutOfRangeException(nameof(height), "The image exceeds the pixel budget.");
    }
}

/// <summary>A proposed crop. RasterImage.Crop validates it against the source dimensions.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Explicit metadata-only serialization also avoids reflection over a ref-struct constructor.</summary>
internal sealed class RasterImageJsonConverter : JsonConverter<RasterImage>
{
    public RasterImageJsonConverter() { }
    public override RasterImage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raster images are loaded through the bounded PNG codec, not JSON metadata.");
    public override void Write(Utf8JsonWriter writer, RasterImage value, JsonSerializerOptions options)
    {
        string Name(string name) => options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        writer.WriteStartObject();
        writer.WriteNumber(Name(nameof(RasterImage.Width)), value.Width);
        writer.WriteNumber(Name(nameof(RasterImage.Height)), value.Height);
        writer.WriteString(Name(nameof(RasterImage.PixelChecksum)), value.PixelChecksum);
        writer.WriteEndObject();
    }
}
