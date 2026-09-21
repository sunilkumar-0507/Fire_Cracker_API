using GopiCrackers.Api.Data;
using GopiCrackers.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The database model and its mapping, checked without a database.
///
/// EF builds the whole model from <c>OnModelCreating</c> the first time a
/// context is constructed, and building it is where a bad length, a duplicate
/// key, a relationship that cannot be resolved or a converter that does not
/// apply blows up. So constructing one context proves more than it looks like
/// it does — and it proves it on a machine with no MySQL on it, which is the
/// only reason these assertions can run in CI at all.
///
/// What they cannot prove is that MySQL accepts the SQL. That needs a server;
/// <c>dotnet ef migrations script</c> is the cheapest way to look at what it
/// would be sent.
/// </summary>
public sealed class DatabaseModelTests
{
    /// <summary>
    /// A context that is never connected to. The version is pinned for the same
    /// reason the design-time factory pins it: detecting it would need a server.
    /// </summary>
    private static GopiCrackersDbContext Context() =>
        new(new DbContextOptionsBuilder<GopiCrackersDbContext>()
            .UseMySql(
                "Server=127.0.0.1;Database=gopicrackers_tests;User ID=none;Password=none",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options);

    [Fact]
    public void The_model_builds_and_names_every_table()
    {
        using var db = Context();

        var tables = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .ToHashSet()!;

        string[] expected =
        [
            "products", "categories", "combos", "offers", "banners", "testimonials", "faqs",
            "orders", "order_items", "order_events",
            "bulk_enquiries", "contact_messages", "newsletter_subscribers",
            "stock_intake", "analytics_events",
        ];

        foreach (var table in expected)
            Assert.Contains(table, tables);

        Assert.Equal(expected.Length, tables.Count);
    }

    [Fact]
    public void Every_timestamp_is_stored_as_a_utc_datetime()
    {
        using var db = Context();

        var timestamps = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?))
            .ToList();

        Assert.NotEmpty(timestamps);

