using System.Collections.Immutable;

namespace Olivia.Core.Workspaces;

/// <summary>Seeded local eight-channel input, matching the official demo's signal families and scales.</summary>
internal sealed class RealtimeSensorGenerator(int seed)
{
    private readonly Random _random = new(seed);
    private readonly double[] _offsets = new double[8];
    private long _counter;

    public RealtimeSensorRow Next(long key, long ticks)
    {
        _counter++;
        var arg = new DateTime(ticks, DateTimeKind.Utc).ToOADate() * 250000d;
        var values = ImmutableArray.CreateBuilder<double?>(8);
        int[] lower = [300, 100, 100, 50, 300, 400, 200, 300];
        int[] upper = [500, 300, 500, 100, 400, 1000, 300, 350];
        for (var channel = 0; channel < 8; channel++)
        {
            if (_counter % _random.Next(lower[channel], upper[channel]) == 0)
                _offsets[channel] += _random.Next(10, 20) * Math.Sign(_random.NextDouble() - .5);
            if (_offsets[channel] < -30) _offsets[channel] += 10;
            if (_offsets[channel] > 30) _offsets[channel] -= 10;
            var value = channel switch
            {
                0 => 5 * Math.Sin(2.5 * Math.Cos(arg)) + 100 + (_random.NextDouble() - .5) * 5,
                1 => 4 * Math.Sin(7 * Math.Cos(arg - 1.5)) + 90 + (_random.NextDouble() - .5) * 7,
                2 => 10 * (Math.Sin(arg) + Math.Sin(arg / 1.2) + Math.Sin(arg / 1.5)) + 100 + _random.NextDouble() * 12,
                3 => 10 * (Math.Cos(arg + 1.5) + Math.Sin(arg / 1.2 + .5) + Math.Cos(arg / 1.5 + .3)) + 120 + _random.NextDouble() * 15,
                4 => 15 * Math.Cos(Math.Tan(arg + _random.NextDouble() / 10)) + 500 + _random.NextDouble() * 15,
                5 => 20 * Math.Sin(Math.Tan(arg + 1)) + 450 + _random.NextDouble() * 9,
                6 => 30 * Math.Abs(Math.Sin(Math.Tan(arg + 1))) + Math.Cos(arg) + Math.Sin(arg) + 750 + _random.NextDouble() * 15,
                _ => 30 * (1 - Math.Cos(arg)) / _random.Next(1, 5) + 700 + _random.NextDouble() * 15
            };
            values.Add(value + _offsets[channel]);
        }
        return new(key, ticks, values.MoveToImmutable());
    }
}
