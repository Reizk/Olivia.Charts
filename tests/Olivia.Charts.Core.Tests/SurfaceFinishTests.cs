using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceFinishTests
{
    [Theory]
    [InlineData(.35, 1.4)]
    [InlineData(-.55, 1.4)]
    [InlineData(.35, .55)]
    public void Both_material_sides_follow_unit_face_normals_and_srgb_gouraud_lighting(double pitch, double halfHeight)
    {
        double[] x = [-1, -.6, 1], y = [-1, .3, 1];
        var source = new SurfaceChartSource("unequal-fold", x, y, [-.6, .5, -.2, .9, -.5, .7, -.8, .4, -.1]);
        var camera = new Point3DCamera(yaw: .6, pitch: pitch, halfHeight: halfHeight);
        var color = new SurfaceColor(96, 144, 192);
        var options = new SurfaceRenderingOptions(true, true) { Material = new(color, color, color) };
        var frame = SurfaceRasterizer.Render(source, camera, 120, 100, -1, 1, false, SurfaceProjectionPlacement.Auto, options);
        var normals = new V[source.Vertices.Length];
        foreach (var t in frame.Triangles)
        {
            var n = Cross(P(t.B) - P(t.A), P(t.C) - P(t.A)).Unit();
            normals[t.A.Key] += n; normals[t.B.Key] += n; normals[t.C.Key] += n;
        }
        var right = new V(Math.Cos(camera.Yaw), 0, -Math.Sin(camera.Yaw));
        var up = new V(-Math.Sin(camera.Yaw) * Math.Sin(pitch), Math.Cos(pitch), -Math.Cos(camera.Yaw) * Math.Sin(pitch));
        var forward = Cross(right, up);
        var eye = forward * camera.Distance;
        var light = new V(-1.1, 1, 1.8).Unit();
        var checkedPixels = 0;
        for (var py = 0; py < frame.Height; py++)
            for (var px = 0; px < frame.Width; px++)
            {
                if (frame.HitTest(px, py) is not { } hit) continue;
                var a = hit.A; var b = hit.B; var c = hit.C;
                double Edge(SurfaceVertex u, SurfaceVertex v, double xx, double yy) =>
                    (v.X - u.X) * (yy - u.Y) - (v.Y - u.Y) * (xx - u.X);
                var area = Edge(a, b, c.X, c.Y);
                var wa = Edge(b, c, hit.InterpolatedX, hit.InterpolatedY) / area;
                var wb = Edge(c, a, hit.InterpolatedX, hit.InterpolatedY) / area;
                var wc = 1 - wa - wb;
                if (Math.Min(wa, Math.Min(wb, wc)) < .03) continue;
                var side = Dot(Cross(P(b) - P(a), P(c) - P(a)), eye - P(a)) < 0 ? -1 : 1;
                (double Diffuse, double Specular) At(SurfaceVertex vertex)
                {
                    var n = normals[vertex.Key].Unit() * side;
                    n = new(Dot(n, right), Dot(n, up), Dot(n, forward));
                    var p = P(vertex);
                    var view = new V(-Dot(p, right), -Dot(p, up), camera.Distance - Dot(p, forward)).Unit();
                    var d = Math.Max(0, Dot(n, light));
                    return (Math.Min(1, 105 / 255d + d), d > 0 ? 75 / 255d * Math.Pow(Math.Max(0, Dot(n, (light + view).Unit())), 9) : 0);
                }
                var la = At(a); var lb = At(b); var lc = At(c);
                // The raw hit gives world barycentrics; vertex colors instead use screen barycentrics.
                wa *= camera.Distance - Dot(P(a), forward);
                wb *= camera.Distance - Dot(P(b), forward);
                wc *= camera.Distance - Dot(P(c), forward);
                var sum = wa + wb + wc; wa /= sum; wb /= sum; wc /= sum;
                var diffuse = wa * la.Diffuse + wb * lb.Diffuse + wc * lc.Diffuse;
                var specular = wa * la.Specular + wb * lb.Specular + wc * lc.Specular;
                var offset = (py * frame.Width + px) * 4;
                foreach (var (channel, value) in new[] { (0, color.B), (1, color.G), (2, color.R) })
                    Assert.InRange(Math.Abs(frame.Pixels[offset + channel] - Math.Round(Math.Clamp(value * diffuse + 255 * specular, 0, 255))), 0, 1);
                checkedPixels++;
            }
        Assert.True(checkedPixels > 400);
        // Optional data-only fixture for the Windows WPF material comparison; no extra project.
        if (halfHeight == 1.4 && Environment.GetEnvironmentVariable("OLIVIA_SURFACE_WPF_FIXTURE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, pitch > 0 ? "front" : "back");
            File.WriteAllBytes(stem + ".bgra", frame.Pixels.ToArray());
            File.WriteAllText(stem + ".json", System.Text.Json.JsonSerializer.Serialize(new
            {
                frame.Width, frame.Height, camera.Yaw, camera.Pitch, camera.Distance, camera.HalfHeight, camera.Near, camera.Far,
                Positions = source.Vertices.Select(v => new[] { v.X, v.Z!.Value, v.Y }),
                Indices = frame.Triangles.SelectMany(t => new[] { t.A.Key, t.B.Key, t.C.Key }),
                CenterIdentities = frame.TriangleIndices,
                Color = new[] { color.R, color.G, color.B }
            }));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Edge_coverage_matches_four_sample_reference_without_changing_center_hits(bool perspective, bool domain)
    {
        var source = new SurfaceChartSource("coverage", [-1, 1], [-1, 1], [-.2, .1, .1, .4], 17);
        var camera = new Point3DCamera(yaw: .51, pitch: .64, halfHeight: 1.47);
        var paint = new SurfaceColor(83, 137, 191, 191);
        var options = new SurfaceRenderingOptions(perspective) { Material = new(paint, paint, paint, domain ? new(230, 230, 230) : null) };
        var center = SurfaceRasterizer.Render(source, camera, 80, 64, -1, 1, domain, SurfaceProjectionPlacement.Bottom, options);
        var edges = SurfaceRasterizer.Render(source, camera, 80, 64, -1, 1, domain, SurfaceProjectionPlacement.Bottom, options with { EdgeAntialiasing = true });
        var reference = SurfaceRasterizer.Render(source, camera, 160, 128, -1, 1, domain, SurfaceProjectionPlacement.Bottom, options);
        Assert.Equal(center.Depths.ToArray(), edges.Depths.ToArray());
        Assert.Equal(center.TriangleIndices.ToArray(), edges.TriangleIndices.ToArray());
        Assert.Equal(center.ProjectionPixels.ToArray(), edges.ProjectionPixels.ToArray());
        Assert.Equal(center.CoveredPixelCount, edges.CoveredPixelCount);
        Assert.Equal(0, center.EdgeSampledPixelCount);
        Assert.InRange(edges.EdgeSampledPixelCount, 1, edges.Width * edges.Height / 2);
        var changed = 0;
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 80; x++)
            {
                Assert.Equal(center.HitTest(x, y), edges.HitTest(x, y));
                var offset = (y * 80 + x) * 4;
                uint alpha = 0; uint[] channels = new uint[3];
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var p = ((y * 2 + dy) * 160 + x * 2 + dx) * 4;
                        var a = reference.Pixels[p + 3]; alpha += a;
                        for (var c = 0; c < 3; c++) channels[c] += (uint)(reference.Pixels[p + c] * a);
                    }
                Assert.Equal((byte)((alpha + 2) / 4), edges.Pixels[offset + 3]);
                for (var c = 0; c < 3; c++)
                    Assert.InRange(Math.Abs(edges.Pixels[offset + c] - (alpha == 0 ? 0 : (channels[c] + alpha / 2) / alpha)), 0, 1);
                if (edges.Pixels[offset + 3] != center.Pixels[offset + 3]) changed++;
            }
        Assert.True(changed > 30);
        var retained = edges.Pixels.ToArray();
        SurfaceRasterizer.Render(source, new(pitch: -.5), 80, 64, -1, 1, false, SurfaceProjectionPlacement.Auto, options with { EdgeAntialiasing = true });
        Assert.Equal(retained, edges.Pixels.ToArray());
    }

    private static V P(SurfaceVertex v) => new(v.X, v.Z!.Value, v.Y);
    private static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private readonly record struct V(double X, double Y, double Z)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator *(V a, double v) => new(a.X * v, a.Y * v, a.Z * v);
        internal V Unit() { var length = Math.Sqrt(Dot(this, this)); return length == 0 ? default : this * (1 / length); }
    }
}
