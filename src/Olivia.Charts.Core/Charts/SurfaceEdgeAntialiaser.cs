using System.Buffers;
using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Four coverage samples only near visible layer/occlusion/color boundaries.
/// Pooled scratch is never published. Center depth and identity remain the picking contract.
/// This is adaptive edge coverage, not full-scene MSAA or a substitute for subpixel geometry.</summary>
internal sealed class SurfaceEdgeAntialiaser : IDisposable
{
    private readonly int[] _map;
    private readonly int _length;
    internal double[] Depths { get; }
    internal uint[] Colors { get; }
    internal int PixelCount { get; }

    private SurfaceEdgeAntialiaser(int[] map, int length, int count)
    {
        _map = map; _length = length; PixelCount = count;
        Depths = ArrayPool<double>.Shared.Rent(count * 4);
        try { Colors = ArrayPool<uint>.Shared.Rent(count * 4); }
        catch { ArrayPool<double>.Shared.Return(Depths); throw; }
        Array.Fill(Depths, double.PositiveInfinity, 0, count * 4);
        Array.Clear(Colors, 0, count * 4);
    }

    internal static SurfaceEdgeAntialiaser? Create(int width, int height, int yCount,
        ImmutableArray<SurfaceTriangle> triangles, int[] indices, byte[] projections, byte[] pixels,
        CancellationToken cancellationToken)
    {
        var length = width * height;
        var map = ArrayPool<int>.Shared.Rent(length);
        try
        {
            Array.Fill(map, -1, 0, length);
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var p = y * width + x;
                    if (x + 1 < width && Different(p, p + 1)) { Mark(x, y); Mark(x + 1, y); }
                    if (y + 1 < height && Different(p, p + width)) { Mark(x, y); Mark(x, y + 1); }
                }
            }
            var count = 0;
            for (var i = 0; i < length; i++) if (map[i] == -2) map[i] = count++ * 4;
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) { ArrayPool<int>.Shared.Return(map); return null; }
            return new(map, length, count);
        }
        catch { ArrayPool<int>.Shared.Return(map); throw; }

        bool Different(int a, int b)
        {
            var ia = indices[a]; var ib = indices[b];
            if (ia == -1 && ib == -1) return false;
            if (ia < 0 || ib < 0) return ia != ib;
            if (projections.Length != 0 && projections[a] != projections[b]) return true;
            var ka = triangles[ia].A.Key; var kb = triangles[ib].A.Key;
            if (Math.Abs(ka / yCount - kb / yCount) > 1 || Math.Abs(ka % yCount - kb % yCount) > 1) return true;
            // Also sample a sharp two-sided lighting transition, but not every mesh diagonal.
            for (var c = 0; c < 3; c++) if (Math.Abs(pixels[a * 4 + c] - pixels[b * 4 + c]) > 48) return true;
            return false;
        }
        void Mark(int x, int y)
        {
            for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++) map[yy * width + xx] = -2;
        }
    }

    internal int SampleBase(int pixel) => _map[pixel];

    internal void Resolve(byte[] pixels, CancellationToken cancellationToken)
    {
        for (var p = 0; p < _length; p++)
        {
            if ((p & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var start = _map[p];
            if (start < 0) continue;
            uint a = 0, b = 0, g = 0, r = 0;
            for (var s = 0; s < 4; s++)
            {
                var color = Colors[start + s]; var alpha = color >> 24;
                a += alpha; b += (color & 255) * alpha;
                g += ((color >> 8) & 255) * alpha; r += ((color >> 16) & 255) * alpha;
            }
            pixels[p * 4] = a == 0 ? (byte)0 : (byte)((b + a / 2) / a);
            pixels[p * 4 + 1] = a == 0 ? (byte)0 : (byte)((g + a / 2) / a);
            pixels[p * 4 + 2] = a == 0 ? (byte)0 : (byte)((r + a / 2) / a);
            pixels[p * 4 + 3] = (byte)((a + 2) / 4);
        }
    }

    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(_map);
        ArrayPool<double>.Shared.Return(Depths);
        ArrayPool<uint>.Shared.Return(Colors);
    }
}
