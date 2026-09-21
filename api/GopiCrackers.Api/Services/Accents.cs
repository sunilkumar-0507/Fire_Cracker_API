namespace GopiCrackers.Api.Services;

/// <summary>
/// The five accent tones, mirrored from <c>src/constants/accents.js</c>.
///
/// A category or offer carries a tone name; the storefront paints its icon tile,
/// subtitle and CTA from the matching entry there. The hex pair is written into
/// the JSON alongside it for anything reading the files directly, but the tone
/// is what the UI actually keys off — so the admin picks a tone and the two hex
/// values are derived here rather than typed in and left to drift.
/// </summary>
public static class Accents
{
    private static readonly Dictionary<string, (string Hex, string Soft)> Palette =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["amber"] = ("#FFB238", "#FFF1D6"),
            ["ember"] = ("#E67A00", "#FFE7C6"),
            ["mint"] = ("#22C9AE", "#E4FBF6"),
            ["berry"] = ("#F5567F", "#FFEAF1"),
            ["royal"] = ("#C273F5", "#F6EAFF"),
        };

    public static IReadOnlyCollection<string> Keys => Palette.Keys;

    /// <summary>Falls back to amber, the same default as <c>accentOf</c>.</summary>
    public static string Normalise(string? tone) =>
        tone is not null && Palette.ContainsKey(tone) ? tone.ToLowerInvariant() : "amber";

    public static string Hex(string? tone) => Palette[Normalise(tone)].Hex;

    public static string Soft(string? tone) => Palette[Normalise(tone)].Soft;
}
