using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>An original matrix vertex, not a resampled or interpolated height. Chart X/Y are
/// argument axes and chart Z is the height. Null and nonfinite heights remain explicit gaps.</summary>
public readonly record struct SurfaceVertex(long Key, int XIndex, int YIndex, double X, double Y, double? Z)
{
    public bool IsGap => !Z.HasValue || !double.IsFinite(Z.Value);
}

/// <summary>Immutable rectangular matrix in X-outer/Y-inner order: index = ix * YCount + iy.
/// Axes are finite, strictly increasing and may be nonuniform. Bounds are admission limits,
/// never a request to silently thin data. Vertex keys are original flat matrix indices within
/// this source/version; changing the shape creates a new version, not the same raw identity.</summary>
public sealed class SurfaceChartSource
{
    public const int MaximumAxisCount = 101;
    public const int MaximumVertices = MaximumAxisCount * MaximumAxisCount;

    public SurfaceChartSource(string id, IEnumerable<double> xArguments, IEnumerable<double> yArguments,
        IEnumerable<double?> values, long version = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        XArguments = CopyAxis(xArguments, nameof(xArguments));
        YArguments = CopyAxis(yArguments, nameof(yArguments));
        Values = CopyMatrix(values, XCount * YCount);
        var vertices = ImmutableArray.CreateBuilder<SurfaceVertex>(Values.Length);
        for (var ix = 0; ix < XCount; ix++)
            for (var iy = 0; iy < YCount; iy++)
            {
                var index = ix * YCount + iy;
                vertices.Add(new(index, ix, iy, XArguments[ix], YArguments[iy], Values[index]));
            }
        Vertices = vertices.MoveToImmutable();
        Id = id;
        Version = version;
    }

    public string Id { get; }
    public long Version { get; }
    public ImmutableArray<double> XArguments { get; }
    public ImmutableArray<double> YArguments { get; }
    public ImmutableArray<double?> Values { get; }
    public ImmutableArray<SurfaceVertex> Vertices { get; }
    public int XCount => XArguments.Length;
    public int YCount => YArguments.Length;

    public SurfaceVertex VertexAt(int ix, int iy)
    {
        if (ix < 0 || ix >= XCount) throw new ArgumentOutOfRangeException(nameof(ix));
        if (iy < 0 || iy >= YCount) throw new ArgumentOutOfRangeException(nameof(iy));
        return Vertices[ix * YCount + iy];
    }

    private static ImmutableArray<double?> CopyMatrix(IEnumerable<double?> values, int count)
    {
        ArgumentNullException.ThrowIfNull(values);
        // The validated axes already fix the exact count. Avoid geometric growth and the
        // final immutable copy; transferred storage belongs only to this published source.
        var result = ImmutableArray.CreateBuilder<double?>(count);
        using var iterator = values.GetEnumerator();
        while (iterator.MoveNext())
        {
            if (result.Count == count)
                throw new ArgumentException($"Admission limit {count} exceeded.", nameof(values));
            result.Add(iterator.Current);
        }
        if (result.Count != count)
            throw new ArgumentException("The height count must exactly match the rectangular matrix.", nameof(values));
        return result.MoveToImmutable();
    }

    private static ImmutableArray<double> CopyAxis(IEnumerable<double> arguments, string parameter)
    {
        var axis = BarChartSource.CopyBounded(arguments, MaximumAxisCount, parameter);
        if (axis.Length < 2)
            throw new ArgumentException("A surface axis requires at least two arguments.", parameter);
        for (var index = 0; index < axis.Length; index++)
            if (!double.IsFinite(axis[index]) || (index > 0 && axis[index] <= axis[index - 1]))
                throw new ArgumentException("Surface arguments must be finite and strictly increasing.", parameter);
        return axis;
    }
}

/// <summary>Algorithm-compatible implementation of the official 25-elevation surface example.
/// System.Random(seed), default zero, is an explicit compatibility substitution for the proprietary
/// NonCryptographicRandom: seeded output equivalence with that RNG is NOT claimed. Generator
/// coordinates are mapped to chart arguments X/Y and chart height Z, not treated as XYZ points.
/// This mutable generator is synchronous; RealtimeSurfaceSession serializes its access.</summary>
public sealed class SurfaceDataGenerator
{
    public const int ElevationCount = 25;
    private const double OffsetFactor = Math.PI * 3d / 2d;
    private const double RadiusFactor = 10d / Math.PI;
    private readonly Random _random;
    private readonly Elevation[] _elevations = new Elevation[ElevationCount];
    private ImmutableArray<double> _arguments;
    private int _sensitivityDirection = 1;

