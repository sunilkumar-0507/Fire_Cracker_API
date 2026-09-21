using System.Net;
using System.Text.Json;
using Xunit;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Stock movement and the intake ledger.
///
/// The three figures on the inventory screen come from three different places,
/// and the whole value of the screen is that they agree. These tests are about
/// keeping them agreeing.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InventoryEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private async Task<JsonElement> RowAsync(string productId)
    {
        var report = await GetAdminJsonAsync("/api/admin/inventory");
        return report.GetProperty("rows")
            .EnumerateArray()
            .Single(r => r.GetProperty("productId").GetString() == productId);
    }

    private static int Stock(JsonElement product) => product.GetProperty("stock").GetInt32();

    /// <summary>A valid single-line order, so each test reads as its assertion.</summary>
    private static object OrderFor(string productId, int qty) => new
    {
        name = "Inventory Test",
        phone = "9842011994",
        email = "inventory@example.in",
        address = "12 Second Cross Street, Adyar",
        city = "Chennai",
        district = "Chennai",
        pincode = "600020",
        payment = "cod",
        items = new[] { new { id = productId, qty } },
    };

    [Fact]
    public async Task The_report_is_closed_without_the_passcode() =>
        await AssertStatusAsync("/api/admin/inventory", HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Recording_intake_raises_the_stock_level_and_the_ledger_together()
    {
        var before = Stock(await GetJsonAsync("/api/products/p-001"));

        var (status, entry) = await PostAdminAsync("/api/admin/inventory/intake", new
        {
            productId = "p-001",
            quantity = 40,
            supplier = "Test supplier",
        });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(40, entry.GetProperty("quantity").GetInt32());

        // The shelf moved...
        Assert.Equal(before + 40, Stock(await GetJsonAsync("/api/products/p-001")));

        // ...and so did the ledger the report is built from.
        var row = await RowAsync("p-001");
        Assert.Equal(40, row.GetProperty("intake").GetInt32());
        Assert.Equal(before + 40, row.GetProperty("holding").GetInt32());
    }

    [Fact]
    public async Task A_negative_entry_writes_stock_off()
    {
        await PostAdminAsync("/api/admin/inventory/intake", new { productId = "p-003", quantity = 30 });
        var afterIntake = Stock(await GetJsonAsync("/api/products/p-003"));

        var (status, _) = await PostAdminAsync("/api/admin/inventory/intake",
            new { productId = "p-003", quantity = -5, note = "Two boxes damaged in transit" });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(afterIntake - 5, Stock(await GetJsonAsync("/api/products/p-003")));

        // The ledger nets out rather than forgetting the correction.
        Assert.Equal(25, (await RowAsync("p-003")).GetProperty("intake").GetInt32());
    }

    [Fact]
    public async Task Stock_cannot_be_written_below_zero()
    {
        var held = Stock(await GetJsonAsync("/api/products/p-004"));

        var (status, _) = await PostAdminAsync("/api/admin/inventory/intake",
            new { productId = "p-004", quantity = -(held + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(held, Stock(await GetJsonAsync("/api/products/p-004")));
    }

    [Fact]
    public async Task An_entry_of_zero_is_refused()
    {
        var (status, _) = await PostAdminAsync("/api/admin/inventory/intake",
            new { productId = "p-001", quantity = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Intake_against_an_unknown_product_is_refused()
    {
        var (status, _) = await PostAdminAsync("/api/admin/inventory/intake",
            new { productId = "p-does-not-exist", quantity = 10 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// The reason stock has to move on its own: nothing decremented it before,
    /// so the shop would keep selling the same box until somebody noticed.
    /// </summary>
    [Fact]
    public async Task Placing_an_order_takes_the_goods_off_the_shelf()
    {
        var before = Stock(await GetJsonAsync("/api/products/p-005"));

        var (status, order) = await PostAsync("/api/orders", OrderFor("p-005", 3));
        Assert.Equal(HttpStatusCode.Created, status);

        Assert.Equal(before - 3, Stock(await GetJsonAsync("/api/products/p-005")));

        var row = await RowAsync("p-005");
        Assert.Equal(3, row.GetProperty("sold").GetInt32());
        Assert.Equal(before - 3, row.GetProperty("holding").GetInt32());

        // And the order is still findable, which is what "sold" is counted from.
        Assert.False(string.IsNullOrWhiteSpace(order.GetProperty("orderId").GetString()));
    }

    [Fact]
    public async Task Cancelling_an_order_puts_the_goods_back()
    {
        var before = Stock(await GetJsonAsync("/api/products/p-006"));

        var (_, order) = await PostAsync("/api/orders", OrderFor("p-006", 4));
        var orderId = order.GetProperty("orderId").GetString()!;
        Assert.Equal(before - 4, Stock(await GetJsonAsync("/api/products/p-006")));

        var (status, _) = await PatchAdminAsync($"/api/admin/orders/{orderId}/status",
            new { status = "cancelled" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(before, Stock(await GetJsonAsync("/api/products/p-006")));

        // A cancelled order is not a sale, so it drops out of the count too.
        Assert.Equal(0, (await RowAsync("p-006")).GetProperty("sold").GetInt32());
    }

    [Fact]
    public async Task Cancelling_twice_does_not_return_the_goods_twice()
    {
        var before = Stock(await GetJsonAsync("/api/products/p-007"));

        var (_, order) = await PostAsync("/api/orders", OrderFor("p-007", 2));
        var orderId = order.GetProperty("orderId").GetString()!;

        await PatchAdminAsync($"/api/admin/orders/{orderId}/status", new { status = "cancelled", note = "First" });
        await PatchAdminAsync($"/api/admin/orders/{orderId}/status", new { status = "cancelled", note = "Again" });

        Assert.Equal(before, Stock(await GetJsonAsync("/api/products/p-007")));
    }

    [Fact]
    public async Task Reopening_a_cancelled_order_takes_the_goods_again()
    {
        var before = Stock(await GetJsonAsync("/api/products/p-008"));

        var (_, order) = await PostAsync("/api/orders", OrderFor("p-008", 5));
        var orderId = order.GetProperty("orderId").GetString()!;

        await PatchAdminAsync($"/api/admin/orders/{orderId}/status", new { status = "cancelled" });
        Assert.Equal(before, Stock(await GetJsonAsync("/api/products/p-008")));

        await PatchAdminAsync($"/api/admin/orders/{orderId}/status", new { status = "confirmed" });
        Assert.Equal(before - 5, Stock(await GetJsonAsync("/api/products/p-008")));
    }

    [Fact]
    public async Task Best_sellers_are_ranked_by_units_despatched()
    {
        await PostAsync("/api/orders", OrderFor("p-009", 9));
        await PostAsync("/api/orders", OrderFor("p-010", 2));

        var report = await GetAdminJsonAsync("/api/admin/inventory");
        var best = report.GetProperty("bestSellers").EnumerateArray().ToList();

        Assert.NotEmpty(best);

        // Descending, and each entry actually sold something.
        var sold = best.Select(b => b.GetProperty("sold").GetInt32()).ToList();
        Assert.Equal(sold.OrderByDescending(x => x), sold);
        Assert.All(sold, s => Assert.True(s > 0));
    }

    [Fact]
    public async Task Totals_are_the_sum_of_the_rows()
    {
        var report = await GetAdminJsonAsync("/api/admin/inventory");
        var rows = report.GetProperty("rows").EnumerateArray().ToList();

        Assert.Equal(rows.Sum(r => r.GetProperty("intake").GetInt32()),
            report.GetProperty("totalIntake").GetInt32());
        Assert.Equal(rows.Sum(r => r.GetProperty("sold").GetInt32()),
            report.GetProperty("totalSold").GetInt32());
        Assert.Equal(rows.Sum(r => r.GetProperty("holding").GetInt32()),
            report.GetProperty("totalHolding").GetInt32());
    }

    /// <summary>
    /// A product nothing has ever been received against reports zero drift
    /// rather than its whole shelf, which would make the column meaningless on
    /// a catalogue that predates the ledger.
    /// </summary>
    [Fact]
    public async Task A_product_with_no_ledger_history_reports_no_drift()
    {
        var row = await RowAsync("p-150");

        Assert.Equal(0, row.GetProperty("intake").GetInt32());
        Assert.Equal(0, row.GetProperty("unaccounted").GetInt32());
    }
}
