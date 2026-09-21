using System.ComponentModel.DataAnnotations;

namespace GopiCrackers.Api.Models;

/// <summary>
/// The event vocabulary. A closed list rather than free text, so the admin
/// report can never be quietly broken by a typo on the storefront — an unknown
/// type is rejected at the door instead of becoming a row nothing counts.
/// </summary>
public static class AnalyticsEvents
{
    public const string PageView = "page_view";
    public const string ProductView = "product_view";
    public const string CategoryView = "category_view";
    public const string ComboView = "combo_view";
    public const string Search = "search";
    public const string CartAdd = "cart_add";
    public const string CartRemove = "cart_remove";
    public const string CheckoutStart = "checkout_start";
    public const string OrderPlaced = "order_placed";
    public const string Enquiry = "enquiry";
    public const string WhatsappClick = "whatsapp_click";

    public static readonly IReadOnlyList<string> All =
    [
        PageView, ProductView, CategoryView, ComboView, Search,
        CartAdd, CartRemove, CheckoutStart, OrderPlaced, Enquiry, WhatsappClick,
    ];

    public static bool IsKnown(string? type) =>
        type is not null && All.Contains(type.Trim().ToLowerInvariant());
}

/// <summary>
/// One thing that happened on the storefront.
///
/// Note what is absent: no IP address, no user agent, no cookie, no name, no
/// phone number. <see cref="Session"/> is a random string the browser keeps for
/// the length of one tab, which is enough to count "baskets started" without
/// being able to identify anybody. That is the whole reason this is self-hosted
/// rather than a third-party tag.
/// </summary>
public sealed record AnalyticsEventRequest
{
    [Required(ErrorMessage = "An event needs a type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>What it happened to — a product slug, a category slug, a route.</summary>
    [StringLength(160)]
    public string? Ref { get; init; }

    /// <summary>Something readable for the report, e.g. the product's name.</summary>
    [StringLength(160)]
    public string? Label { get; init; }

    /// <summary>Rupees for an order, quantity for a basket line, hits for a search.</summary>
    [Range(0, 100_000_000)]
    public int Value { get; init; }

    /// <summary>An anonymous per-tab id. Absent is fine; it just cannot be grouped.</summary>
    [StringLength(64)]
    public string? Session { get; init; }
}

/// <summary>
/// A batch. The storefront queues events and flushes them together, so a busy
/// page costs one request rather than twenty.
/// </summary>
public sealed record AnalyticsBatch
{
    [Required, MinLength(1, ErrorMessage = "Nothing to record")]
    [MaxLength(50, ErrorMessage = "Send at most 50 events at a time")]
    public IReadOnlyList<AnalyticsEventRequest> Events { get; init; } = [];
}

/// <summary>A stored event. <see cref="At"/> is the server's clock, never the browser's.</summary>
public sealed record AnalyticsEvent(
    string Type,
    DateTimeOffset At,
    string? Ref,
    string? Label,
    int Value,
    string? Session);

public sealed record AnalyticsAccepted(int Accepted, IReadOnlyList<string> Rejected);

/* -------------------------------------------------------------------------- */
/* The report                                                                  */
/* -------------------------------------------------------------------------- */

public sealed record CountedRef(string Ref, string Label, int Count, int Value = 0);

public sealed record DailyPoint(DateOnly Day, int Views, int CartAdds, int Orders, int Revenue);

/// <summary>
/// The funnel.
///
/// Every stage counts **visits**, not events — the number of distinct sessions
/// that reached it. That is the only unit the rates below make sense in: three
/// page views from one person is one visit that looked, and dividing event
/// counts by each other answers a question nobody asked.
///
/// A later stage can still exceed an earlier one, and the API does not hide it.
/// Somebody arriving with a basket their browser kept from last week reaches
/// checkout in this window without having added anything in it. That is a real
/// and useful fact about the shop's traffic, so the rate is reported as it
/// falls and the client is left to present anything over 100% for what it is.
/// </summary>
public sealed record Funnel(
    int Sessions,
    int ProductViews,
    int CartAdds,
    int CheckoutStarts,
    int Orders,
    double ViewToCartRate,
    double CartToCheckoutRate,
    double CheckoutToOrderRate,
    /// <summary>Raw event counts, for the tiles that want totals rather than reach.</summary>
    int ProductViewEvents = 0,
    int CartAddEvents = 0);

public sealed record AnalyticsReport(
    int Days,
    DateOnly From,
    DateOnly To,
    int TotalEvents,
    Funnel Funnel,
    IReadOnlyList<CountedRef> TopProducts,
    IReadOnlyList<CountedRef> TopCategories,
    IReadOnlyList<CountedRef> TopSearches,
    IReadOnlyList<CountedRef> MostAddedToCart,
    IReadOnlyList<CountedRef> ByType,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<CountedRef> SearchesWithNoResults);
