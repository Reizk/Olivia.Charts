using System.Reflection;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceDataTests
{
    [Fact]
    public void RectangularNonuniformMatrixRetainsOriginalOrderCoordinatesKeysAndGaps()
    {
        double[] x = [-7, 2.5];
        double[] y = [-3, 0, 12];
        double?[] values = [11, null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -9];
        var source = new SurfaceChartSource("raw-matrix", x, y, values, 37);
        x[0] = 400; y[0] = 500; values[0] = 600;

        Assert.Equal("raw-matrix", source.Id);
        Assert.Equal(37, source.Version);
        Assert.Equal(2, source.XCount);
        Assert.Equal(3, source.YCount);
        Assert.Equal(new double[] { -7, 2.5 }, source.XArguments);
        Assert.Equal(new double[] { -3, 0, 12 }, source.YArguments);
        Assert.Equal(new SurfaceVertex(0, 0, 0, -7, -3, 11), source.VertexAt(0, 0));
        Assert.Equal(new SurfaceVertex(5, 1, 2, 2.5, 12, -9), source.VertexAt(1, 2));
        Assert.Equal(new long[] { 0, 1, 2, 3, 4, 5 }, source.Vertices.Select(vertex => vertex.Key));
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1 }, source.Vertices.Select(vertex => vertex.XIndex));
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, source.Vertices.Select(vertex => vertex.YIndex));
        Assert.False(source.Vertices[0].IsGap);
        Assert.All(source.Vertices.Skip(1).Take(4), vertex => Assert.True(vertex.IsGap));
        Assert.False(source.Vertices[5].IsGap);
        Assert.Null(source.Values[1]);
        Assert.True(double.IsNaN(source.Values[2]!.Value));
        Assert.Equal(double.PositiveInfinity, source.Values[3]);
        Assert.Equal(double.NegativeInfinity, source.Values[4]);
        Assert.Throws<NotSupportedException>(() => ((IList<double?>)source.Values)[0] = 0);
        Assert.Throws<NotSupportedException>(() => ((IList<SurfaceVertex>)source.Vertices)[0] = default);
    }

    [Fact]
    public void AxisAndMatrixAdmissionRejectInvalidInputInsteadOfSortingOrThinning()
    {
        double[][] invalidAxes = [[], [0], [0, 0], [1, 0], [0, double.NaN],
            [double.NegativeInfinity, 1], [0, double.PositiveInfinity],
            Enumerable.Range(0, 102).Select(i => (double)i).ToArray()];
        foreach (var axis in invalidAxes)
        {
            Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource("bad-x", axis, [0, 1], [0, 1, 2, 3]));
            Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource("bad-y", [0, 1], axis, [0, 1, 2, 3]));
        }
        Assert.Throws<ArgumentNullException>(() => new SurfaceChartSource("null-x", null!, [0, 1], [0, 1, 2, 3]));
        Assert.Throws<ArgumentNullException>(() => new SurfaceChartSource("null-y", [0, 1], null!, [0, 1, 2, 3]));
        Assert.Throws<ArgumentNullException>(() => new SurfaceChartSource("null-values", [0, 1], [0, 1], null!));
        Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource("short", [0, 1], [0, 1], [0, 1, 2]));
        Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource("long", [0, 1], [0, 1], [0, 1, 2, 3, 4]));
        Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource(" ", [0, 1], [0, 1], [0, 1, 2, 3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceChartSource("negative-version", [0, 1], [0, 1], [0, 1, 2, 3], -1));
        var valid = new SurfaceChartSource("valid", [0, 1], [0, 1], [0, 1, 2, 3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.VertexAt(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.VertexAt(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.VertexAt(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.VertexAt(0, 2));
    }

    [Fact]
    public void AxisAndHeightEnumerationStopAtTheFirstExcessEntry()
    {
        var axisReads = 0;
        IEnumerable<double> InfiniteAxis()
        {
            while (true) yield return axisReads++;
        }
        Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource("axis-limit", InfiniteAxis(), [0, 1], []));
        Assert.Equal(102, axisReads);
        var valueReads = 0;
        IEnumerable<double?> InfiniteValues()
        {
            while (true) yield return valueReads++;
        }
        Assert.ThrowsAny<ArgumentException>(() => new SurfaceChartSource("value-limit", [0, 1], [0, 1], InfiniteValues()));
        Assert.Equal(5, valueReads);
    }

    [Fact]
    public void MaximumMatrixKeepsEveryOriginalVertexWithoutDecimation()
    {
        var axis = Enumerable.Range(0, 101).Select(index => index * .25).ToArray();
        var source = new SurfaceChartSource("maximum", axis, axis,
            Enumerable.Range(0, 10201).Select(index => (double?)index), 9);
        Assert.Equal(10201, source.Vertices.Length);
        Assert.Equal(10201, source.Values.Length);
        Assert.Equal(new SurfaceVertex(10200, 100, 100, 25, 25, 10200), source.VertexAt(100, 100));
        Assert.Equal(new SurfaceVertex(5153, 51, 2, 12.75, .5, 5153), source.VertexAt(51, 2));
    }

    [Theory]
    [InlineData(2, -1.5, 1.5)]
    [InlineData(5, -2.5, 2.5)]
    [InlineData(51, -18.5, 18.5)]
    [InlineData(101, -34.5, 34.5)]
    public void GeneratorUsesIntegerThirdDomainsAndExactArgumentGrid(int side, double minimum, double maximum)
    {
        var generator = new SurfaceDataGenerator(side);
        Assert.Equal(minimum, generator.MinValue);
        Assert.Equal(maximum, generator.MaxValue);
        Assert.Equal(side * .5, generator.Sensitivity);
        var source = generator.Generate("domain", 71);
        Assert.Equal(side, source.XCount);
        Assert.Equal(side, source.YCount);
        Assert.Equal(side * side, source.Values.Length);
        Assert.Equal(Enumerable.Range(0, side).Select(i => (double)i), source.XArguments);
        Assert.Equal(source.XArguments.ToArray(), source.YArguments.ToArray());
        Assert.Equal(71, source.Version);
        Assert.All(source.Values, value => Assert.True(value.HasValue && double.IsFinite(value.Value)));
        Assert.Equal(1, generator.GeneratedFrames);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(51)]
    public void FirstFrameMatchesIndependentWeightedProductAfterMovementAndSensitivityAdvance(int side)
    {
        // Only the replacement System.Random stream is asserted, not DevExpress's proprietary RNG.
        // The oracle expands the recurrence as a weighted product and uses its cosine identity;
        // it does not replay the generator's nested update-and-blend implementation.
        var initial = ReadInitialElevations(new Random(0), side);
        var step = side / 66d;
        var moved = initial.Select(p => new Elevation(p.X + step, p.Y + step, p.Height + step)).ToArray();
        var generator = new SurfaceDataGenerator(side);
        var source = generator.Generate("first");
        Assert.Equal(25, SurfaceDataGenerator.ElevationCount);
        Near(side * .5 + .2, generator.Sensitivity);
        foreach (var (x, y) in ProbeCoordinates(side))
            Near(ExpandedBlend(moved, x, y, side * .5 + .2), source.VertexAt(x, y).Z!.Value);
        if (side == 2)
        {
            // Every height is exactly 1/33 here. Convex blending cannot add the 25 heights.
            Assert.All(source.Values, height => Assert.InRange(height!.Value, 0, step));
            Assert.Contains(source.Values, height => height > 0);
        }
    }

    [Fact]
    public void PriorCoordinateBounceAllowsOvershootAndUsesStrictHeightBounds()
    {
        // N=66 makes the motion step exactly 1. Seed zero has planar 65 and height 21 witnesses.
        var initial = ReadInitialElevations(new Random(0), 66);
        Assert.Equal(new Elevation(2, 65, 15), initial[5]);
        Assert.Equal(new Elevation(55, 2, 21), initial[7]);
        Assert.Equal(new Elevation(65, 50, 6), initial[11]);
        var generator = new SurfaceDataGenerator(66);
        for (var frame = 1; frame <= 4; frame++)
        {
            // Plane reflection is at 66; height reflection is at 23, one step beyond q=22.
            var moved = initial.Select(p => new Elevation(
                66 - Math.Abs(66 - (p.X + frame)),
                66 - Math.Abs(66 - (p.Y + frame)),
                23 - Math.Abs(23 - (p.Height + frame)))).ToArray();
            var source = generator.Generate("bounce", frame);
            foreach (var (x, y) in new[] { (0, 0), (2, 65), (65, 50), (55, 2), (14, 31), (33, 33), (65, 65) })
                Near(ExpandedBlend(moved, x, y, 33 + frame * .2), source.VertexAt(x, y).Z!.Value);
        }
    }

    [Fact]
    public void ZeroIntegerHeightLimitOscillatesWithoutClampingAndSensitivityReversesAfterOvershoot()
    {
        var generator = new SurfaceDataGenerator(2);
        Assert.Contains(generator.Generate("one").Values, height => height > 0);
        Near(1.2, generator.Sensitivity);
        Assert.All(generator.Generate("two").Values, height => Near(0, height!.Value));
        Near(1.4, generator.Sensitivity);
        Assert.Contains(generator.Generate("three").Values, height => height < 0);
        Near(1.2, generator.Sensitivity);
        Assert.All(generator.Generate("four").Values, height => Near(0, height!.Value));
        Near(1, generator.Sensitivity);
        foreach (var sensitivity in new[] { .8, .6, .4, .2, .4 })
        {
            generator.Generate("sensitivity");
            Near(sensitivity, generator.Sensitivity);
        }
    }

    [Fact]
    public void ResizeKeepsRandomStreamAndDownwardSensitivityDirectionButRecreatesElevations()
    {
        var fixtureRandom = new Random(0);
        ReadInitialElevations(fixtureRandom, 2); // Initial recreation consumes 25 triples.
        var generator = new SurfaceDataGenerator(2);
        generator.Generate("one"); generator.Generate("two"); generator.Generate("three");
        Near(1.2, generator.Sensitivity); // Direction is now negative.
        var beforeFrames = generator.GeneratedFrames;
        generator.Resize(5);
        Near(2.5, generator.Sensitivity);
        Assert.Equal(beforeFrames, generator.GeneratedFrames);
        var initial = ReadInitialElevations(fixtureRandom, 5);
        var step = 5d / 66;
        var moved = initial.Select(p => new Elevation(p.X + step, p.Y + step, p.Height + step)).ToArray();
        var resized = generator.Generate("resized", 8);
        Near(2.3, generator.Sensitivity); // Resetting the direction would wrongly give 2.7.
        foreach (var (x, y) in ProbeCoordinates(5))
            Near(ExpandedBlend(moved, x, y, 2.3), resized.VertexAt(x, y).Z!.Value);
        var fresh = new SurfaceDataGenerator(5).Generate("fresh");
        Assert.False(fresh.Values.SequenceEqual(resized.Values));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(102)]
    [InlineData(int.MaxValue)]
    public void InvalidGeneratorResizeAndInvalidFrameIdentityDoNotAdvanceState(int invalidSide)
    {
        var actual = new SurfaceDataGenerator(5);
        var control = new SurfaceDataGenerator(5);
        Assert.Equal(control.Generate("before").Values.ToArray(), actual.Generate("before").Values.ToArray());
        var sensitivity = actual.Sensitivity;
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.Resize(invalidSide));
        Assert.ThrowsAny<ArgumentException>(() => actual.Generate(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.Generate("negative", -1));
        Assert.Equal(5, actual.SideLength);
        Assert.Equal(sensitivity, actual.Sensitivity);
        Assert.Equal(1, actual.GeneratedFrames);
        Assert.Equal(control.Generate("next").Values.ToArray(), actual.Generate("next").Values.ToArray());
        actual.Resize(7); control.Resize(7);
        Assert.Equal(control.Generate("next-size").Values.ToArray(), actual.Generate("next-size").Values.ToArray());
    }

    [Fact]
    public void SessionStartsWithOneDefaultFrameAndOnlyAcceptedTicksPublishNewVersions()
    {
        using var session = new RealtimeSurfaceSession();
        var initial = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
        var initialValues = initial.Values.ToArray();
        Assert.Equal(50, session.Size);
        Assert.Equal(51, initial.XCount);
        Assert.Equal(51, initial.YCount);
        Assert.Equal(2601, initial.Values.Length);
        Assert.Equal(-18.5, session.MinValue);
        Assert.Equal(18.5, session.MaxValue);
        Assert.Equal(TimeSpan.FromMilliseconds(40), RealtimeSurfaceSession.TickInterval);
        Assert.False(session.IsRunning);
        Assert.False(session.Tick(session.Generation));
        Assert.Equal(0, session.ProducedTicks);
        var unstartedToken = session.Generation;
        session.Start();
        Assert.True(session.IsRunning);
        Assert.NotEqual(unstartedToken, session.Generation);
        Assert.False(session.Tick(unstartedToken));
        Assert.True(session.Tick(session.Generation));
        var next = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
        Assert.NotSame(initial, next);
        Assert.Equal(initial.Id, next.Id);
        Assert.Equal(initial.Version + 1, next.Version);
        Assert.Equal(1, session.ProducedTicks);
        Assert.Equal(initialValues, initial.Values);
        Assert.Equal(initial.Vertices.Select(v => v.Key), next.Vertices.Select(v => v.Key));
        var currentToken = session.Generation;
        session.Start();
        Assert.Equal(currentToken, session.Generation);
    }

    [Fact]
    public async Task CoreOwnsNoBackgroundTimerOrPendingFrameProducer()
    {
        using var session = new RealtimeSurfaceSession(15);
        session.Start();
        var initial = session.CurrentSource;
        await Task.Delay(100);
        Assert.Same(initial, session.CurrentSource);
        Assert.Equal(0, session.ProducedTicks);
        Assert.True(session.Tick(session.Generation));
        var next = session.CurrentSource;
        await Task.Delay(60);
        Assert.Same(next, session.CurrentSource);
        Assert.Equal(1, session.ProducedTicks);
    }

    [Fact]
    public void PauseRejectsOldCallbacksAndResumeDoesNotGenerateUntilTheNextTick()
    {
        using var session = new RealtimeSurfaceSession(15);
        session.Start();
        var beforePause = session.Generation;
        Assert.True(session.Tick(beforePause));
        var source = session.CurrentSource;
        session.Pause();
        var pausedToken = session.Generation;
        Assert.True(session.IsPaused);
        Assert.False(session.IsRunning);
        Assert.False(session.Tick(beforePause));
        Assert.False(session.Tick(pausedToken));
        session.Pause();
        Assert.Equal(pausedToken, session.Generation);
        session.Resume();
        Assert.False(session.IsPaused);
        Assert.True(session.IsRunning);
        Assert.False(session.Tick(beforePause));
        Assert.False(session.Tick(pausedToken));
        Assert.Same(source, session.CurrentSource);
        Assert.Equal(1, session.ProducedTicks);
        Assert.True(session.Tick(session.Generation));
        Assert.Equal(2, session.ProducedTicks);
    }

    [Fact]
    public void SuspendRestorePreservesPauseChoiceIncludingChangesMadeWhileDetached()
    {
        using var session = new RealtimeSurfaceSession(15);
        session.Start(); session.Pause();
        var beforeSuspend = session.Generation;
        var source = session.CurrentSource;
        session.Suspend(); session.Restore();
        Assert.True(session.IsPaused);
        Assert.False(session.IsSuspended);
        Assert.False(session.IsRunning);
        Assert.False(session.Tick(beforeSuspend));
        Assert.Same(source, session.CurrentSource);
        session.Suspend(); session.Resume();
        Assert.False(session.IsPaused);
        Assert.True(session.IsSuspended);
        Assert.False(session.IsRunning);
        Assert.False(session.Tick(session.Generation));
        session.Restore();
        Assert.True(session.IsRunning);
        Assert.True(session.Tick(session.Generation));
        session.Suspend(); session.Pause(); session.Restore();
        Assert.True(session.IsPaused);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void PausedResizeImmediatelyPublishesMaximumGridAndInvalidatesOldCallbacks()
    {
        using var session = new RealtimeSurfaceSession(15);
        session.Start(); session.Pause();
        var old = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
        var retained = old.Values.ToArray();
        var oldToken = session.Generation;
        session.Resize(100);
        var resized = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
        Assert.Equal(100, session.Size);
        Assert.Equal(101, resized.XCount);
        Assert.Equal(101, resized.YCount);
        Assert.Equal(10201, resized.Values.Length);
        Assert.All(resized.Values, value => Assert.True(value.HasValue && double.IsFinite(value.Value)));
        Assert.Contains(resized.Values, value => value != 0);
        Assert.Equal(old.Id, resized.Id);
        Assert.Equal(old.Version + 1, resized.Version);
        Assert.Equal(-34.5, session.MinValue);
        Assert.Equal(34.5, session.MaxValue);
        Assert.True(session.IsPaused);
        Assert.False(session.IsRunning);
        Assert.Equal(0, session.ProducedTicks);
        Assert.False(session.Tick(oldToken));
        Assert.False(session.Tick(session.Generation));
        Assert.Equal(retained, old.Values);
        session.Resume();
        Assert.False(session.Tick(oldToken));
        Assert.True(session.Tick(session.Generation));
    }

    [Fact]
    public void ResizePreservesRunningAndSuspendedStatesAndSameSizeIsANoop()
    {
        using var session = new RealtimeSurfaceSession(15);
        session.Start();
        var token = session.Generation;
        var source = session.CurrentSource;
        session.Resize(15);
        Assert.Equal(token, session.Generation);
        Assert.Same(source, session.CurrentSource);
        session.Resize(16);
        Assert.True(session.IsRunning);
        Assert.False(session.Tick(token));
        Assert.True(session.Tick(session.Generation));
        session.Suspend();
        var suspendedToken = session.Generation;
        session.Resize(17);
        Assert.True(session.IsSuspended);
        Assert.False(session.IsPaused);
        Assert.False(session.IsRunning);
        Assert.False(session.Tick(suspendedToken));
        session.Restore();
        Assert.True(session.IsRunning);
        Assert.False(session.Tick(suspendedToken));
        Assert.True(session.Tick(session.Generation));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(14)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void InvalidSessionResizeLeavesPublishedSourceGenerationAndFutureSequenceUnchanged(int invalidSize)
    {
        using var session = new RealtimeSurfaceSession(15);
        using var control = new RealtimeSurfaceSession(15);
        session.Start(); control.Start();
        var source = session.CurrentSource;
        var token = session.Generation;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Resize(invalidSize));
        Assert.Same(source, session.CurrentSource);
        Assert.Equal(token, session.Generation);
        Assert.Equal(15, session.Size);
        Assert.True(session.IsRunning);
        Assert.Equal(0, session.ProducedTicks);
        Assert.True(session.Tick(token));
        Assert.True(control.Tick(control.Generation));
        Assert.Equal(control.CurrentSource!.Values.ToArray(), session.CurrentSource!.Values.ToArray());
        session.Resize(18); control.Resize(18);
        Assert.Equal(control.CurrentSource!.Values.ToArray(), session.CurrentSource!.Values.ToArray());
    }

    [Fact]
    public void GenerationTokensCannotCrossDistinctSessionLifetimes()
    {
        using var oldSession = new RealtimeSurfaceSession(15);
        oldSession.Start();
        var oldToken = oldSession.Generation;
        oldSession.Dispose();
        using var newSession = new RealtimeSurfaceSession(15);
        newSession.Start();
        Assert.NotEqual(oldToken, newSession.Generation);
        Assert.False(newSession.Tick(oldToken));
        Assert.True(newSession.Tick(newSession.Generation));
        using var parallelSession = new RealtimeSurfaceSession(15);
        parallelSession.Start();
        Assert.False(newSession.Tick(parallelSession.Generation));
        Assert.False(parallelSession.Tick(newSession.Generation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopAndDisposeReleaseResourcesAreTerminalAndKeepHandedOutSnapshotsValid(bool dispose)
    {
        var session = new RealtimeSurfaceSession(15);
        session.Start();
        Assert.True(session.Tick(session.Generation));
        var source = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
        var retained = source.Values.ToArray();
        var token = session.Generation;
        // Directly verify the resource-release invariant without adding a production diagnostics API.
        var generatorField = Assert.Single(typeof(RealtimeSurfaceSession)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SurfaceDataGenerator));
        Assert.NotNull(generatorField.GetValue(session));
        if (dispose) session.Dispose(); else session.Stop();
        Assert.True(session.IsStopped);
        Assert.False(session.IsRunning);
        Assert.Null(session.CurrentSource);
        Assert.Null(generatorField.GetValue(session));
        Assert.False(session.Tick(token));
        Assert.False(session.Tick(session.Generation));
        var stoppedToken = session.Generation;
        session.Pause(); session.Resume(); session.Suspend(); session.Restore();
        session.Stop(); session.Dispose();
        Assert.Equal(stoppedToken, session.Generation);
        Assert.Null(session.CurrentSource);
        Assert.Null(generatorField.GetValue(session));
        Assert.Throws<InvalidOperationException>(() => session.Start());
        Assert.Throws<InvalidOperationException>(() => session.Resize(16));
        Assert.Equal(retained, source.Values);
        Assert.Equal(1, session.ProducedTicks);
    }

    private readonly record struct Elevation(double X, double Y, double Height);

    private static Elevation[] ReadInitialElevations(Random random, int side)
    {
        var result = new Elevation[25];
        var q = side / 3;
        for (var i = 0; i < result.Length; i++)
        {
            var x = random.Next(0, side);
            var height = random.Next(-q, q);
            var y = random.Next(0, side);
            result[i] = new(x, y, height);
        }
        return result;
    }

    private static IEnumerable<(int X, int Y)> ProbeCoordinates(int side)
    {
        if (side <= 5)
        {
            for (var x = 0; x < side; x++)
                for (var y = 0; y < side; y++) yield return (x, y);
        }
        else
        {
            yield return (0, 0); yield return (0, side - 1);
            yield return (side - 1, 0); yield return (side - 1, side - 1);
            yield return (side / 2, side / 2); yield return (7, 19);
        }
    }

    private static double ExpandedBlend(Elevation[] elevations, double x, double y, double sensitivity)
    {
        // z_n = sum_i [height_i * weight_i * product_(j>i) (1 - weight_j)].
        // Reverse accumulation exposes ordering, unlike a sum of independent bumps.
        var value = 0d;
        var subsequentRetention = 1d;
        for (var index = elevations.Length - 1; index >= 0; index--)
        {
            var point = elevations[index];
            var distance = Math.Sqrt(Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2));
            var weight = distance > sensitivity ? 0 :
                (1 - Math.Cos((1 - distance / sensitivity) * 10 / Math.PI)) / 2;
            value += point.Height * weight * subsequentRetention;
            subsequentRetention *= 1 - weight;
        }
        return value;
    }

    private static void Near(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, 1e-10 * Math.Max(1, Math.Abs(expected)));
}
