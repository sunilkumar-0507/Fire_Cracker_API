using System.Net;
using System.Text.Json;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The requirements added in the 2026 brief that are not already covered
/// elsewhere: availability as a three-state (3), activate/deactivate (10),
/// enquiry management (11) and analytics (13).
///
/// Delivery/pickup (8), reference numbers (6) and order status (9) are
/// exercised in <see cref="CommerceEndpointTests"/>, beside the rest of the
/// checkout they belong to.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RequirementsEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    /* ---------------------------------------------------------------------- */
    /* 3 + 10. Availability and the activate / deactivate switch               */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Every_product_reports_one_of_the_three_availability_states()
    {
        var states = (await GetJsonAsync("/api/meta/availability"))
            .EnumerateArray().Select(s => s.GetString()).ToHashSet();

        Assert.Equal(["available", "out-of-stock", "unavailable"], states);

        var page = await GetJsonAsync("/api/products?pageSize=100");
        foreach (var product in page.GetProperty("items").EnumerateArray())
            Assert.Contains(product.GetProperty("availability").GetString(), states);
    }

    [Fact]
    public async Task Availability_is_derived_from_stock_and_the_active_switch()
    {
        const string slug = "p-004";

        try
        {
            // Active with stock on the shelf.
            var before = await GetJsonAsync($"/api/products/{slug}");
            if (before.GetProperty("stock").GetInt32() > 0)
                Assert.Equal("available", before.GetProperty("availability").GetString());

            // Deactivated: "temporarily unavailable", whatever the stock says.
            var (status, deactivated) = await PatchAdminAsync(
                $"/api/admin/products/{slug}/active", new { active = false });

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(deactivated.GetProperty("active").GetBoolean());
            Assert.Equal("unavailable", deactivated.GetProperty("availability").GetString());

            // And it stays that way when read back through the storefront route.
            var reread = await GetJsonAsync($"/api/products/{slug}");
            Assert.Equal("unavailable", reread.GetProperty("availability").GetString());
        }
        finally
        {
            await PatchAdminAsync($"/api/admin/products/{slug}/active", new { active = true });
        }
    }

    [Fact]
    public async Task A_deactivated_product_cannot_be_put_in_a_basket()
    {
        const string slug = "p-005";

        try
        {
            await PatchAdminAsync($"/api/admin/products/{slug}/active", new { active = false });

            var (status, body) = await PostAsync("/api/cart/quote", new
            {
                items = new[] { new { id = slug, qty = 1 } },
            });

            Assert.Equal(HttpStatusCode.BadRequest, status);

            // The message has to say *why*, and "unavailable" is a different
            // sentence from "sold out" for whoever is reading the checkout.
            Assert.Contains("temporarily unavailable",
                body.GetProperty("errors").ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await PatchAdminAsync($"/api/admin/products/{slug}/active", new { active = true });
        }
    }

    [Fact]
    public async Task The_stock_filter_hides_deactivated_products_too()
    {
        const string slug = "p-006";

        try
        {
            await PatchAdminAsync($"/api/admin/products/{slug}/active", new { active = false });

            var page = await GetJsonAsync("/api/products?stock=1&pageSize=100&sort=price-asc");
            var ids = page.GetProperty("items").EnumerateArray()
                .Select(p => p.GetProperty("id").GetString())
                .ToList();

            Assert.DoesNotContain(slug, ids);

            var unavailableOnly = await GetJsonAsync("/api/products?availability=unavailable&pageSize=100");
            var withdrawn = unavailableOnly.GetProperty("items").EnumerateArray()
                .Select(p => p.GetProperty("id").GetString())
                .ToList();

            Assert.Contains(slug, withdrawn);
        }
        finally
        {
            await PatchAdminAsync($"/api/admin/products/{slug}/active", new { active = true });
        }
    }

    [Fact]
    public async Task Deactivating_an_unknown_product_is_a_404()
    {
        var (status, _) = await PatchAdminAsync(
            "/api/admin/products/p-nonexistent/active", new { active = false });

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    /* ---------------------------------------------------------------------- */
    /* 8. Delivery and pickup                                                  */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Fulfilment_options_describe_both_delivery_and_pickup()
    {
        var body = await GetJsonAsync("/api/meta/fulfilment");

        var ids = body.GetProperty("methods").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .ToList();

        Assert.Equal(["delivery", "pickup"], ids);
        Assert.False(string.IsNullOrWhiteSpace(
            body.GetProperty("pickup").GetProperty("address").GetString()));
    }

    [Fact]
    public async Task A_pickup_quote_drops_the_delivery_fee_and_the_free_delivery_nudge()
    {
        object[] items = [new { id = "p-001", qty = 1 }];

        var (_, delivery) = await PostAsync("/api/cart/quote", new { items, fulfilment = "delivery" });
        var (_, pickup) = await PostAsync("/api/cart/quote", new { items, fulfilment = "pickup" });

        var deliveryTotals = delivery.GetProperty("totals");
        var pickupTotals = pickup.GetProperty("totals");

        // A single cheap item is well under the free-delivery threshold, so the
        // delivery quote must actually be carrying a fee for this to mean anything.
        Assert.True(deliveryTotals.GetProperty("shipping").GetInt32() > 0);
        Assert.Equal(0, pickupTotals.GetProperty("shipping").GetInt32());
        Assert.Equal(0, pickupTotals.GetProperty("freeShippingGap").GetInt32());

        Assert.Equal(
            deliveryTotals.GetProperty("total").GetInt32() - deliveryTotals.GetProperty("shipping").GetInt32(),
            pickupTotals.GetProperty("total").GetInt32());
    }

    /* ---------------------------------------------------------------------- */
    /* 9. Order status                                                         */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Order_statuses_cover_the_vocabulary_the_brief_asked_for()
    {
        var statuses = (await GetJsonAsync("/api/meta/order-statuses"))
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToList();

        foreach (var expected in new[] { "pending", "confirmed", "processing", "ready", "completed" })
            Assert.Contains(expected, statuses);
    }

    [Fact]
    public async Task Moving_an_order_on_records_a_timeline_the_customer_can_read()
    {
        var (_, created) = await PostAsync("/api/orders", new
        {
            name = "Arun Prakash",
            phone = "9876543210",
            fulfilment = "pickup",
            payment = "upi",
            items = new[] { new { id = "p-007", qty = 1 } },
        });

        var orderId = created.GetProperty("orderId").GetString();

        await PatchAdminAsync($"/api/admin/orders/{orderId}/status",
            new { status = "confirmed" });
        await PatchAdminAsync($"/api/admin/orders/{orderId}/status",
            new { status = "ready", note = "Waiting at the counter" });

        var tracked = await GetJsonAsync($"/api/orders/{orderId}/track?phone=9876543210");
        var history = tracked.GetProperty("history").EnumerateArray().ToList();

        Assert.Equal(["pending", "confirmed", "ready"],
            history.Select(e => e.GetProperty("status").GetString()).ToList());

        Assert.Equal("Waiting at the counter", history[^1].GetProperty("note").GetString());

        // A pickup says "ready to collect", never "out for delivery".
        Assert.Equal("Ready to collect", tracked.GetProperty("statusLabel").GetString());
    }

    [Fact]
    public async Task Setting_the_same_status_twice_does_not_stutter_the_timeline()
    {
        var (_, created) = await PostAsync("/api/orders", new
        {
            name = "Arun Prakash",
            phone = "9876543210",
            fulfilment = "pickup",
            payment = "upi",
            items = new[] { new { id = "p-008", qty = 1 } },
        });

        var orderId = created.GetProperty("orderId").GetString();

        await PatchAdminAsync($"/api/admin/orders/{orderId}/status", new { status = "confirmed" });
        await PatchAdminAsync($"/api/admin/orders/{orderId}/status", new { status = "confirmed" });

        var tracked = await GetJsonAsync($"/api/orders/{orderId}/track?phone=9876543210");

        Assert.Equal(2, tracked.GetProperty("history").GetArrayLength());
    }

    /* ---------------------------------------------------------------------- */
    /* 11. Enquiry management                                                  */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task A_bulk_enquiry_reaches_the_admin_list_and_can_be_moved_on()
    {
        var (created, enquiry) = await PostAsync("/api/bulk-enquiries", new
        {
            name = "Lakshmi Narayanan",
            organisation = "Sri Meenakshi Trust",
            phone = "9840012345",
            email = "trust@example.in",
            district = "Madurai",
            quantity = "400 boxes",
            message = "Temple festival, first week of November.",
        });

        Assert.Equal(HttpStatusCode.Created, created);

        var enquiryId = enquiry.GetProperty("enquiryId").GetString();
        Assert.StartsWith("BQ", enquiryId);

        var list = await GetAdminJsonAsync("/api/admin/enquiries?pageSize=200");
        var ids = list.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("enquiryId").GetString())
            .ToList();

        Assert.Contains(enquiryId, ids);

        var (status, quoted) = await PatchAdminAsync(
            $"/api/admin/enquiries/{enquiryId}/status", new { status = "quoted" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("quoted", quoted.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_enquiry_book_is_closed_without_the_passcode()
    {
        var response = await Client.GetAsync("/api/admin/enquiries");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /* ---------------------------------------------------------------------- */
    /* 13. Analytics                                                           */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Events_are_recorded_and_show_up_in_the_admin_report()
    {
        var session = $"test-{Guid.NewGuid():n}";

        var (status, accepted) = await PostAsync("/api/analytics/events", new
        {
            events = new object[]
            {
                new { type = "product_view", @ref = "p-001", label = "Kuruvi", session },
                new { type = "cart_add", @ref = "p-001", value = 2, session },
                new { type = "checkout_start", session },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(3, accepted.GetProperty("accepted").GetInt32());

        var report = await GetAdminJsonAsync("/api/admin/analytics?days=1");

        Assert.True(report.GetProperty("funnel").GetProperty("productViews").GetInt32() >= 1);
        Assert.True(report.GetProperty("funnel").GetProperty("cartAdds").GetInt32() >= 1);
        Assert.True(report.GetProperty("totalEvents").GetInt32() >= 3);
    }

    [Fact]
    public async Task An_unknown_event_type_is_dropped_without_failing_the_batch()
    {
        var (status, body) = await PostAsync("/api/analytics/events", new
        {
            events = new object[]
            {
                new { type = "product_view", @ref = "p-002" },
                new { type = "mining_bitcoin", @ref = "p-002" },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Contains("mining_bitcoin",
            body.GetProperty("rejected").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task The_funnel_counts_visits_rather_than_events()
    {
        var session = $"funnel-{Guid.NewGuid():n}";

        var before = await GetAdminJsonAsync("/api/admin/analytics?days=1");
        var baseline = before.GetProperty("funnel").GetProperty("productViews").GetInt32();

        // One visit looking at the same product four times is one visit that
        // looked — not four. Counting events here would let a single person
        // refreshing a page outvote four people who each looked once, and every
        // percentage below it would be wrong in the same direction.
        await PostAsync("/api/analytics/events", new
        {
            events = new object[]
            {
                new { type = "product_view", @ref = "p-001", session },
                new { type = "product_view", @ref = "p-001", session },
                new { type = "product_view", @ref = "p-002", session },
                new { type = "product_view", @ref = "p-003", session },
            },
        });

        var after = await GetAdminJsonAsync("/api/admin/analytics?days=1");
        var funnel = after.GetProperty("funnel");

        Assert.Equal(baseline + 1, funnel.GetProperty("productViews").GetInt32());

        // The raw event total is still available for the tile that wants it.
        Assert.True(funnel.GetProperty("productViewEvents").GetInt32() >= 4);
    }

    [Fact]
    public async Task An_event_without_a_session_is_recorded_but_not_counted_as_a_visit()
    {
        var before = await GetAdminJsonAsync("/api/admin/analytics?days=1");
        var baseline = before.GetProperty("funnel").GetProperty("sessions").GetInt32();

        // Storage disabled in the browser, or an event the API recorded itself.
        // Attributing those to a visit each would inflate every stage.
        await PostAsync("/api/analytics/events", new
        {
            events = new object[] { new { type = "page_view", @ref = "/offers" } },
        });

        var after = await GetAdminJsonAsync("/api/admin/analytics?days=1");

        Assert.Equal(baseline, after.GetProperty("funnel").GetProperty("sessions").GetInt32());
        Assert.True(after.GetProperty("totalEvents").GetInt32() > before.GetProperty("totalEvents").GetInt32());
    }

    [Fact]
    public async Task An_order_is_counted_once_and_attributed_to_the_visit_that_placed_it()
    {
        var session = $"order-{Guid.NewGuid():n}";

        // The visit that led to the order.
        await PostAsync("/api/analytics/events", new
        {
            events = new object[]
            {
                new { type = "product_view", @ref = "p-001", session },
                new { type = "cart_add", @ref = "p-001", value = 1, session },
                new { type = "checkout_start", session },
            },
        });

        var before = await GetAdminJsonAsync("/api/admin/analytics?days=1");
        var ordersBefore = before.GetProperty("funnel").GetProperty("orders").GetInt32();

        var (status, _) = await PostAsync("/api/orders", new
        {
            name = "Session Test",
            phone = "9842011994",
            fulfilment = "pickup",
            payment = "upi",
            items = new[] { new { id = "p-001", qty = 1 } },
            session,
        });

        Assert.Equal(HttpStatusCode.Created, status);

        var after = await GetAdminJsonAsync("/api/admin/analytics?days=1");
        var funnel = after.GetProperty("funnel");

        // Exactly one more visit reached the last step — the API records the
        // event itself and the storefront does not also record its own, which
        // would double every order and every rupee of revenue in the report.
        Assert.Equal(ordersBefore + 1, funnel.GetProperty("orders").GetInt32());

        var byType = after.GetProperty("byType").EnumerateArray()
            .First(t => t.GetProperty("ref").GetString() == "order_placed");

        var eventsBefore = before.GetProperty("byType").EnumerateArray()
            .Where(t => t.GetProperty("ref").GetString() == "order_placed")
            .Select(t => t.GetProperty("count").GetInt32())
            .FirstOrDefault();

        Assert.Equal(eventsBefore + 1, byType.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task The_analytics_report_is_closed_without_the_passcode()
    {
        var response = await Client.GetAsync("/api/admin/analytics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_search_that_found_nothing_is_reported_separately()
    {
        await PostAsync("/api/analytics/events", new
        {
            events = new object[]
            {
                new { type = "search", @ref = "sky lantern", value = 0 },
            },
        });

        var report = await GetAdminJsonAsync("/api/admin/analytics?days=1");
        var barren = report.GetProperty("searchesWithNoResults").EnumerateArray()
            .Select(r => r.GetProperty("ref").GetString())
            .ToList();

        Assert.Contains("sky lantern", barren);
    }

    /* ---------------------------------------------------------------------- */
    /* 12. The dashboard the shop actually reads                               */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task The_summary_counts_the_things_the_dashboard_puts_on_screen()
    {
        var summary = await GetAdminJsonAsync("/api/admin/summary");

        foreach (var field in new[]
                 {
                     "products", "categories", "orders", "revenue", "pendingOrders",
                     "outOfStock", "lowStock", "unavailable", "enquiries",
                     "openEnquiries", "ordersThisWeek", "revenueThisWeek",
                 })
        {
            Assert.True(summary.TryGetProperty(field, out var value), $"summary is missing '{field}'");
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
        }
    }
}
