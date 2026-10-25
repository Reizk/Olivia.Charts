using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Olivia.Avalonia.Themes;

/// <summary>Candidate visual directions, not certified DevExpress theme implementations.</summary>
public enum OliviaThemeId { Office2019Colorful, VS2019Dark, Win11Light }

/// <summary>UI chrome only. Document fills, exported content and chart series are not UI color roles.</summary>
public sealed record OliviaThemeColors(
    Color Canvas, Color Surface, Color Elevated, Color Text, Color Muted, Color Border,
    Color Accent, Color OnAccent, Color Hover, Color Pressed, Color Selection,
    Color DisabledSurface, Color DisabledText, Color Error, Color Focus);

/// <summary>Immutable tokens shared by standard-control resources and opt-in custom drawing.</summary>
public sealed class OliviaThemeDefinition
{
    private OliviaThemeDefinition(OliviaThemeId id, ThemeVariant variant, OliviaThemeColors colors,
        double radius, double controlHeight, double spacing, Thickness padding)
    {
        Id = id; Variant = variant; Colors = colors;
        CornerRadius = new CornerRadius(radius); ControlHeight = controlHeight;
        Spacing = spacing; ControlPadding = padding;
        Chart = OliviaChartTheme.CreateCandidate(id);
        Palette = new UiPalette(Brush(colors.Surface), Brush(colors.Text), Brush(colors.Muted),
            Brush(colors.Border), Brush(colors.Accent), Brush(colors.Selection), Brush(colors.Error));
    }

    public OliviaThemeId Id { get; }
    public string DisplayName => $"{Id} · candidate";
    public ThemeVariant Variant { get; }
    public OliviaThemeColors Colors { get; }
    public CornerRadius CornerRadius { get; }
    public double ControlHeight { get; }
    public double Spacing { get; }
    public double FontSize => 14;
    public Thickness ControlPadding { get; }
    /// <summary>Existing UI chrome palette. This is not the ordered chart-series palette.</summary>
    public UiPalette Palette { get; }
    /// <summary>Separate opt-in chart paints; existing plot defaults are not changed.</summary>
    public OliviaChartTheme Chart { get; }
    public override string ToString() => DisplayName;
    internal static IBrush Brush(Color color) => new ImmutableSolidColorBrush(color);
    private static Color C(string value) => Color.Parse(value);

    // These are original compatibility choices for the review page, not copied WPF templates.
    // Official Windows reference capture and calibration remain a separate acceptance step.
    public static OliviaThemeDefinition Office2019Colorful { get; } = new(
        OliviaThemeId.Office2019Colorful, ThemeVariant.Light,
        new(C("#F3F3F3"), C("#FFFFFF"), C("#FFFFFF"), C("#252525"), C("#606060"), C("#AFAFAF"),
            C("#0078D4"), C("#FFFFFF"), C("#E5F1FB"), C("#C7E0F4"), C("#CDE6F7"),
            C("#ECECEC"), C("#707070"), C("#B42318"), C("#005A9E")),
        0, 30, 8, new Thickness(10, 5));

    public static OliviaThemeDefinition VS2019Dark { get; } = new(
        OliviaThemeId.VS2019Dark, ThemeVariant.Dark,
        new(C("#1E1E1E"), C("#252526"), C("#2D2D30"), C("#F1F1F1"), C("#B7B7B7"), C("#686868"),
            C("#007ACC"), C("#FFFFFF"), C("#3E3E42"), C("#505055"), C("#264F78"),
            C("#2D2D30"), C("#929292"), C("#FF9B9B"), C("#75BEFF")),
        0, 28, 6, new Thickness(8, 4));

    public static OliviaThemeDefinition Win11Light { get; } = new(
        OliviaThemeId.Win11Light, ThemeVariant.Light,
        new(C("#F3F3F3"), C("#FFFFFF"), C("#FAFAFA"), C("#1A1A1A"), C("#606060"), C("#A0A0A0"),
            C("#0067C0"), C("#FFFFFF"), C("#EAF2FA"), C("#D8E8F7"), C("#DCEBFA"),
            C("#F0F0F0"), C("#747474"), C("#B42318"), C("#005FB8")),
        4, 34, 10, new Thickness(12, 7));

    public static IReadOnlyList<OliviaThemeDefinition> Candidates { get; } =
        Array.AsReadOnly(new[] { Office2019Colorful, VS2019Dark, Win11Light });

    public static OliviaThemeDefinition FromId(OliviaThemeId id) => id switch
    {
        OliviaThemeId.Office2019Colorful => Office2019Colorful,
        OliviaThemeId.VS2019Dark => VS2019Dark,
        OliviaThemeId.Win11Light => Win11Light,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Olivia theme.")
    };
}
