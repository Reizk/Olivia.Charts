using Avalonia.Media;

namespace Olivia.Avalonia;

/// <summary>Small original semantic palette. Resources are owned by the adapter.</summary>
public sealed record UiPalette(IBrush Surface, IBrush Text, IBrush Muted, IBrush GridLine,
    IBrush Accent, IBrush Selection, IBrush Error)
{
    public static UiPalette Dark { get; } = new(Brush("#14202E"), Brush("#EBF2F7"), Brush("#9DB0C4"),
        Brush("#2D4155"), Brush("#61D5C3"), Brush("#234D66"), Brush("#FFBE98"));
    public static UiPalette Light { get; } = new(Brush("#FFFFFF"), Brush("#132C40"), Brush("#526C80"),
        Brush("#D4E1EA"), Brush("#006F68"), Brush("#DAEDF7"), Brush("#9C3C13"));
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
