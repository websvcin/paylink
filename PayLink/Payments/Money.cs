using System.Globalization;

namespace PayLink.Payments;

public static class Money
{
    /// <summary>Formats minor units for people: 149900 INR → ₹1,499.00. Indian digit grouping for INR.</summary>
    public static string Format(long minor, string currency)
    {
        var value = minor / 100m;
        return currency.ToUpperInvariant() switch
        {
            "INR" => "₹" + Indian(value),
            "USD" => "$" + value.ToString("#,##0.00", CultureInfo.InvariantCulture),
            "EUR" => "€" + value.ToString("#,##0.00", CultureInfo.InvariantCulture),
            _ => $"{currency.ToUpperInvariant()} {value.ToString("#,##0.00", CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>Indian digit grouping (lakh, crore): 12345678.5 → 1,23,45,678.50. Done by hand because PayLink runs with invariant globalization.</summary>
    private static string Indian(decimal value)
    {
        var sign = value < 0 ? "-" : "";
        var text = Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture);
        var whole = text[..^3];
        var grouped = whole.Length <= 3 ? whole : whole[^3..];
        for (var rest = whole.Length <= 3 ? "" : whole[..^3]; rest.Length > 0; rest = rest.Length <= 2 ? "" : rest[..^2])
            grouped = (rest.Length <= 2 ? rest : rest[^2..]) + "," + grouped;
        return sign + grouped + text[^3..];
    }

    /// <summary>Reads a rupee amount typed by a person ("1,499.50") into paise.</summary>
    public static long? ParseMajor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return decimal.TryParse(text.Replace(",", "").Replace("₹", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0
            ? (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero)
            : null;
    }
}
