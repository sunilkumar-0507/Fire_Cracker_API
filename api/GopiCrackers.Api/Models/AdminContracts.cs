using System.ComponentModel.DataAnnotations;

namespace GopiCrackers.Api.Models;

/* -------------------------------------------------------------------------- */
/* Catalogue writes                                                            */
/* -------------------------------------------------------------------------- */

/// <summary>
/// What the admin form may set on a product. Everything the server can work out
/// for itself is left off: <c>id</c> and <c>code</c> are assigned on create,
/// and <c>discount</c> is always recomputed from price against MRP so the badge
/// on a card can never disagree with the two numbers beside it.
/// </summary>
public sealed record ProductWrite
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Left blank on create, the server slugifies the name.</summary>
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$",
        ErrorMessage = "A slug is lowercase words joined by hyphens")]
    public string? Slug { get; init; }

    [Required(ErrorMessage = "Pick a category")]
    public string Category { get; init; } = string.Empty;

    public string Brand { get; init; } = "SKV Pyros";

    [Range(1, 1_000_000, ErrorMessage = "Price must be at least ₹1")]
    public int Price { get; init; }

    [Range(1, 4_000_000, ErrorMessage = "MRP must be at least ₹1")]
    public int Mrp { get; init; }

    [Required(ErrorMessage = "What does a customer get — a box, a pack, a single piece?")]
    [StringLength(60)]
    public string Unit { get; init; } = string.Empty;

    [Required, StringLength(2000, MinimumLength = 20,
        ErrorMessage = "A sentence or two at least — this is what sells the product")]
    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Highlights { get; init; } = [];

    [MinLength(1, ErrorMessage = "A product needs at least one photo")]
    public IReadOnlyList<string> Images { get; init; } = [];

    [Range(0, 100_000)]
    public int Stock { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Specs { get; init; } = new Dictionary<string, string>();

    public bool Featured { get; init; }
    public bool BestSeller { get; init; }
    public bool IsNew { get; init; }

    /// <summary>
    /// False parks the product: it keeps its page, its photos and its history,
    /// but reads as "temporarily unavailable" and cannot be added to a basket.
    /// Defaults to true so a form that omits it creates something sellable.
    /// </summary>
    public bool Active { get; init; } = true;
}

/// <summary>The activate / deactivate toggle, on its own so one click is one request.</summary>
public sealed record ProductActiveWrite
{
    public bool Active { get; init; }
}

public sealed record CategoryWrite
{
    [Required, StringLength(60, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$",
        ErrorMessage = "A slug is lowercase words joined by hyphens")]
    public string? Slug { get; init; }

    public string TamilName { get; init; } = string.Empty;

    [Required, StringLength(80)]
    public string Tagline { get; init; } = string.Empty;

    [Required, StringLength(600, MinimumLength = 20)]
    public string Description { get; init; } = string.Empty;

    /// <summary>One of the eight vector art types the storefront can draw.</summary>
    public string Art { get; init; } = "flowerpot";

    /// <summary>One of the five accent tones in <c>constants/accents.js</c>.</summary>
    public string Tone { get; init; } = "amber";

    public string NoiseLevel { get; init; } = "low";
    public bool Featured { get; init; }
}

public sealed record ComboItemWrite
{
    [Required] public string Slug { get; init; } = string.Empty;
    [Range(1, 999)] public int Qty { get; init; } = 1;
}

