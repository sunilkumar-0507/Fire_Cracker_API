using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The same API, on MySQL.
///
/// These run only when <c>GOPI_TEST_MYSQL</c> names a server — see
/// <see cref="MySqlFixture"/> for what they do and do not touch. Their job is
/// the half of the persistence seam the rest of the suite cannot reach: that
/// pointing the API at an empty server produces a schema and a catalogue
/// without anyone running anything by hand, and that every endpoint then
/// answers exactly as it does on the files.
/// </summary>
[Collection(MySqlCollection.Name)]
public sealed class MySqlBackendTests(MySqlFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client { get; } = fixture.CreateClient();

    private HttpClient AdminClient
    {
        get
        {
            if (_admin is not null) return _admin;

            _admin = fixture.CreateClient();
            _admin.DefaultRequestHeaders.Add("X-Admin-Passcode", MySqlFixture.AdminPasscode);
            return _admin;
        }
    }

    private HttpClient? _admin;

    private async Task<JsonElement> AdminJsonAsync(string url)
    {
        var response = await AdminClient.GetAsync(url);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {url} returned {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /* ---------------------------------------------------------------------- */
    /* The schema creates itself                                               */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// The deployment-day claim, stated as an assertion: the fixture points the
    /// API at a database that does not exist, and by the time the first request
    /// is served it does — migrated, and with no migration left pending.
    /// </summary>
    [MySqlFact]
    public async Task Pointing_the_API_at_an_absent_database_creates_and_migrates_it()
    {
        var status = await AdminJsonAsync("/api/admin/database");

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("canConnect").GetBoolean(),
            $"Could not connect: {status}");

        Assert.Equal(MySqlFixture.TestDatabaseName, status.GetProperty("database").GetString());

        var applied = status.GetProperty("appliedMigrations").EnumerateArray().ToList();
        var pending = status.GetProperty("pendingMigrations").EnumerateArray().ToList();

        Assert.True(applied.Count > 0, "No migration was applied, so there is no schema.");
        Assert.True(pending.Count == 0,
            "Migrations are still pending after startup: " +
            string.Join(", ", pending.Select(p => p.GetString())));

        // An error field is set defensively by the status endpoint for things
        // that are worth reporting but not worth a 500 — a missing table, for
        // one. On a freshly migrated database there is nothing to report.
        Assert.True(status.GetProperty("error").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
            $"The database reported: {status.GetProperty("error")}");
    }

    /// <summary>
    /// Every table the status endpoint knows about exists and can be counted.
    /// A table that had not been created would surface here as the error the
    /// previous test rules out, but naming them individually is what turns
    /// "something is wrong" into "stock_intake is missing".
    /// </summary>
    [MySqlFact]
    public async Task Every_table_in_the_schema_is_present_and_countable()
    {
        var status = await AdminJsonAsync("/api/admin/database");

        var tables = status.GetProperty("tables").EnumerateArray()
            .ToDictionary(t => t.GetProperty("table").GetString()!,
                          t => t.GetProperty("rows").GetInt64());

        string[] expected =
        [
            "products", "categories", "combos", "offers", "banners", "testimonials",
            "faqs", "orders", "order_items", "order_events", "bulk_enquiries",
            "contact_messages", "newsletter_subscribers", "stock_intake", "analytics_events",
        ];

        var missing = expected.Where(t => !tables.ContainsKey(t)).ToList();

        Assert.True(missing.Count == 0,
            "The schema is missing tables: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The seeder filled the empty database from the JSON catalogue, so the
    /// shop arrives with its price list rather than with an empty shelf.
    /// </summary>
    [MySqlFact]
    public async Task An_empty_database_is_seeded_with_the_catalogue()
    {
        var status = await AdminJsonAsync("/api/admin/database");

        var rows = status.GetProperty("tables").EnumerateArray()
            .ToDictionary(t => t.GetProperty("table").GetString()!,
                          t => t.GetProperty("rows").GetInt64());

        Assert.True(rows["products"] > 0, "The products table is empty — the seeder did not run.");
        Assert.True(rows["categories"] > 0, "The categories table is empty.");

        // And the catalogue the endpoints serve is the one in the database,
        // not a leftover in-memory copy of the files.
        var listed = await Client.GetFromJsonAsync<JsonElement>("/api/products?pageSize=1", Json);
        Assert.Equal(rows["products"], listed.GetProperty("total").GetInt64());
    }

    /// <summary>Every store is on MySQL, not just the one that was checked.</summary>
    [MySqlFact]
    public async Task Every_store_reports_the_mysql_backend()
    {
        var backends = await AdminJsonAsync("/api/admin/database/backends");

        foreach (var store in (string[])["catalogue", "orders", "inventory", "analytics", "configured"])
            Assert.Equal("mysql", backends.GetProperty(store).GetString());
    }

    /* ---------------------------------------------------------------------- */
    /* The endpoints behave identically                                        */
    /* ---------------------------------------------------------------------- */

    [MySqlFact]
    public async Task Every_documented_GET_answers_200_on_mysql()
    {
        var failures = await EndpointAudit.FailingGetsAsync(Client, MySqlFixture.AdminPasscode);

        Assert.True(failures.Count == 0,
            $"{failures.Count} documented GET endpoint(s) did not return 200 on MySQL:\n  " +
            string.Join("\n  ", failures));
    }

    [MySqlFact]
    public async Task Every_admin_endpoint_refuses_a_caller_without_the_passcode_on_mysql()
    {
        var failures = await EndpointAudit.UnprotectedAdminAsync(Client);

        Assert.True(failures.Count == 0,
            $"{failures.Count} admin endpoint(s) did not refuse an anonymous caller on MySQL:\n  " +
            string.Join("\n  ", failures));
    }

    /* ---------------------------------------------------------------------- */
    /* Writes reach the database                                               */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// An order placed over HTTP lands in the orders table and comes back out
    /// of it.
    ///
    /// The row count either side is what makes this a persistence test rather
    /// than a cache one: the stores keep their contents in memory, so reading
    /// an order back proves only that the write reached RAM. The count comes
    /// from <c>SELECT COUNT(*)</c> against the table.
    /// </summary>
    [MySqlFact]
    public async Task An_order_placed_over_HTTP_is_written_to_the_orders_table()
    {
        var before = await RowsAsync("orders");

        var products = await Client.GetFromJsonAsync<JsonElement>("/api/products?pageSize=1", Json);
        var productId = products.GetProperty("items").EnumerateArray().First()
            .GetProperty("id").GetString();

        var response = await Client.PostAsJsonAsync("/api/orders", new
        {
            name = "Meenakshi Raghavan",
            phone = EndpointAudit.OrderPhone,
            email = "meenakshi@example.in",
            address = "12 Second Cross Street, Adyar",
            city = "Chennai",
            district = "Chennai",
            pincode = "600020",
            payment = "upi",
            items = new[] { new { id = productId, qty = 2 } },
        }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var orderId = created.GetProperty("orderId").GetString();

        Assert.Equal(before + 1, await RowsAsync("orders"));

        // The line items are a separate table with a foreign key, so an order
        // that saved its header and lost its basket would pass everything above.
        Assert.True(await RowsAsync("order_items") > 0, "The order saved no line items.");

        var tracked = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/orders/{orderId}/track?phone={EndpointAudit.OrderPhone}", Json);

        Assert.Equal(orderId, tracked.GetProperty("orderId").GetString());
    }

    /// <summary>
    /// An admin edit to the catalogue survives in the products table.
    ///
    /// Created and deleted within the test: the scratch database is disposable,
    /// but a test that leaves a fake product behind makes the next run's row
    /// counts lie.
    /// </summary>
    [MySqlFact]
    public async Task An_admin_product_edit_is_written_to_the_products_table()
    {
        var before = await RowsAsync("products");

        var created = await AdminClient.PostAsJsonAsync("/api/admin/products", new
        {
            name = "Audit Test Sparkler",
            category = "sparklers",
            price = 100,
            mrp = 200,
            stock = 5,
            description = "Created by the MySQL backend test.",
        }, Json);

        Assert.True(created.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Creating a product returned {(int)created.StatusCode}: " +
            await created.Content.ReadAsStringAsync());

        var product = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = product.GetProperty("id").GetString();

        Assert.Equal(before + 1, await RowsAsync("products"));

        var deleted = await AdminClient.DeleteAsync($"/api/admin/products/{id}");
        Assert.True(deleted.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"Deleting the product returned {(int)deleted.StatusCode}.");

        Assert.Equal(before, await RowsAsync("products"));
    }

    /// <summary>The row count the database reports for one table.</summary>
    private async Task<long> RowsAsync(string table)
    {
        var status = await AdminJsonAsync("/api/admin/database");

        return status.GetProperty("tables").EnumerateArray()
            .First(t => t.GetProperty("table").GetString() == table)
            .GetProperty("rows").GetInt64();
    }
}
