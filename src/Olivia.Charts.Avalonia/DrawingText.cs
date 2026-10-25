using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Olivia.Avalonia;

internal static class DrawingText
{
    private static FormattedText Create(string text, IBrush brush, double size, double maxWidth) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    internal static void Draw(DrawingContext context, string text, Point origin, IBrush brush,
        double size = 12, double maxWidth = double.PositiveInfinity) =>
        context.DrawText(Create(text, brush, size, maxWidth), origin);

    /// <summary>Keep the formatter's guaranteed offset visible when a long date needs ellipsis.</summary>
    internal static void DrawTimestamp(DrawingContext context, string text, Point origin, IBrush brush,
        double size, double maxWidth)
    {
        const string fallbackMarker = " [UTC fallback]";
        var end = text.EndsWith(fallbackMarker, StringComparison.Ordinal) ? text.Length - fallbackMarker.Length : text.Length;
        var offset = end - 6;
        // Invalid raw ticks use a diagnostic instead of an offset. Do not reinterpret that diagnostic.
        if (offset < 1 || text[offset - 1] != ' ' || text[offset] is not ('+' or '-') || text[offset + 3] != ':' ||
            !char.IsAsciiDigit(text[offset + 1]) || !char.IsAsciiDigit(text[offset + 2]) ||
            !char.IsAsciiDigit(text[offset + 4]) || !char.IsAsciiDigit(text[offset + 5]))
        {
            Draw(context, text, origin, brush, size, maxWidth);
            return;
        }
        var suffix = Create(text[(offset - 1)..], brush, size, double.PositiveInfinity);
        var prefixWidth = maxWidth - suffix.Width;
        if (prefixWidth <= size)
        {
            // A surface narrower than the suffix cannot show the whole timestamp; full selection detail
            // remains available. Prefer the explicit offset/fallback marker over misleading partial time.
            Draw(context, text[offset..], origin, brush, size, maxWidth);
            return;
        }
        var prefix = Create(text[..(offset - 1)], brush, size, prefixWidth);
        context.DrawText(prefix, origin);
        context.DrawText(suffix, new Point(origin.X + Math.Min(prefix.Width, prefixWidth), origin.Y));
    }
}
