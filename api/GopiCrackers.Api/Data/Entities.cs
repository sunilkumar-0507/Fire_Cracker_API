using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Data;

/*
 * The table rows.
 *
 * These are deliberately not the records in Models. Those are the wire shape:
 * immutable, positional, carrying computed members like "availability" and
 * "resolvedEndsAt" that belong in a response and have no business being
 * columns. These are the storage shape: mutable so EF can track them, flat so
 * a shopkeeper with a MySQL client can read the order book without a decoder.
 *
 * Mapping.cs is the only place the two meet.
 */

/// <summary>A catalogue product. Mirrors <c>products.json</c> row for row.</summary>
public sealed class ProductRow
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public int Price { get; set; }
    public int Mrp { get; set; }
    public int Discount { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Stored as a JSON array in one column rather than a child table.
    ///
    /// These are a product's own prose — its bullet points, its photographs, its
    /// spec sheet. Nothing joins to them, nothing aggregates them and nothing
    /// queries across them; they are read and written whole, with the product.
    /// A product_highlights table would buy a foreign key and cost four more
    /// round trips per save. Order lines, which sales reporting genuinely does
    /// aggregate, get a real child table further down.
    /// </summary>
    public List<string> Highlights { get; set; } = [];
    public List<string> Images { get; set; } = [];
    public int Stock { get; set; }
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, string> Specs { get; set; } = [];

    public bool Featured { get; set; }
    public bool BestSeller { get; set; }
    public bool Combo { get; set; }
    public bool IsNew { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class CategoryRow
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TamilName { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Art { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string AccentSoft { get; set; } = string.Empty;

    /// <summary>
    /// Recomputed from the products on every snapshot build, exactly as it is
    /// when the catalogue comes from files. Stored anyway so the table reads
    /// correctly to anything else that opens it, never trusted on load.
    /// </summary>
    public int ProductCount { get; set; }

    public string NoiseLevel { get; set; } = string.Empty;
    public bool Featured { get; set; }
    public string Tone { get; set; } = string.Empty;
}

public sealed class ComboRow
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Art { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public int Price { get; set; }
    public int Mrp { get; set; }
    public int Discount { get; set; }
    public int Saves { get; set; }
    public int ItemCount { get; set; }
    public string Serves { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public int Stock { get; set; }
    public bool Featured { get; set; }
    public List<ComboItem> Includes { get; set; } = [];
    public string Tone { get; set; } = string.Empty;
}

public sealed class OfferRow
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Value { get; set; }
    public int MinOrder { get; set; }
    public string Art { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string? AccentTo { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int FallbackHours { get; set; }
    public string Badge { get; set; } = string.Empty;
    public bool Featured { get; set; }
    public List<string> Terms { get; set; } = [];
    public string Tone { get; set; } = string.Empty;
}

public sealed class BannerRow
{
    public string Id { get; set; } = string.Empty;
    public string Placement { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Eyebrow { get; set; }
    public string? TitleAccent { get; set; }
    public string? Subtitle { get; set; }

    public string? CtaPrimaryLabel { get; set; }
    public string? CtaPrimaryTo { get; set; }
    public string? CtaSecondaryLabel { get; set; }
    public string? CtaSecondaryTo { get; set; }

    public string Art { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string? AccentTo { get; set; }
}

public sealed class TestimonialRow
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Initials { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string Quote { get; set; } = string.Empty;
}

public sealed class FaqRow
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}

/* -------------------------------------------------------------------------- */
/* Commerce                                                                    */
/* -------------------------------------------------------------------------- */

/// <summary>
/// One order.
///
/// The money is flattened into columns rather than kept as a JSON blob: "what
/// did the shop take last week" is the question the order book exists to
/// answer, and it should be a SUM, not a deserialisation.
/// </summary>
public sealed class OrderRow
{
    public string OrderId { get; set; } = string.Empty;
    public DateTimeOffset PlacedAt { get; set; }
    public string Status { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string Pincode { get; set; } = string.Empty;

    public string Payment { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = Models.PaymentStatus.Pending;
    public string? PaymentReference { get; set; }
    public string? Notes { get; set; }

    public int Subtotal { get; set; }
    public int MrpTotal { get; set; }
    public int CatalogueSavings { get; set; }
    public int CouponDiscount { get; set; }
    public int Shipping { get; set; }
    public int Total { get; set; }
    public int TotalSavings { get; set; }
    public int FreeShippingGap { get; set; }
    public int ItemCount { get; set; }

    /// <summary>Null when no coupon was applied — the usual case.</summary>
    public string? CouponCode { get; set; }
    public string? CouponType { get; set; }
    public int? CouponValue { get; set; }
    public int? CouponMinOrder { get; set; }
    public string? CouponNote { get; set; }

    public DateOnly DeliveryFrom { get; set; }
    public DateOnly DeliveryTo { get; set; }
    public string Fulfilment { get; set; } = Models.Fulfilment.Delivery;

    public List<OrderItemRow> Items { get; set; } = [];
    public List<OrderEventRow> History { get; set; } = [];
}

/// <summary>
/// One line of one order, priced as it was on the day.
///
/// Every field is a copy rather than a foreign key to the product on purpose: a
/// price list that changes in November must not retroactively rewrite what
/// October's customers were charged. The product id is kept so sales can still
/// be grouped by product — which is the one thing a child table buys that a
/// JSON column would not.
/// </summary>
public sealed class OrderItemRow
{
    public long Key { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public OrderRow? Order { get; set; }

    /// <summary>Position in the basket, so a reloaded order lists in the same order.</summary>
    public int LineNo { get; set; }

    public string ItemId { get; set; } = string.Empty;
    public string Kind { get; set; } = "product";
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public int Price { get; set; }
    public int Mrp { get; set; }
    public string? Image { get; set; }
    public string Category { get; set; } = string.Empty;
    public int Stock { get; set; }
    public List<string> Tags { get; set; } = [];
    public int Qty { get; set; }
    public int LineTotal { get; set; }
    public bool Capped { get; set; }
    public string Availability { get; set; } = Models.Availability.Available;
}

/// <summary>One step in an order's life — what the tracking page draws.</summary>
public sealed class OrderEventRow
{
    public long Key { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public OrderRow? Order { get; set; }

    public int Seq { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }
}

public sealed class EnquiryRow
{
    public string EnquiryId { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public string Status { get; set; } = "received";
    public string Name { get; set; } = string.Empty;
    public string? Organisation { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string District { get; set; } = string.Empty;
    public string? Budget { get; set; }
    public string? Quantity { get; set; }
    public string? Message { get; set; }
}

public sealed class ContactMessageRow
{
    public string MessageId { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Subject { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>The newsletter list. The address is the key — subscribing twice is one row.</summary>
public sealed class SubscriberRow
{
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset SubscribedAt { get; set; }
}

public sealed class StockIntakeRow
{
    public string Id { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? Supplier { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// One storefront event. Carries no IP address, user agent, cookie or name —
/// see <see cref="AnalyticsEventRequest"/> for why that list is the point.
/// </summary>
public sealed class AnalyticsEventRow
{
    public long Key { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string? Ref { get; set; }
    public string? Label { get; set; }
    public int Value { get; set; }
    public string? Session { get; set; }
}