    public SurfaceDataGenerator(int sideLength = 51, int seed = 0)
    {
        ValidateSideLength(sideLength);
        _random = new(seed);
        Resize(sideLength);
    }

    public int SideLength { get; private set; }
    public double Sensitivity { get; private set; }
    public long GeneratedFrames { get; private set; }
    public double MinValue => -(SideLength / 3) - 1.5;
    public double MaxValue => SideLength / 3 + 1.5;

    /// <summary>Recreates all elevations, resetting their directions and sensitivity, but
    /// deliberately retains the random stream and sensitivity direction just like the reference.</summary>
    public void Resize(int sideLength)
    {
        ValidateSideLength(sideLength);
        SideLength = sideLength;
        Sensitivity = sideLength * .5;
        _arguments = Enumerable.Range(0, sideLength).Select(value => (double)value).ToImmutableArray();
        for (var index = 0; index < _elevations.Length; index++)
        {
            // Preserve reference RNG call order: planar X, elevation height, planar Y.
            var x = _random.Next(0, sideLength);
            var height = _random.Next(-sideLength / 3, sideLength / 3);
            var y = _random.Next(0, sideLength);
            _elevations[index] = new(x, y, height);
        }
    }

    /// <summary>Moves elevations and advances sensitivity BEFORE producing the first or next
    /// matrix. Elevations blend sequentially into a zeroed matrix, never add independent waves.</summary>
    public SurfaceChartSource Generate(string id, long version = 0)
    {
        BarChartSource.ValidateText(id, nameof(id), identity: true);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        var frames = checked(GeneratedFrames + 1);
        var step = SideLength / 66d;
        var heightLimit = SideLength / 3;
        for (var index = 0; index < _elevations.Length; index++)
        {
            ref var elevation = ref _elevations[index];
            // Boundary tests use the PREVIOUS position. Overshoot is retained, not clamped.
            if (elevation.X < 0 || elevation.X >= SideLength) elevation.DirectionX *= -1;
            if (elevation.Y < 0 || elevation.Y >= SideLength) elevation.DirectionY *= -1;
            if (elevation.Height < -heightLimit || elevation.Height > heightLimit) elevation.DirectionHeight *= -1;
            elevation.X += step * elevation.DirectionX;
            elevation.Y += step * elevation.DirectionY;
            elevation.Height += step * elevation.DirectionHeight;
        }
        if (Sensitivity < SideLength * .15) _sensitivityDirection = 1;
        if (Sensitivity > SideLength * .6) _sensitivityDirection = -1;
        Sensitivity += _sensitivityDirection * .2;

        var heights = new double[SideLength * SideLength];
        foreach (var elevation in _elevations)
        {
            var counter = 0;
            for (var x = 0; x < SideLength; x++)
                for (var y = 0; y < SideLength; y++, counter++)
                {
                    var dx = elevation.X - x;
                    var dy = elevation.Y - y;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance > Sensitivity) continue;
                    var percent = 1 - distance / Sensitivity;
                    var coefficient = elevation.Height - heights[counter];
                    heights[counter] += coefficient * (Math.Sin(percent * RadiusFactor + OffsetFactor) + 1) / 2;
                }
        }
        var source = new SurfaceChartSource(id, _arguments, _arguments,
            heights.Select(value => (double?)value), version);
        GeneratedFrames = frames;
        return source;
    }

    private static void ValidateSideLength(int sideLength)
    {
        if (sideLength is < 2 or > SurfaceChartSource.MaximumAxisCount)
            throw new ArgumentOutOfRangeException(nameof(sideLength));
    }

    private struct Elevation(double x, double y, double height)
    {
        public double X = x;
        public double Y = y;
        public double Height = height;
        public int DirectionX = 1;
        public int DirectionY = 1;
        public int DirectionHeight = 1;
    }
}
