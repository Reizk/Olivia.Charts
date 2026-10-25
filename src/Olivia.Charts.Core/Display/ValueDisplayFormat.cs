using System.Globalization;

namespace Olivia.Core.Display;

/// <summary>
/// Immutable, presentation-only timezone, calendar and numeric formatting. Source UTC ticks, keys,
/// ordering and values are never converted or rounded. No current-thread or process culture is changed.
/// </summary>
/// <remarks>
/// Each time label has a separate invariant numeric offset, including when the requested format already
/// contains one. UTC-reinterpreting standard formats R/r/u/U are rejected. When the requested timezone
/// would overflow DateTime, or its local date is outside the culture's calendar, an explicit invariant
/// UTC fallback preserves the complete original instant instead of clamping it or throwing while drawing.
/// Formats are limited to 128 characters; standard numeric precision is limited to 99.
/// </remarks>
public sealed class ValueDisplayFormat
{
    private const int MaximumConfigurationLength = 128;
    private const string DefaultTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string DefaultNumberFormat = "0.###";
    private readonly TimeZoneInfo _timeZone;
    private readonly CultureInfo _culture;

    public static ValueDisplayFormat Default { get; } = new();

    public ValueDisplayFormat(string timeZoneId = "UTC", string cultureName = "en-US",
        string timeFormat = DefaultTimeFormat, string numberFormat = DefaultNumberFormat)
    {
        ValidateText(timeZoneId, nameof(timeZoneId));
        ValidateText(cultureName, nameof(cultureName), allowEmpty: true);
        ValidateText(timeFormat, nameof(timeFormat));
        ValidateText(numberFormat, nameof(numberFormat));

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("The display timezone must be an installed timezone ID.", nameof(timeZoneId), error);
        }

        CultureInfo culture;
        try
        {
            // ICU can manufacture cultures for unknown-but-well-formed language tags. Only installed
            // cultures, registered LCID aliases (for example zh-CN / zh-Hans-CN), and invariant are accepted.
            culture = new CultureInfo(cultureName, useUserOverride: false);
            if (culture.LCID == 0x1000 && !CultureInfo.GetCultures(CultureTypes.AllCultures)
                .Any(candidate => string.Equals(candidate.Name, culture.Name, StringComparison.OrdinalIgnoreCase)))
                throw new CultureNotFoundException(nameof(cultureName), cultureName, "The culture is not installed.");
            culture = CultureInfo.ReadOnly(culture);
        }
        catch (CultureNotFoundException error)
        {
            throw new ArgumentException("The display culture must be an installed culture name, or empty for invariant.", nameof(cultureName), error);
        }

        ValidateTimeFormat(timeFormat, culture);
        ValidateNumberFormat(numberFormat, culture);

        // Publish configuration only after every part has been checked. A rejected replacement cannot
        // leave a partially changed formatter in a view; the existing immutable instance stays valid.
        _timeZone = timeZone;
        _culture = culture;
        TimeZoneId = timeZone.Id;
        CultureName = culture.Name;
        TimeFormat = timeFormat;
        NumberFormat = numberFormat;
    }

    public string TimeZoneId { get; }
    public string CultureName { get; }
    public string TimeFormat { get; }
    public string NumberFormat { get; }

    /// <summary>
    /// Formats UTC tick values, returning an explicit diagnostic for ticks outside the DateTime domain.
    /// Offsets distinguish repeated wall-clock times at DST
    /// fall-back; spring-forward never invents a nonexistent local time. Raw ticks remain unchanged.
    /// </summary>
    public string FormatTimestamp(long utcTicks)
    {
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            return "[invalid UTC ticks: " + utcTicks.ToString(CultureInfo.InvariantCulture) + "]";

        var utc = new DateTime(utcTicks, DateTimeKind.Utc);
        var offset = _timeZone.GetUtcOffset(utc);
        // DateTime ticks plus any supported timezone offset cannot overflow Int64.
        var localTicks = utcTicks + offset.Ticks;
        var calendar = _culture.DateTimeFormat.Calendar;
        if (localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks ||
            localTicks < calendar.MinSupportedDateTime.Ticks || localTicks > calendar.MaxSupportedDateTime.Ticks)
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + " +00:00 [UTC fallback]";

        var local = new DateTimeOffset(localTicks, offset);
        return local.ToString(TimeFormat, _culture) + " " + local.ToString("zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats finite values independently of time formatting; null, NaN and infinities are gaps.</summary>
    public string FormatNumber(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString(NumberFormat, _culture)
        : "— gap";

    private static void ValidateText(string text, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(text, parameterName);
        if (text.Length > MaximumConfigurationLength || text.Any(char.IsControl) ||
            (!allowEmpty && string.IsNullOrWhiteSpace(text)) ||
            (allowEmpty && text.Length > 0 && string.IsNullOrWhiteSpace(text)))
            throw new ArgumentException($"Use {(allowEmpty ? "0" : "1")}–{MaximumConfigurationLength} characters without control characters.", parameterName);
    }

    private static void ValidateTimeFormat(string format, CultureInfo culture)
    {
        if (format is "R" or "r" or "u" or "U")
            throw new ArgumentException("UTC-reinterpreting time standards are not supported; choose an explicit timezone and a local-time format.", "timeFormat");
        try
        {
            // The calendar boundary is guaranteed to be representable even for restricted calendars.
            var sample = new DateTimeOffset(culture.DateTimeFormat.Calendar.MinSupportedDateTime.Ticks, TimeSpan.Zero);
            _ = sample.ToString(format, culture);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("The display time format is invalid.", "timeFormat", error);
        }
    }

    private static void ValidateNumberFormat(string format, CultureInfo culture)
    {
        // Standard double formats may otherwise request hundreds of millions of digits. Integer-only
        // standards (D/X/B) and unknown standards are deliberately excluded before any formatting occurs.
        var looksStandard = char.IsAsciiLetter(format[0]) && format.Skip(1).All(char.IsAsciiDigit);
        if (looksStandard)
        {
            var specifier = char.ToUpperInvariant(format[0]);
            if (specifier is not ('C' or 'E' or 'F' or 'G' or 'N' or 'P' or 'R') ||
                format.Length > 3 || (specifier == 'R' && format.Length != 1))
                throw new ArgumentException("Use C/E/F/G/N/P with at most two precision digits, R alone, or a custom numeric format.", "numberFormat");
        }
        else
        {
            ValidateCustomNumberFormat(format);
        }

        try
        {
            // Exercise positive, negative, zero and both finite extremes, including custom format sections.
            foreach (var sample in new[] { 1234.567, -1234.567, 0d, double.MaxValue, double.MinValue, double.Epsilon })
                _ = sample.ToString(format, culture);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("The display number format is invalid.", "numberFormat", error);
        }
    }

    private static void ValidateCustomNumberFormat(string format)
    {
        var quote = '\0';
        var sections = 1;
        var hasDigitPlaceholder = false;
        for (var index = 0; index < format.Length; index++)
        {
            var character = format[index];
            if (character == '\\')
            {
                if (++index == format.Length)
                    throw new ArgumentException("A numeric escape must have a following character.", "numberFormat");
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') quote = character;
            else if (character is '0' or '#') hasDigitPlaceholder = true;
            else if (character == ';' && ++sections > 3)
                throw new ArgumentException("A numeric format supports at most three sections.", "numberFormat");
        }
        if (quote != '\0' || !hasDigitPlaceholder)
            throw new ArgumentException("Use balanced quotes and at least one unquoted numeric digit placeholder.", "numberFormat");
    }
}