/// <summary>
/// A bundle is described by what is in it. Price, MRP, saving and item count are
/// all derived from the catalogue lines on save, so a combo can never advertise
/// a discount its own contents do not add up to.
/// </summary>
public sealed record ComboWrite
{
    [Required, StringLength(80, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public string? Slug { get; init; }

    [Required, StringLength(120)]
    public string Tagline { get; init; } = string.Empty;

    [Required, StringLength(1000, MinimumLength = 20)]
    public string Description { get; init; } = string.Empty;

    public string Art { get; init; } = "giftbox";
    public string Tone { get; init; } = "amber";
    public string Serves { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;

    [Range(0, 100_000)] public int Stock { get; init; } = 25;
    public bool Featured { get; init; }

    /// <summary>Extra percent off the sum of the parts. 10 means "10% cheaper than buying them separately".</summary>
    [Range(0, 60, ErrorMessage = "A bundle discount above 60% is almost certainly a typo")]
    public int BundleDiscount { get; init; } = 10;

    [MinLength(1, ErrorMessage = "A combo needs at least one product in it")]
    public IReadOnlyList<ComboItemWrite> Includes { get; init; } = [];
}

public sealed record OfferWrite
{
    [Required]
    [RegularExpression("^[A-Z0-9]{4,20}$", ErrorMessage = "A code is 4–20 capitals and digits, like DIWALI75")]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(80, MinimumLength = 3)]
    public string Title { get; init; } = string.Empty;

    [StringLength(140)] public string Subtitle { get; init; } = string.Empty;

    [Required, StringLength(600, MinimumLength = 10)]
    public string Description { get; init; } = string.Empty;

    /// <summary><c>percentage</c> or <c>flat</c>.</summary>
    [RegularExpression("^(percentage|flat)$", ErrorMessage = "Type is percentage or flat")]
    public string Type { get; init; } = "percentage";

    [Range(0, 1_000_000)] public int Value { get; init; }
    [Range(0, 1_000_000)] public int MinOrder { get; init; }

    public string Art { get; init; } = "flowerpot";
    public string Tone { get; init; } = "amber";
    public string Badge { get; init; } = string.Empty;

    public DateTimeOffset EndsAt { get; init; } = DateTimeOffset.UtcNow.AddDays(30);

    [Range(1, 8760)] public int FallbackHours { get; init; } = 48;

    public bool Featured { get; init; }
    public IReadOnlyList<string> Terms { get; init; } = [];
}

/// <summary>One row of a stock take: a product id and its counted level.</summary>
public sealed record StockLevel
{
    [Required] public string Id { get; init; } = string.Empty;
    [Range(0, 100_000)] public int Stock { get; init; }
}

public sealed record StockWrite
{
    [Required, MinLength(1, ErrorMessage = "Nothing to update")]
    public IReadOnlyList<StockLevel> Levels { get; init; } = [];
}

public sealed record StockResult(int Updated, IReadOnlyList<string> Unknown);

/* -------------------------------------------------------------------------- */
/* Orders                                                                      */
/* -------------------------------------------------------------------------- */

public sealed record OrderStatusWrite
{
    [Required(ErrorMessage = "Which status?")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// An optional line recorded against the step, e.g. a lorry number or why
    /// it was cancelled. Shown to the customer on the tracking page.
    /// </summary>
    [StringLength(200)]
    public string? Note { get; init; }
}

/// <summary>
/// An invoice raised in the admin for a customer who did not use the website —
/// a counter sale, a phone order, a walk-in. It becomes an ordinary order, so it
/// takes stock off the shelf and appears in the order book beside the online
/// ones.
///
/// Looser than <see cref="OrderRequest"/> on purpose: the shopkeeper is typing
/// what the customer said across the counter, and a sale should not be refused
/// because nobody asked for a pincode. Prices are still the catalogue's, and a
/// coupon is still checked against the codes the admin has created.
/// </summary>
public sealed record OfflineOrderWrite
{
    [Required(ErrorMessage = "Who is the customer?")]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [RegularExpression(@"^\+?[\d\s-]{6,20}$", ErrorMessage = "That phone number does not look right")]
    public string? Phone { get; init; }

    [EmailAddress(ErrorMessage = "That email does not look right")]
    public string? Email { get; init; }

    [StringLength(400)]
    public string? Address { get; init; }

    [StringLength(120)]
    public string? City { get; init; }

    [StringLength(120)]
    public string? District { get; init; }

    [RegularExpression(@"^\d{6}$", ErrorMessage = "A 6-digit pincode, please")]
    public string? Pincode { get; init; }

    /// <summary><c>pickup</c> (handed over at the counter) or <c>delivery</c>.</summary>
    public string Fulfilment { get; init; } = Models.Fulfilment.Pickup;

    /// <summary>How the customer paid — cash, upi, card, bank or credit.</summary>
    [Required(ErrorMessage = "How did the customer pay?")]
    [StringLength(32)]
    public string Payment { get; init; } = "cash";

    /// <summary>True when the money has been taken.</summary>
    public bool Paid { get; init; } = true;

    /// <summary>Where the order starts. A counter sale is usually done already.</summary>
    public string Status { get; init; } = "completed";

    [StringLength(1000)]
    public string? Notes { get; init; }

    [Required, MinLength(1, ErrorMessage = "Add at least one item")]
    public IReadOnlyList<CartLineRequest> Items { get; init; } = [];

    public string? Coupon { get; init; }
}

/* -------------------------------------------------------------------------- */
/* Dashboard                                                                   */
/* -------------------------------------------------------------------------- */

public sealed record AdminSummary(
    int Products,
    int Categories,
    int Combos,
    int Offers,
    int Orders,
    int Revenue,
    int PendingOrders,
    int OutOfStock,
    int LowStock,
    IReadOnlyList<Order> RecentOrders,
    IReadOnlyList<Product> LowStockProducts,
    IReadOnlyList<StatusCount> ByStatus,
    /// <summary>Products the shop has parked — requirement 3's third state.</summary>
    int Unavailable,
    /// <summary>Bulk enquiries still waiting on a quote — requirement 11.</summary>
    int OpenEnquiries,
    int Enquiries,
    IReadOnlyList<BulkEnquiry> RecentEnquiries,
    /// <summary>Orders placed in the last seven days, and what they were worth.</summary>
    int OrdersThisWeek,
    int RevenueThisWeek);

/// <summary>One row of the admin enquiry list.</summary>
public sealed record EnquiryStatusWrite
{
    [Required(ErrorMessage = "Which status?")]
    public string Status { get; init; } = string.Empty;
}

public sealed record StatusCount(string Status, int Count);

/* -------------------------------------------------------------------------- */
/* Bootstrap                                                                   */
/* -------------------------------------------------------------------------- */

/// <summary>
/// Everything <c>src/data/index.js</c> needs to hydrate, in one round trip. The
/// storefront makes this call once before it mounts, which is why it is worth
/// one payload rather than seven.
/// </summary>
public sealed record Bootstrap(
    IReadOnlyList<Product> Products,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<Combo> Combos,
    IReadOnlyList<Offer> Offers,
    IReadOnlyList<Banner> Banners,
    IReadOnlyList<Testimonial> Testimonials,
    IReadOnlyList<Faq> Faqs);
