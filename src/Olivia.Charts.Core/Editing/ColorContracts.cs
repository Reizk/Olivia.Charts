namespace Olivia.Core.Editing;

/// <summary>Eight-bit sRGB channels with straight (unpremultiplied) alpha, in RGBA order.</summary>
public readonly record struct Rgba8(byte R, byte G, byte B, byte A);
