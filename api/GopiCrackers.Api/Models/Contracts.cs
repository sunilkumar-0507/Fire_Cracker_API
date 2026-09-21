using System.ComponentModel.DataAnnotations;

namespace GopiCrackers.Api.Models;

/* -------------------------------------------------------------------------- */
/* Envelopes                                                                   */
/* -------------------------------------------------------------------------- */

/// <summary>A page of results plus the counts the catalogue UI needs.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total,
    int TotalPages)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static PagedResult<T> From(IReadOnlyList<T> all, int page, int pageSize)
    {
        var total = all.Count;
        var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResult<T>(items, page, pageSize, total, totalPages);
    }
}

public sealed record TagCount(string Tag, int Count);

public sealed record PriceBounds(int Min, int Max);

public sealed record SortOption(string Value, string Label);

/// <summary>Combined product + category hits, for the search overlay.</summary>
public sealed record SearchResult(
    string Query,
    IReadOnlyList<Product> Products,
    IReadOnlyList<Category> Categories,
    int ProductTotal);

/* -------------------------------------------------------------------------- */
/* Cart & pricing                                                              */
/* -------------------------------------------------------------------------- */

/// <summary>
/// One requested line. The client sends ids and quantities only — every price
/// used in the quote is re-read from the catalogue, so a tampered basket cannot
/// change what an order costs.
/// </summary>
public sealed record CartLineRequest
{
    [Required(ErrorMessage = "Each line needs an id")]
    public string Id { get; init; } = string.Empty;

    [Range(1, 999, ErrorMessage = "Quantity must be between 1 and 999")]
    public int Qty { get; init; } = 1;
}

public sealed record CartQuoteRequest
{
    [Required, MinLength(1, ErrorMessage = "The cart is empty")]
    public IReadOnlyList<CartLineRequest> Items { get; init; } = [];

    /// <summary>Optional coupon code. An invalid one is reported, not rejected.</summary>
    public string? Coupon { get; init; }

    /// <summary>
    /// <c>delivery</c> or <c>pickup</c>. Quoting the basket before the customer
    /// has chosen assumes delivery, which is the figure that can only go down.
    /// </summary>
    public string Fulfilment { get; init; } = "delivery";
}

/// <summary>A priced line, denormalised exactly like the storefront cart store.</summary>
public sealed record CartLine(
    string Id,
    string Kind,
    string Slug,
    string Name,
    string Unit,
    int Price,
    int Mrp,
    string? Image,
    string Category,
    int Stock,
    IReadOnlyList<string> Tags,
    int Qty,
    int LineTotal,
    bool Capped,
    /// <summary>
    /// <c>available</c>, <c>out-of-stock</c> or <c>unavailable</c>, copied from
    /// the product at the moment it was priced. Optional so lines journalled
    /// before availability existed load as available, which is what they were.
    /// </summary>
    string Availability = Models.Availability.Available);

/// <summary>Money breakdown — a port of <c>selectTotals</c> in cartStore.js.</summary>
public sealed record CartTotals(
    int Subtotal,
    int MrpTotal,
    int CatalogueSavings,
    int CouponDiscount,
    int Shipping,
    int Total,
    int TotalSavings,
    int FreeShippingGap,
    int Count);

public sealed record CartQuote(
    IReadOnlyList<CartLine> Items,
    CartTotals Totals,
    AppliedCoupon? Coupon,
    IReadOnlyList<string> Notices);

public sealed record AppliedCoupon(string Code, string Type, int Value, int MinOrder, string Note);

public sealed record CouponValidationRequest
{
    [Required(ErrorMessage = "A coupon code is required")]
    public string Code { get; init; } = string.Empty;

    [Range(0, int.MaxValue, ErrorMessage = "Subtotal cannot be negative")]
    public int Subtotal { get; init; }
}

public sealed record CouponValidationResult(
    bool Ok,
    string Message,
    AppliedCoupon? Coupon = null,
    int Discount = 0);

/* -------------------------------------------------------------------------- */
/* Orders & enquiries                                                          */
/* -------------------------------------------------------------------------- */

/// <summary>
/// How an order is handed over. Pickup means the customer collects from the
/// Sivakasi counter, which is why the address block stops being required and
/// the delivery fee stops being charged.
/// </summary>
public static class Fulfilment
{
    public const string Delivery = "delivery";
    public const string Pickup = "pickup";

    public static readonly IReadOnlyList<string> All = [Delivery, Pickup];

