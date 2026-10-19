using System.Globalization;

namespace PayLink.Infrastructure;

/// <summary>Times are stored as ISO-8601 UTC strings so they sort and compare the same way on SQLite and MySQL.</summary>
public static class Clock
{
    public static string Now() => Format(DateTime.UtcNow);

    public static string Format(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTime? Parse(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>"19 Oct, 18:02" for compact lists.</summary>
    public static string Short(string? value) => Parse(value) is { } d ? d.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture) : "—";

    /// <summary>"19 Oct 2026, 18:02 UTC" for display.</summary>
    public static string Display(string? value) => Parse(value) is { } d ? d.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture) : "—";
}
