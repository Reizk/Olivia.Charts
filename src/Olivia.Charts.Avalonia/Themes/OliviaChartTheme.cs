using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Olivia.Avalonia.Themes;

/// <summary>
/// Immutable, opt-in chart paint tokens. Unlike UiPalette (UI chrome), SeriesColors is an
/// ordered data-series palette. This object does not recolor documents, change source data,
/// choose gradient-stop positions, create a material, or attach itself to a chart.
/// </summary>
public sealed class OliviaChartTheme
{
    public const int MinimumSeriesColors = 3;

    /// <summary>
    /// Copies the ordered colors; callers cannot change a published snapshot by mutating their
    /// original list. At least three colors are required for the pending three-stop consumer.
    /// </summary>
    public OliviaChartTheme(IReadOnlyList<Color> seriesColors, Color domain3DColor)
    {
        ArgumentNullException.ThrowIfNull(seriesColors);
        if (seriesColors.Count < MinimumSeriesColors)
            throw new ArgumentException("A chart palette requires at least three colors.", nameof(seriesColors));
        var copy = new Color[seriesColors.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = seriesColors[i];
        SeriesColors = Array.AsReadOnly(copy);
        Domain3DBrush = new ImmutableSolidColorBrush(domain3DColor);
    }

    /// <summary>
    /// Stable zero-based palette order, never derived from the current visible series. Consumers
    /// own series-to-index mapping and any cycling; out-of-range indices are not silently wrapped.
    /// </summary>
    public IReadOnlyList<Color> SeriesColors { get; }

    /// <summary>Candidate 3D domain paint, not a WPF material or an automatically applied background.</summary>
    public ImmutableSolidColorBrush Domain3DBrush { get; }

    /// <summary>
    /// Default chart-domain alias to Domain3DBrush, reflecting the documented 3D resource mapping.
    /// This name does not introduce a separate 2D-background policy. A host chooses which resource
    /// key to consume and whether to override it when integrating its own renderer.
    /// </summary>
    public ImmutableSolidColorBrush DomainBrush => Domain3DBrush;

    // Series remain candidate choices. Domain colors resolve the official lightweight
    // Charts.Domain3DBrush -> Color.Editor.Background resource for each theme.
    internal static OliviaChartTheme CreateCandidate(OliviaThemeId id) => id switch
    {
        OliviaThemeId.Office2019Colorful => new(
            Colors("#4472C4", "#ED7D31", "#70AD47", "#A855A1", "#5B9BD5", "#C28E00"), Color.Parse("#FFFFFF")),
        OliviaThemeId.VS2019Dark => new(
            Colors("#569CD6", "#D7BA7D", "#4EC9B0", "#C586C0", "#CE9178", "#9CDCFE"), Color.Parse("#333337")),
        OliviaThemeId.Win11Light => new(
            Colors("#0067C0", "#C75A14", "#0F7B6C", "#744DA9", "#C43E6D", "#497A00"), Color.Parse("#FFFFFF")),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown chart theme candidate.")
    };

    private static Color[] Colors(params string[] colors) => Array.ConvertAll(colors, Color.Parse);
}
