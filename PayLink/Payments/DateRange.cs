using System.Globalization;
using PayLink.Infrastructure;

namespace PayLink.Payments;

/// <summary>
/// A range of whole days (UTC, both ends included) chosen in the console: a preset ("today", "7", "30", "90", "month", "lastmonth",
/// "year") or a custom from/to. <see cref="Start"/> and <see cref="End"/> are the stored-time bounds for SQL (End is exclusive).
/// </summary>
public sealed record DateRange(DateOnly From, DateOnly To, string Preset)
{
    public static readonly (string Key, string Label)[] Presets =
        [("today", "Today"), ("7", "7 days"), ("30", "30 days"), ("90", "90 days"), ("month", "This month"), ("lastmonth", "Last month"), ("year", "This year")];

    public string Start => Clock.Format(From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    public string End => Clock.Format(To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    public int Days => To.DayNumber - From.DayNumber + 1;
    public string Label => Preset == "custom" ? $"{From:d MMM yyyy} to {To:d MMM yyyy}" : Presets.First(p => p.Key == Preset).Label.ToLowerInvariant();
    public string Query => Preset == "custom" ? $"from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}" : $"range={Preset}";

    /// <summary>Reads the console's range parameters; anything unreadable falls back to <paramref name="fallback"/>. Ranges are capped at <paramref name="maxDays"/>.</summary>
    public static DateRange Parse(string? range, string? from, string? to, string fallback = "30", int maxDays = 3660)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f))
        {
            var t = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : today;
            if (t < f) (f, t) = (t, f);
            if (t.DayNumber - f.DayNumber + 1 > maxDays) f = t.AddDays(-(maxDays - 1));
            return new DateRange(f, t, "custom");
        }
        var key = Presets.Any(p => p.Key == range) ? range! : fallback;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return key switch
        {
            "today" => new(today, today, key),
            "7" => new(today.AddDays(-6), today, key),
            "90" => new(today.AddDays(-89), today, key),
            "month" => new(monthStart, today, key),
            "lastmonth" => new(monthStart.AddMonths(-1), monthStart.AddDays(-1), key),
            "year" => new(new DateOnly(today.Year, 1, 1), today, key),
            _ => new(today.AddDays(-29), today, "30"),
        };
    }
}

/// <summary>What the payments list (and its CSV) is narrowed to. Every part is optional.</summary>
public sealed record PaymentFilter(string? Status = null, string? Q = null, string? Connector = null, DateRange? Dates = null,
    long? MinAmount = null, long? MaxAmount = null, string? Reference = null, string? LinkId = null);
