using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Olivia.Core.Editing;

namespace Olivia.Avalonia.Editing;
public sealed class SparklinePreview : Control
{
    public SparklineSnapshot Snapshot { get; private set; } = new([]);
    public SparklineMode Mode { get; private set; }
    public SparklineGeometry Geometry => Snapshot.Geometry(Mode);
    public void Update(SparklineSnapshot snapshot, SparklineMode mode)
    { ArgumentNullException.ThrowIfNull(snapshot); if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode)); Snapshot = snapshot; Mode = mode; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!double.IsFinite(Bounds.Width) || !double.IsFinite(Bounds.Height) || Bounds.Width <= 12 || Bounds.Height <= 12) return;
        var g = Geometry; var w = Bounds.Width - 12; var h = Bounds.Height - 12;
        Point P(double x, double y) => new(6 + x * w, 6 + y * h);
        var pen = new Pen(Brushes.CornflowerBlue, 2);
        context.DrawLine(new Pen(Brushes.Gray, 1), P(0, g.Baseline), P(1, g.Baseline));
        if (g.Points.Count == 0) return;
        if (Mode is SparklineMode.Line or SparklineMode.Area)
        {
            if (Mode == SparklineMode.Area && g.Points.Count > 1)
            {
                var area = new StreamGeometry();
                using (var path = area.Open())
                {
                    path.BeginFigure(P(g.Points[0].X, g.Baseline), true);
                    foreach (var p in g.Points) path.LineTo(P(p.X, p.Y));
                    path.LineTo(P(g.Points[^1].X, g.Baseline)); path.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 100, 149, 237)), null, area);
            }
            for (var i = 1; i < g.Points.Count; i++) context.DrawLine(pen, P(g.Points[i - 1].X, g.Points[i - 1].Y), P(g.Points[i].X, g.Points[i].Y));
            foreach (var p in g.Points) context.DrawEllipse(Brushes.CornflowerBlue, null, P(p.X, p.Y), 3, 3);
        }
        else foreach (var p in g.Points)
        {
            var left = P(p.X - g.BarWidth / 2, Math.Min(g.Baseline, p.Y));
            if (p.Y == g.Baseline) context.DrawLine(pen, P(p.X - g.BarWidth / 2, p.Y), P(p.X + g.BarWidth / 2, p.Y));
            else context.FillRectangle(p.Y < g.Baseline ? Brushes.CornflowerBlue : Brushes.Coral, new Rect(left, new Size(g.BarWidth * w, Math.Abs(p.Y - g.Baseline) * h)));
        }
    }
}