    public static string Normalise(string? value) => IsPickup(value) ? Pickup : Delivery;

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim().ToLowerInvariant());

    public static bool IsPickup(string? value) =>
        string.Equals(value?.Trim(), Pickup, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where an order stands with the money, which is not the same question as
/// where it stands with the goods.
///
/// Cash on delivery is <see cref="Pending"/> until the driver is paid, so an
/// order can be <c>completed</c> and still unpaid. Keeping the two apart is
/// what lets the order book answer "what is owed" without guessing.
/// </summary>
public static class PaymentStatus
{
    /// <summary>Nothing collected yet — every cash-on-delivery order starts here.</summary>
    public const string Pending = "pending";

    /// <summary>Handed off to the gateway; waiting on its word.</summary>
    public const string Processing = "processing";

    public const string Paid = "paid";
    public const string Failed = "failed";
    public const string Refunded = "refunded";

    public static readonly IReadOnlyList<string> All =
        [Pending, Processing, Paid, Failed, Refunded];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim().ToLowerInvariant());
}

/// <summary>
/// A checkout submission.
///
/// The address block is validated conditionally: a delivery needs somewhere to
/// go, a pickup does not. That rule lives in <see cref="Validate"/> rather than
/// in attributes, because an attribute cannot see the sibling field it depends on.
/// </summary>
public sealed record OrderRequest : IValidatableObject
{
    [Required(ErrorMessage = "What should we call you?")]
    [StringLength(80, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required(ErrorMessage = "A 10-digit mobile number, please")]
    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "A 10-digit Indian mobile number, please")]
    public string Phone { get; init; } = string.Empty;

    [EmailAddress(ErrorMessage = "That email does not look right")]
    public string? Email { get; init; }

    /// <summary><c>delivery</c> or <c>pickup</c>.</summary>
    public string Fulfilment { get; init; } = Models.Fulfilment.Delivery;

    /* The four address fields carry no [Required]: a pickup order legitimately
       leaves them blank. The length and shape rules below still apply whenever a
       value is present, because every attribute except [Required] passes a null
       straight through. */

    [StringLength(240, MinimumLength = 8,
        ErrorMessage = "Door number, street and area - couriers need all three")]
    public string? Address { get; init; }

    [StringLength(80)]
    public string? City { get; init; }

    public string? District { get; init; }

    [RegularExpression(@"^\d{6}$", ErrorMessage = "A 6-digit pincode, please")]
    public string? Pincode { get; init; }

    [Required(ErrorMessage = "Choose a payment method")]
    public string Payment { get; init; } = string.Empty;

    public string? Notes { get; init; }

    [Required, MinLength(1, ErrorMessage = "The cart is empty")]
    public IReadOnlyList<CartLineRequest> Items { get; init; } = [];

    public string? Coupon { get; init; }

    /// <summary>
    /// The browser's anonymous analytics session, so the order can be joined to
    /// the visit that produced it. Optional, never stored on the order, and not
    /// capable of identifying anybody — it is a random string that dies with
    /// the tab. See <see cref="AnalyticsEventRequest"/>.
    /// </summary>
    [StringLength(64)]
    public string? Session { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!Models.Fulfilment.IsKnown(Fulfilment))
        {
            yield return new ValidationResult("Choose delivery or pickup.", [nameof(Fulfilment)]);
            yield break;
        }

        // A pickup is collected from the counter, so there is nothing else to ask.
        if (Models.Fulfilment.IsPickup(Fulfilment)) yield break;

        if (string.IsNullOrWhiteSpace(Address))
            yield return new ValidationResult("Where are we delivering?", [nameof(Address)]);

        if (string.IsNullOrWhiteSpace(City))
            yield return new ValidationResult("Which city or town?", [nameof(City)]);

        if (string.IsNullOrWhiteSpace(District))
            yield return new ValidationResult("Which district?", [nameof(District)]);

        if (string.IsNullOrWhiteSpace(Pincode))
            yield return new ValidationResult("A 6-digit pincode, please", [nameof(Pincode)]);
    }
}

/// <summary>One step in an order's life, as shown on the tracking page.</summary>
public sealed record OrderEvent(string Status, DateTimeOffset At, string? Note = null);

