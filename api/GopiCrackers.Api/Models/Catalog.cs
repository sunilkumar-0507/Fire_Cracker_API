namespace GopiCrackers.Api.Models;

/// <summary>
/// Catalogue entities. These mirror <c>src/data/*.json</c> field for field — the
/// storefront already consumes these exact shapes, so a component swapping
/// <c>src/data/index.js</c> for <c>fetch()</c> needs no other change.
/// </summary>
public sealed record Product(
    string Id,
    string Code,
    string Slug,
    string Name,
    string Category,
    string Brand,
    int Price,
    int Mrp,
    int Discount,
    string Unit,
    string Description,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<string> Images,
    int Stock,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Specs,
    bool Featured,
    bool BestSeller,
    bool Combo,
    bool IsNew,
    /// <summary>
    /// The shop's own switch. False is "temporarily unavailable" — the product
    /// stays in the catalogue, keeps its page and its history, but cannot be
    /// bought. Deactivating is what a shopkeeper wants when a line is between
    /// batches; deleting is for a line that is gone for good.
    ///
    /// Optional in the JSON so a catalogue written before this existed loads as
    /// active rather than as a shop with nothing for sale.
    /// </summary>
    bool Active = true)
{
    /// <summary>
    /// The three states requirement 3 asks for, derived rather than stored so a
    /// badge can never disagree with the stock figure beside it. Served, never
    /// persisted — <see cref="Services.PersistenceJson"/> drops it.
    /// </summary>
    public string Availability =>
        !Active ? Models.Availability.Unavailable
        : Stock <= 0 ? Models.Availability.OutOfStock
        : Models.Availability.Available;

    /// <summary>True when a customer can actually add this to a basket.</summary>
    public bool Purchasable => Active && Stock > 0;
}

/// <summary>
/// The availability vocabulary, in one place so the API, the storefront and the
/// admin cannot drift into three different spellings of "out of stock".
/// </summary>
public static class Availability
{
    public const string Available = "available";
    public const string OutOfStock = "out-of-stock";
    public const string Unavailable = "unavailable";

    public static readonly IReadOnlyList<string> All = [Available, OutOfStock, Unavailable];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim().ToLowerInvariant());
}

public sealed record Category(
    string Id,
    string Slug,
    string Name,
    string TamilName,
    string Tagline,
    string Description,
    string Art,
    string Accent,
    string AccentSoft,
    int ProductCount,
    string NoiseLevel,
    bool Featured,
    string Tone);

public sealed record ComboItem(string Name, int Qty, string? Slug = null);

public sealed record Combo(
    string Id,
    string Slug,
    string Name,
    string Tagline,
    string Description,
    string Art,
    string Accent,
    int Price,
    int Mrp,
    int Discount,
    int Saves,
    int ItemCount,
    string Serves,
    string Duration,
    string Badge,
    int Stock,
    bool Featured,
    IReadOnlyList<ComboItem> Includes,
    string Tone);

public sealed record Offer(
    string Id,
    string Code,
    string Title,
    string Subtitle,
    string Description,
    string Type,
    int Value,
    int MinOrder,
    string Art,
    string Accent,
    string? AccentTo,
    DateTimeOffset EndsAt,
    int FallbackHours,
    string Badge,
    bool Featured,
    IReadOnlyList<string> Terms,
    string Tone)
{
    /// <summary>
    /// An offer carries a fixed <c>endsAt</c>, but a countdown that has already
    /// died looks broken. Once the fixed date passes this rolls forward by
    /// <c>fallbackHours</c> — a port of <c>resolveDeadline</c> in format.js.
    ///
    /// Served, but never persisted — `PersistenceJson` drops it when offers.json
    /// is written back, so a value computed from "now" is not frozen into a
    /// source file.
    /// </summary>
    public DateTimeOffset ResolvedEndsAt =>
        EndsAt > DateTimeOffset.UtcNow
            ? EndsAt
            : DateTimeOffset.UtcNow.AddHours(FallbackHours);

    public bool Expired => EndsAt <= DateTimeOffset.UtcNow;
}

public sealed record BannerCta(string Label, string To);

public sealed record Banner(
    string Id,
    string Placement,
    string Title,
    string? Eyebrow,
    string? TitleAccent,
    string? Subtitle,
    BannerCta? CtaPrimary,
    BannerCta? CtaSecondary,
    string Art,
    string Accent,
    string? AccentTo);

public sealed record Testimonial(
    string Id,
    string Name,
    string Role,
    string Location,
    string Initials,
    string Accent,
    int Rating,
    string Quote);

public sealed record Faq(
    string Id,
    string Category,
    string Question,
    string Answer);