        foreach (var property in timestamps)
        {
            Assert.NotNull(property.GetValueConverter());
            Assert.Equal("datetime(6)", property.GetColumnType());
        }
    }

    [Fact]
    public void Slugs_and_codes_are_unique()
    {
        using var db = Context();

        Assert.True(IsUnique(db, typeof(ProductRow), nameof(ProductRow.Slug)));
        Assert.True(IsUnique(db, typeof(CategoryRow), nameof(CategoryRow.Slug)));
        Assert.True(IsUnique(db, typeof(ComboRow), nameof(ComboRow.Slug)));
        Assert.True(IsUnique(db, typeof(OfferRow), nameof(OfferRow.Code)));
    }

    /// <summary>
    /// utf8mb4 costs four bytes a character and InnoDB caps a key at 3072, so
    /// anything longer than 768 characters cannot be indexed. Every key column
    /// here is well inside that — this asserts it stays that way.
    /// </summary>
    [Fact]
    public void No_indexed_column_is_too_long_to_index()
    {
        using var db = Context();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var indexed = entity.GetIndexes()
                .SelectMany(i => i.Properties)
                .Concat(entity.FindPrimaryKey()?.Properties ?? [])
                .Where(p => p.ClrType == typeof(string));

            foreach (var property in indexed)
            {
                var length = property.GetMaxLength();

                Assert.True(
                    length is > 0 and <= 768,
                    $"{entity.GetTableName()}.{property.Name} is indexed but its max length is " +
                    $"{(length?.ToString() ?? "unbounded")} — MySQL cannot index that under utf8mb4.");
            }
        }
    }

    /// <summary>
    /// Compares two models by the JSON they serialise to.
    ///
    /// Record equality will not do it: every one of these records has a list or
    /// a dictionary on it, and a record compares those by reference — so two
    /// products with identical contents are unequal purely because one arrived
    /// through a round trip. JSON is also the better question to ask, because
    /// it is what a caller actually receives.
    /// </summary>
    private static void AssertSame<T>(T expected, T actual)
    {
        var options = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(expected, options),
            System.Text.Json.JsonSerializer.Serialize(actual, options));
    }

    private static bool IsUnique(GopiCrackersDbContext db, Type entity, string property) =>
        db.Model.FindEntityType(entity)!
            .GetIndexes()
            .Any(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == property);

    /* ---------------------------------------------------------------------- */
    /* Mapping                                                                 */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public void A_product_survives_the_round_trip()
    {
        var product = new Product(
            Id: "p-042",
            Code: "128",
            Slug: "flower-pots-giant",
            Name: "Flower Pots Giant",
            Category: "flower-pots",
            Brand: "Gopi Crackers",
            Price: 240,
            Mrp: 400,
            Discount: 40,
            Unit: "1 box (10 pcs)",
            Description: "A tall gold fountain — ₹ signs and ½-inch fuses included.",
            Highlights: ["Burns for 45 seconds", "Under 110 dB"],
            Images: ["/img/a.jpg", "/img/b.jpg"],
            Stock: 36,
            Tags: ["silent", "kids"],
            Specs: new Dictionary<string, string> { ["Height"] = "30 cm", ["Safe distance"] = "5 m" },
            Featured: true,
            BestSeller: false,
            Combo: false,
            IsNew: true,
            Active: false);

        var round = new ProductRow().Fill(product).ToModel();

        AssertSame(product, round);
        // The derived members are derived, not stored — so they have to come
        // back right rather than come back at all.
        Assert.Equal(Availability.Unavailable, round.Availability);
    }

    [Fact]
    public void An_order_survives_the_round_trip_with_its_lines_and_timeline()
    {
        var placed = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

        var order = new Order(
            OrderId: "AC12345678",
            PlacedAt: placed,
            Status: "processing",
            Name: "Priya R",
            Phone: "9842011994",
            Email: "priya@example.com",
            Address: "14/3 Sattur Main Road",
            City: "Sivakasi",
            District: "Virudhunagar",
            Pincode: "626123",
            Payment: "cod",
            Notes: "Ring the bell twice",
            Items:
            [
                new CartLine("p-001", "product", "sparklers-15cm", "15cm Sparklers", "1 box",
                    60, 100, "/img/s.jpg", "sparklers", 20, ["kids"], 2, 120, false),
                new CartLine("cmb-family", "combo", "family-pack", "Family Pack", "1 box",
                    2400, 4000, null, "combos", 5, [], 1, 2400, true, Availability.OutOfStock),
            ],
            Totals: new CartTotals(2520, 4100, 1580, 200, 0, 2320, 1780, 0, 3),
            Coupon: new AppliedCoupon("EARLYBIRD", "percentage", 10, 1500, "10% off before the rush"),
            DeliveryFrom: new DateOnly(2026, 10, 16),
            DeliveryTo: new DateOnly(2026, 10, 19),
            Fulfilment: Fulfilment.Delivery,
            History:
            [
                new OrderEvent("pending", placed, "Order received"),
                new OrderEvent("confirmed", placed.AddHours(2), null),
                new OrderEvent("processing", placed.AddHours(5), "Being packed"),
            ],
            PaymentStatus: PaymentStatus.Processing,
            PaymentReference: "cf_8812");

        var round = order.ToRow().ToModel();

        Assert.Equal(order.OrderId, round.OrderId);
        Assert.Equal(order.PlacedAt, round.PlacedAt);
        Assert.Equal(order.Totals, round.Totals);
        Assert.Equal(order.Coupon, round.Coupon);
        Assert.Equal(order.DeliveryFrom, round.DeliveryFrom);
        Assert.Equal(order.DeliveryTo, round.DeliveryTo);
        Assert.Equal(order.PaymentStatus, round.PaymentStatus);
        Assert.Equal(order.PaymentReference, round.PaymentReference);

        // Lines keep their order, their kind and the availability they were
        // priced at — a combo line that came back as a product line would put
        // the stock adjustment on the wrong list.
        AssertSame(order.Items, round.Items);
        AssertSame(order.Timeline, round.Timeline);
    }

    [Fact]
    public void An_order_with_no_coupon_maps_to_no_coupon()
    {
        var order = new Order(
            OrderId: "AC87654321",
            PlacedAt: DateTimeOffset.UtcNow,
            Status: "pending",
            Name: "Anand",
            Phone: "9842011995",
            Email: null,
            Address: "Counter",
            City: "Sivakasi",
            District: "Virudhunagar",
            Pincode: "626123",
            Payment: "upi",
            Notes: null,
            Items: [],
            Totals: new CartTotals(0, 0, 0, 0, 0, 0, 0, 0, 0),
            Coupon: null,
            DeliveryFrom: new DateOnly(2026, 10, 16),
            DeliveryTo: new DateOnly(2026, 10, 16),
            Fulfilment: Fulfilment.Pickup);

        var row = order.ToRow();
        Assert.Null(row.CouponCode);
        Assert.Null(row.ToModel().Coupon);

        // An order that predates a timeline still gets one, so tracking has
        // something to draw — the same rule the journal has always applied.
        Assert.Single(row.History);
    }

    [Fact]
    public void A_banner_with_one_call_to_action_keeps_exactly_one()
    {
        var banner = new Banner(
            Id: "b-hero",
            Placement: "home-hero",
            Title: "Diwali 2026",
            Eyebrow: null,
            TitleAccent: "2026",
            Subtitle: null,
            CtaPrimary: new BannerCta("Shop now", "/products"),
            CtaSecondary: null,
            Art: "rocket",
            Accent: "#f97316",
            AccentTo: null);

        var round = new BannerRow().Fill(banner).ToModel();

        Assert.Equal(banner.CtaPrimary, round.CtaPrimary);
        Assert.Null(round.CtaSecondary);
        AssertSame(banner, round);
    }
}
