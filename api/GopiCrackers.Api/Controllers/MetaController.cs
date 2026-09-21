using System.Reflection;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The non-catalogue constants the storefront needs, plus health and counts.
/// Everything here is a server-side copy of <c>src/constants/index.js</c>.
/// </summary>
[ApiController]
[Route("api/meta")]
public sealed class MetaController(
    CatalogStore catalog,
    OrderStore orders,
    PricingService pricing,
    IOptions<StorefrontOptions> options,
    IOptions<DatabaseOptions> database) : ControllerBase
{
    private readonly StorefrontOptions _options = options.Value;

    /// <summary>Liveness check plus the row counts loaded from the seed files.</summary>
    [HttpGet("/api/health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new
    {
        status = "healthy",
        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
        checkedAt = DateTimeOffset.UtcNow,
        catalogue = new
        {
            products = catalog.Products.Count,
            categories = catalog.Categories.Count,
            combos = catalog.Combos.Count,
            offers = catalog.Offers.Count,
            banners = catalog.Banners.Count,
            testimonials = catalog.Testimonials.Count,
            faqs = catalog.Faqs.Count,
            tags = catalog.Tags.Count,
        },
        // "Session" was the right word when nothing here outlived the process.
        // With a database behind it these counts are the shop's whole record,
        // so the storage block says which it is rather than leaving a reader to
        // guess why the order count survived a restart.
        storage = new
        {
            backend = catalog.Backend,
            database = database.Value.Enabled ? "configured" : "not configured",
        },
        session = new
        {
            orders = orders.RecentOrders(int.MaxValue).Count,
            subscribers = orders.SubscriberCount,
        },
    });

    /// <summary>Shop identity — name, licence, GSTIN, contact details, hours.</summary>
    [HttpGet("brand")]
    [ProducesResponseType<BrandOptions>(StatusCodes.Status200OK)]
    public ActionResult<BrandOptions> Brand() => Ok(_options.Brand);

    /// <summary>Districts the delivery and bulk forms accept.</summary>
    [HttpGet("districts")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> Districts() => Ok(_options.Districts);

    /// <summary>Accepted payment methods. Ids are what <c>POST /api/orders</c> expects.</summary>
    [HttpGet("payment-methods")]
    [ProducesResponseType<IReadOnlyList<PaymentMethod>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PaymentMethod>> PaymentMethods() => Ok(_options.PaymentMethods);

    [HttpGet("safety-rules")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> SafetyRules() => Ok(_options.SafetyRules);

    [HttpGet("popular-searches")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> PopularSearches() => Ok(_options.PopularSearches);

    [HttpGet("trust-points")]
    [ProducesResponseType<IReadOnlyList<TrustPoint>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TrustPoint>> TrustPoints() => Ok(_options.TrustPoints);

    /// <summary>
    /// How an order can be handed over, and where a pickup is collected from.
    /// The storefront renders the checkout step straight from this, so changing
    /// the counter's address or hours is configuration, not a rebuild.
    /// </summary>
    [HttpGet("fulfilment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult FulfilmentOptions() => Ok(new
    {
        methods = new object[]
        {
            new
            {
                id = Models.Fulfilment.Delivery,
                label = "Deliver to me",
                hint = $"Licensed surface transport. Free above ₹{_options.Shipping.FreeAbove:N0}, "
                     + $"otherwise ₹{_options.Shipping.LocalFee:N0}.",
                available = true,
            },
            new
            {
                id = Models.Fulfilment.Pickup,
                label = "Collect from the shop",
                hint = "No delivery charge. Ready the next working day.",
                available = _options.Pickup.Enabled,
            },
        },
        pickup = _options.Pickup,
    });

    /// <summary>The three availability states, so a client never hard-codes them.</summary>
    [HttpGet("availability")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> AvailabilityStates() => Ok(Models.Availability.All);

    /// <summary>The order statuses, in the order they happen — requirement 9.</summary>
    [HttpGet("order-statuses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult OrderStatuses() => Ok(OrderStore.Statuses.Select(s => new
    {
        id = s,
        label = OrderStore.Label(s, pickup: false),
        pickupLabel = OrderStore.Label(s, pickup: true),
        // -1 for `cancelled`, which sits outside the sequence rather than in it.
        step = OrderStore.ProgressSteps.ToList().IndexOf(s),
    }));

    /// <summary>
    /// Everything a cold storefront boot needs in one round trip: brand,
    /// shipping, coupons, payment methods, districts, categories, tags and
    /// price bounds.
    ///
    /// Coupons are here because the checkout has to know which codes exist
    /// before a customer types one. They carry no secret — the offers page
    /// prints them — and the discount is still recomputed server-side on
    /// <c>POST /api/orders</c>, so this list decides what the form will accept,
    /// never what the order costs.
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Config() => Ok(new
    {
        brand = _options.Brand,
        shipping = _options.Shipping,
        coupons = pricing.Coupons.ToDictionary(
            kv => kv.Key,
            kv => new AppliedCoupon(kv.Key, kv.Value.Type, kv.Value.Value, kv.Value.MinOrder, kv.Value.Note)),
        paymentMethods = _options.PaymentMethods,
        districts = _options.Districts,
        safetyRules = _options.SafetyRules,
        popularSearches = _options.PopularSearches,
        trustPoints = _options.TrustPoints,
        sortOptions = ProductQueryService.SortOptions,
        pickup = _options.Pickup,
        fulfilmentMethods = Models.Fulfilment.All,
        availabilityStates = Models.Availability.All,
        orderStatuses = OrderStore.Statuses,
        categories = catalog.Categories,
        tags = catalog.Tags,
        priceBounds = catalog.PriceBounds,
    });
}