public sealed record Order(
    string OrderId,
    DateTimeOffset PlacedAt,
    string Status,
    string Name,
    string Phone,
    string? Email,
    string Address,
    string City,
    string District,
    string Pincode,
    string Payment,
    string? Notes,
    IReadOnlyList<CartLine> Items,
    CartTotals Totals,
    AppliedCoupon? Coupon,
    DateOnly DeliveryFrom,
    DateOnly DeliveryTo,
    /// <summary>
    /// <c>delivery</c> or <c>pickup</c>. Optional so orders journalled before
    /// pickup existed load as deliveries, which is what they were.
    /// </summary>
    string Fulfilment = Models.Fulfilment.Delivery,
    /// <summary>
    /// Every status this order has held, oldest first. Stored rather than
    /// derived, because "when did it ship" cannot be recovered from the current
    /// status alone - and that is the question a tracking page exists to answer.
    /// </summary>
    IReadOnlyList<OrderEvent>? History = null,
    /// <summary>
    /// One of <see cref="Models.PaymentStatus"/>. Optional so orders journalled
    /// before online payment existed load as <c>pending</c>, which is what a
    /// cash-on-delivery order is until the money is handed over.
    /// </summary>
    string PaymentStatus = Models.PaymentStatus.Pending,
    /// <summary>The gateway's own reference, once there is one.</summary>
    string? PaymentReference = null)
{
    /// <summary>
    /// Served, but never persisted — `PersistenceJson` drops it when the order
    /// journal is written, so a field that is true by construction does not end
    /// up recorded on every order.
    /// </summary>
    public bool Ok => true;

    public bool IsPickup => Models.Fulfilment.IsPickup(Fulfilment);

    /// <summary>Never null for a caller, however old the journal entry is.</summary>
    public IReadOnlyList<OrderEvent> Timeline => History ?? [new OrderEvent(Status, PlacedAt)];
}

/// <summary>
/// What a customer may see about their own order.
///
/// Deliberately narrower than <see cref="Order"/>: the tracking page proves
/// possession of a reference number and a phone number, which is not the same
/// as being signed in. So it carries the progress and the basket, and leaves
/// out the address, the email and the full phone number.
/// </summary>
public sealed record OrderTracking(
    string OrderId,
    DateTimeOffset PlacedAt,
    string Status,
    string StatusLabel,
    string Fulfilment,
    string Name,
    string MaskedPhone,
    IReadOnlyList<CartLine> Items,
    CartTotals Totals,
    DateOnly DeliveryFrom,
    DateOnly DeliveryTo,
    IReadOnlyList<OrderEvent> History,
    IReadOnlyList<string> Steps);

public sealed record OrderTrackRequest
{
    [Required(ErrorMessage = "Your order reference, like AC12345678")]
    public string OrderId { get; init; } = string.Empty;

    [Required(ErrorMessage = "The mobile number the order was placed with")]
    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "A 10-digit Indian mobile number, please")]
    public string Phone { get; init; } = string.Empty;
}

public sealed record BulkEnquiryRequest
{
    [Required(ErrorMessage = "What should we call you?")]
    [StringLength(80, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    public string? Organisation { get; init; }

    [Required(ErrorMessage = "A 10-digit mobile number, please")]
    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "A 10-digit Indian mobile number, please")]
    public string Phone { get; init; } = string.Empty;

    [EmailAddress(ErrorMessage = "That email does not look right")]
    public string? Email { get; init; }

    [Required(ErrorMessage = "Which district are we delivering to?")]
    public string District { get; init; } = string.Empty;

    public string? Budget { get; init; }
    public string? Quantity { get; init; }
    public string? Message { get; init; }

    /// <summary>The browser's anonymous analytics session. See OrderRequest.</summary>
    [StringLength(64)]
    public string? Session { get; init; }
}

public sealed record BulkEnquiry(
    string EnquiryId,
    DateTimeOffset ReceivedAt,
    string Status,
    string Name,
    string? Organisation,
    string Phone,
    string? Email,
    string District,
    string? Budget,
    string? Quantity,
    string? Message);

public sealed record ContactMessageRequest
{
    [Required(ErrorMessage = "What should we call you?")]
    [StringLength(80, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required(ErrorMessage = "A 10-digit mobile number, please")]
    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "A 10-digit mobile number, please")]
    public string Phone { get; init; } = string.Empty;

    [EmailAddress(ErrorMessage = "That email does not look right")]
    public string? Email { get; init; }

    public string? Subject { get; init; }

    [Required(ErrorMessage = "A sentence or two, so we can actually help")]
    [StringLength(2000, MinimumLength = 10,
        ErrorMessage = "A sentence or two, so we can actually help")]
    public string Message { get; init; } = string.Empty;
}

public sealed record ContactMessage(
    string MessageId,
    DateTimeOffset ReceivedAt,
    string Name,
    string Phone,
    string? Email,
    string? Subject,
    string Message);

public sealed record SubscribeRequest
{
    [Required(ErrorMessage = "That does not look like an email address")]
    [EmailAddress(ErrorMessage = "That does not look like an email address")]
    public string Email { get; init; } = string.Empty;
}

public sealed record Subscription(string Email, DateTimeOffset SubscribedAt, bool AlreadySubscribed);
