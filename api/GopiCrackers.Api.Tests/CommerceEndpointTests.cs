using System.Net;
using System.Text.Json;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Cart pricing, coupons, orders and the three write forms. The money assertions
/// are computed independently of the API so a wrong total cannot pass.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CommerceEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static object Line(string id, int qty) => new { id, qty };

    private static object ValidOrder(object[] items, string payment = "upi", string? coupon = null) => new
    {
        name = "Meenakshi Raghavan",
        phone = "9842011994",
        email = "meenakshi@example.in",
        address = "12 Second Cross Street, Adyar",
        city = "Chennai",
        district = "Chennai",
        pincode = "600020",
        payment,
        notes = "Please call before delivery",
        items,
        coupon,
    };

    /* ---------------------------------------------------------------------- */
    /* Cart quote                                                              */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Quote_prices_a_basket_from_the_catalogue()
    {
        var product = await CheapestProductAsync();
        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line(product.Id, 2) },
        });

        Assert.Equal(HttpStatusCode.OK, status);

        var totals = body.GetProperty("totals");
        Assert.Equal(product.Price * 2, totals.GetProperty("subtotal").GetInt32());
        Assert.Equal(product.Mrp * 2, totals.GetProperty("mrpTotal").GetInt32());
        Assert.Equal((product.Mrp - product.Price) * 2, totals.GetProperty("catalogueSavings").GetInt32());
        Assert.Equal(2, totals.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Quote_ignores_any_price_the_client_sends()
    {
        var product = await CheapestProductAsync();
        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { new { id = product.Id, qty = 1, price = 1, mrp = 1 } },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(product.Price, body.GetProperty("totals").GetProperty("subtotal").GetInt32());
    }

    [Fact]
    public async Task Quote_charges_delivery_below_the_free_threshold()
    {
        var product = await CheapestProductAsync();
        var (_, body) = await PostAsync("/api/cart/quote", new { items = new[] { Line(product.Id, 1) } });
        var totals = body.GetProperty("totals");

        Assert.Equal(product.Price, totals.GetProperty("subtotal").GetInt32());
        Assert.Equal(149, totals.GetProperty("shipping").GetInt32());
        Assert.Equal(product.Price + 149, totals.GetProperty("total").GetInt32());
        Assert.Equal(2000 - product.Price, totals.GetProperty("freeShippingGap").GetInt32());
    }

    [Fact]
    public async Task Quote_gives_free_delivery_above_the_threshold()
    {
        var combo = await ComboOverFreeDeliveryAsync();
        var (_, body) = await PostAsync("/api/cart/quote", new { items = new[] { Line(combo.Id, 1) } });
        var totals = body.GetProperty("totals");

        Assert.Equal(combo.Price, totals.GetProperty("subtotal").GetInt32());
        Assert.Equal(0, totals.GetProperty("shipping").GetInt32());
        Assert.Equal(combo.Price, totals.GetProperty("total").GetInt32());
        Assert.Equal(0, totals.GetProperty("freeShippingGap").GetInt32());
    }

    [Fact]
    public async Task Quote_accepts_products_and_combos_in_the_same_basket()
    {
        var product = await CheapestProductAsync();
        var combo = (await CatalogueAsync()).Combos[1];

        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line(product.Id, 1), Line(combo.Id, 1) },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var kinds = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()).ToList();

        Assert.Contains("product", kinds);
        Assert.Contains("combo", kinds);
        Assert.Equal(product.Price + combo.Price,
            body.GetProperty("totals").GetProperty("subtotal").GetInt32());
    }

    [Fact]
    public async Task Quote_resolves_slugs_as_well_as_ids()
    {
        var product = await CheapestProductAsync();
        var (_, byId) = await PostAsync("/api/cart/quote", new { items = new[] { Line(product.Id, 1) } });
        var (_, bySlug) = await PostAsync("/api/cart/quote",
            new { items = new[] { Line(product.Slug, 1) } });

        Assert.Equal(
            byId.GetProperty("totals").GetProperty("total").GetInt32(),
            bySlug.GetProperty("totals").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Quote_merges_duplicate_lines_rather_than_pricing_twice()
    {
        var product = await CheapestProductAsync();
        var (_, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line(product.Id, 1), Line(product.Id, 2) },
        });

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(3, items[0].GetProperty("qty").GetInt32());
        Assert.Equal(product.Price * 3, body.GetProperty("totals").GetProperty("subtotal").GetInt32());
    }

    [Fact]
    public async Task Quote_caps_a_quantity_at_available_stock_and_says_so()
    {
        var product = await GetAsync<Product>("/api/products/p-001");

        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line("p-001", product.Stock + 500) },
        });

        Assert.Equal(HttpStatusCode.OK, status);

        var line = body.GetProperty("items")[0];
        Assert.Equal(product.Stock, line.GetProperty("qty").GetInt32());
        Assert.True(line.GetProperty("capped").GetBoolean());
        Assert.NotEmpty(body.GetProperty("notices").EnumerateArray());
    }

    [Fact]
    public async Task Quote_rejects_an_unknown_id_with_a_validation_problem()
    {
        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line("p-001", 1), Line("p-999", 1) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("p-999", body.ToString());
    }

    [Fact]
    public async Task Quote_rejects_an_empty_basket()
    {
        var (status, _) = await PostAsync("/api/cart/quote", new { items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Quote_rejects_a_zero_quantity()
    {
        var (status, _) = await PostAsync("/api/cart/quote", new { items = new[] { Line("p-001", 0) } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /* ---------------------------------------------------------------------- */
    /* Coupons                                                                 */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Every offer the shop advertises has to be a code the checkout will take.
    ///
    /// These were two separate lists once — <c>offers.json</c> for the offers
    /// page and a dictionary in configuration for the checkout — so an offer
    /// created in the admin was printed on the shop and then refused at the
    /// till, and FREESHIP had drifted into exactly that state. The catalogue is
    /// now the source, and this is the guard.
    /// </summary>
    [Fact]
    public async Task Every_advertised_offer_is_a_code_the_checkout_accepts()
    {
        var offers = (await CatalogueAsync()).Offers;
        var codes = (await GetAsync<List<AppliedCoupon>>("/api/coupons"))
            .Select(c => c.Code)
            .ToList();

        Assert.NotEmpty(offers);
        foreach (var offer in offers)
            Assert.Contains(offer.Code, codes);
    }

    /// <summary>
    /// Configuration overrides what an offer advertises, for the one case that
    /// needs it: DIWALI75 names the 75% already inside every catalogue price,
    /// so redeeming it must not take that off a second time.
    /// </summary>
    [Fact]
    public async Task A_configured_rule_overrides_what_the_offer_advertises()
    {
        var advertised = (await CatalogueAsync()).Offers.Single(o => o.Code == "DIWALI75");
        Assert.Equal(75, advertised.Value);

        var (status, body) = await PostAsync("/api/coupons/validate",
            new { code = "DIWALI75", subtotal = 5000 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(0, body.GetProperty("discount").GetInt32());
    }

    /// <summary>A shipping coupon waives the delivery fee, not part of the goods.</summary>
    [Fact]
    public async Task A_shipping_coupon_waives_the_delivery_fee()
    {
        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line("p-002", 100) },
            coupon = "FREESHIP",
            fulfilment = "delivery",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var totals = body.GetProperty("totals");

        Assert.Equal(0, totals.GetProperty("shipping").GetInt32());
        // Nothing comes off the goods — only the delivery line.
        Assert.Equal(0, totals.GetProperty("couponDiscount").GetInt32());
        Assert.Equal(totals.GetProperty("subtotal").GetInt32(), totals.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Valid_coupon_reports_its_discount()
    {
        var (status, body) = await PostAsync("/api/coupons/validate",
            new { code = "EARLYBIRD", subtotal = 2000 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(200, body.GetProperty("discount").GetInt32());
    }

    [Fact]
    public async Task Coupon_below_its_minimum_explains_the_shortfall()
    {
        var (status, body) = await PostAsync("/api/coupons/validate",
            new { code = "EARLYBIRD", subtotal = 500 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("1,000", body.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Unknown_coupon_is_reported_not_thrown()
    {
        var (status, body) = await PostAsync("/api/coupons/validate",
            new { code = "NOTACODE", subtotal = 5000 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("not a valid code", body.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Coupon_codes_are_case_and_whitespace_insensitive()
    {
        var (_, body) = await PostAsync("/api/coupons/validate",
            new { code = "  earlybird  ", subtotal = 2000 });

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("EARLYBIRD", body.GetProperty("coupon").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Coupon_validation_requires_a_code()
    {
        var (status, _) = await PostAsync("/api/coupons/validate", new { code = "", subtotal = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Percentage_coupon_applies_to_a_quote_and_can_unlock_free_delivery()
    {
        var combo = await ComboOverFreeDeliveryAsync();
        var subtotal = combo.Price * 2;

        // BULK20 needs ₹25,000, which two combos will not reach, so it is
        // reported as not applicable rather than applied.
        var (_, tooSmall) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line(combo.Id, 2) },
            coupon = "BULK20",
        });

        Assert.Equal(0, tooSmall.GetProperty("totals").GetProperty("couponDiscount").GetInt32());
        Assert.False(tooSmall.TryGetProperty("coupon", out var absent) && absent.ValueKind != JsonValueKind.Null);
        Assert.NotEmpty(tooSmall.GetProperty("notices").EnumerateArray());

        // EARLYBIRD needs ₹1,500 and does apply, at 10% rounded half-up.
        var (_, applied) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line(combo.Id, 2) },
            coupon = "EARLYBIRD",
        });

        var expectedDiscount = (int)Math.Round(subtotal * 0.1, MidpointRounding.AwayFromZero);
        var totals = applied.GetProperty("totals");
        Assert.Equal(subtotal, totals.GetProperty("subtotal").GetInt32());
        Assert.Equal(expectedDiscount, totals.GetProperty("couponDiscount").GetInt32());
        Assert.Equal(subtotal - expectedDiscount, totals.GetProperty("total").GetInt32());
        Assert.Equal("EARLYBIRD", applied.GetProperty("coupon").GetProperty("code").GetString());
    }

    /// <summary>
    /// A flat coupon can push a cart back under the free-delivery line, and the
    /// delivery fee then reappears.
    ///
    /// Shipping is deliberately assessed on the post-coupon figure, matching
    /// `selectTotals` in cartStore.js, so the API and the client cart agree.
    /// </summary>
    [Fact]
    public async Task Flat_coupon_can_push_a_cart_back_below_free_delivery()
    {
        // A combo that clears ₹2,000 on its own but not after ₹500 comes off.
        var combo = (await CatalogueAsync()).Combos
            .First(c => c.Price > 2000 && c.Price - 500 < 2000);

        var (_, body) = await PostAsync("/api/cart/quote", new
        {
            items = new[] { Line(combo.Id, 1) },
            coupon = "COMBO500",                    // ₹500 off, min ₹1,899
        });

        var afterCoupon = combo.Price - 500;
        var totals = body.GetProperty("totals");
        Assert.Equal(combo.Price, totals.GetProperty("subtotal").GetInt32());
        Assert.Equal(500, totals.GetProperty("couponDiscount").GetInt32());
        Assert.Equal(149, totals.GetProperty("shipping").GetInt32());
        Assert.Equal(afterCoupon + 149, totals.GetProperty("total").GetInt32());
        Assert.Equal(2000 - afterCoupon, totals.GetProperty("freeShippingGap").GetInt32());
    }

    [Fact]
    public async Task Totals_match_an_independent_calculation()
    {
        var products = await GetAsync<PagedResult<Product>>("/api/products?pageSize=100");
        var picks = products.Items.Take(5).ToList();
        var quantities = new[] { 1, 2, 3, 1, 2 };

        var (status, body) = await PostAsync("/api/cart/quote", new
        {
            items = picks.Select((p, i) => Line(p.Id, quantities[i])).ToArray(),
            coupon = "EARLYBIRD",
        });

        Assert.Equal(HttpStatusCode.OK, status);

        // Recomputed here from the product endpoints, not read back from the quote.
        var cappedQty = picks.Select((p, i) => Math.Min(quantities[i], p.Stock)).ToList();
        var subtotal = picks.Select((p, i) => p.Price * cappedQty[i]).Sum();
        var mrpTotal = picks.Select((p, i) => p.Mrp * cappedQty[i]).Sum();
        var couponDiscount = subtotal >= 1500
            ? (int)Math.Round(subtotal * 10 / 100.0, MidpointRounding.AwayFromZero)
            : 0;
        var afterCoupon = subtotal - couponDiscount;
        var shipping = afterCoupon == 0 || afterCoupon >= 2000 ? 0 : 149;

        var totals = body.GetProperty("totals");
        Assert.Equal(subtotal, totals.GetProperty("subtotal").GetInt32());
        Assert.Equal(mrpTotal, totals.GetProperty("mrpTotal").GetInt32());
        Assert.Equal(mrpTotal - subtotal, totals.GetProperty("catalogueSavings").GetInt32());
        Assert.Equal(couponDiscount, totals.GetProperty("couponDiscount").GetInt32());
        Assert.Equal(shipping, totals.GetProperty("shipping").GetInt32());
        Assert.Equal(afterCoupon + shipping, totals.GetProperty("total").GetInt32());
        Assert.Equal(mrpTotal - subtotal + couponDiscount, totals.GetProperty("totalSavings").GetInt32());
        Assert.Equal(cappedQty.Sum(), totals.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Shipping_rules_are_published()
    {
        var body = await GetJsonAsync("/api/shipping");
        Assert.Equal(2000, body.GetProperty("freeAbove").GetInt32());
        Assert.Equal(149, body.GetProperty("localFee").GetInt32());
        Assert.Equal(249, body.GetProperty("outstationFee").GetInt32());
    }

    /* ---------------------------------------------------------------------- */
    /* Orders                                                                  */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Order_is_created_and_can_be_fetched_back()
    {
        var product = await CheapestProductAsync();
        var combo = (await CatalogueAsync()).Combos[2];

        var (status, body) = await PostAsync("/api/orders",
            ValidOrder([Line(product.Id, 2), Line(combo.Id, 1)]));

        Assert.Equal(HttpStatusCode.Created, status);

        var orderId = body.GetProperty("orderId").GetString()!;
        Assert.StartsWith("AC", orderId);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(product.Price * 2 + combo.Price,
            body.GetProperty("totals").GetProperty("subtotal").GetInt32());

        var fetched = await GetJsonAsync($"/api/orders/{orderId}/track?phone=9842011994");
        Assert.Equal(orderId, fetched.GetProperty("orderId").GetString());
        Assert.Equal("Meenakshi Raghavan", fetched.GetProperty("name").GetString());
        Assert.Equal("Order received", fetched.GetProperty("statusLabel").GetString());
    }

    [Fact]
    public async Task Tracking_hides_the_order_from_anyone_without_the_phone_number()
    {
        var (_, created) = await PostAsync("/api/orders", ValidOrder([Line("p-002", 1)]));
        var orderId = created.GetProperty("orderId").GetString();

        // A wrong number and an unknown reference must be indistinguishable,
        // or the difference becomes an oracle for enumerating the order book.
        var wrongNumber = await Client.GetAsync($"/api/orders/{orderId}/track?phone=9000000000");
        var unknownOrder = await Client.GetAsync("/api/orders/AC00000000/track?phone=9842011994");

        Assert.Equal(HttpStatusCode.NotFound, wrongNumber.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownOrder.StatusCode);
    }

    [Fact]
    public async Task Tracking_masks_the_phone_number_and_omits_the_address()
    {
        var (_, created) = await PostAsync("/api/orders", ValidOrder([Line("p-002", 1)]));
        var orderId = created.GetProperty("orderId").GetString();

        var tracked = await GetJsonAsync($"/api/orders/{orderId}/track?phone=9842011994");

        // The last four digits only — enough to recognise your own order, not
        // enough to read a phone number off somebody else's.
        Assert.EndsWith("1994", tracked.GetProperty("maskedPhone").GetString());
        Assert.DoesNotContain("9842011994", tracked.GetProperty("maskedPhone").GetString());
        Assert.False(tracked.TryGetProperty("address", out _));
        Assert.False(tracked.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Pickup_order_needs_no_address_and_is_charged_no_delivery()
    {
        var (status, body) = await PostAsync("/api/orders", new
        {
            name = "Meenakshi Raghavan",
            phone = "9842011994",
            fulfilment = "pickup",
            payment = "upi",
            items = new[] { Line("p-001", 1) },
        });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("pickup", body.GetProperty("fulfilment").GetString());
        Assert.Equal(0, body.GetProperty("totals").GetProperty("shipping").GetInt32());
    }

    [Fact]
    public async Task Delivery_order_without_an_address_is_refused()
    {
        var (status, body) = await PostAsync("/api/orders", new
        {
            name = "Meenakshi Raghavan",
            phone = "9842011994",
            fulfilment = "delivery",
            payment = "upi",
            items = new[] { Line("p-001", 1) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var errors = body.GetProperty("errors").ToString();
        Assert.Contains("delivering", errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Order_records_its_own_first_timeline_entry()
    {
        var (_, created) = await PostAsync("/api/orders", ValidOrder([Line("p-003", 1)]));
        var orderId = created.GetProperty("orderId").GetString();

        var tracked = await GetJsonAsync($"/api/orders/{orderId}/track?phone=9842011994");
        var history = tracked.GetProperty("history").EnumerateArray().ToList();

        Assert.Single(history);
        Assert.Equal("pending", history[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Order_response_carries_a_delivery_window_that_skips_sundays()
    {
        var (_, body) = await PostAsync("/api/orders", ValidOrder([Line("cmb-01", 1)]));

        var from = body.GetProperty("deliveryFrom").GetDateTime();
        var to = body.GetProperty("deliveryTo").GetDateTime();

        Assert.True(from > DateTime.UtcNow.Date);
        Assert.True(to > from);
        Assert.NotEqual(DayOfWeek.Sunday, from.DayOfWeek);
        Assert.NotEqual(DayOfWeek.Sunday, to.DayOfWeek);
    }

    [Fact]
    public async Task Order_stores_no_payment_credentials_only_a_method()
    {
        var (_, body) = await PostAsync("/api/orders", ValidOrder([Line("p-001", 1)], payment: "card"));

        Assert.Equal("card", body.GetProperty("payment").GetString());
        foreach (var forbidden in new[] { "cardNumber", "cvv", "upiId", "accountNumber", "expiry" })
            Assert.False(body.TryGetProperty(forbidden, out _), $"order leaked '{forbidden}'");
    }

    [Fact]
    public async Task Order_reprices_server_side_and_ignores_a_client_total()
    {
        var product = await CheapestProductAsync();
        var payload = new
        {
            name = "Arun Prakash",
            phone = "9842011995",
            address = "4 Race Course Road",
            city = "Coimbatore",
            district = "Coimbatore",
            pincode = "641018",
            payment = "upi",
            items = new[] { Line(product.Id, 1) },
            totals = new { subtotal = 1, total = 1 },   // ignored
        };

        var (status, body) = await PostAsync("/api/orders", payload);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(product.Price, body.GetProperty("totals").GetProperty("subtotal").GetInt32());
        Assert.Equal(product.Price + 149, body.GetProperty("totals").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Order_applies_a_coupon()
    {
        var (status, body) = await PostAsync("/api/orders",
            ValidOrder([Line("cmb-01", 1)], coupon: "COMBO500"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("COMBO500", body.GetProperty("coupon").GetProperty("code").GetString());
        Assert.Equal(500, body.GetProperty("totals").GetProperty("couponDiscount").GetInt32());
    }

    [Theory]
    [InlineData("phone", "12345")]
    [InlineData("pincode", "12")]
    [InlineData("name", "")]
    [InlineData("email", "not-an-email")]
    [InlineData("payment", "bitcoin")]
    public async Task Order_validation_rejects_bad_input(string field, string value)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = "Divya Narayanan",
            ["phone"] = "9842011996",
            ["email"] = "divya@example.in",
            ["address"] = "22 North Veli Street",
            ["city"] = "Madurai",
            ["district"] = "Madurai",
            ["pincode"] = "625001",
            ["payment"] = "upi",
            ["items"] = new[] { Line("p-001", 1) },
            [field] = value,
        };

        var (status, body) = await PostAsync("/api/orders", payload);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.TryGetProperty("errors", out _), $"expected a validation problem, got: {body}");
    }

    [Fact]
    public async Task Cash_on_delivery_is_capped_at_five_thousand()
    {
        var under = await PostAsync("/api/orders", ValidOrder([Line("cmb-05", 1)], payment: "cod"));
        Assert.Equal(HttpStatusCode.Created, under.Status);

        // cmb-06 is ₹12,999 — over the COD ceiling the FAQ states.
        var over = await PostAsync("/api/orders", ValidOrder([Line("cmb-06", 1)], payment: "cod"));
        Assert.Equal(HttpStatusCode.BadRequest, over.Status);
        Assert.Contains("5,000", over.Body.ToString());
    }

    [Fact]
    public async Task Order_with_an_unknown_item_is_rejected()
    {
        var (status, body) = await PostAsync("/api/orders", ValidOrder([Line("p-000", 1)]));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("p-000", body.ToString());
    }

    [Fact]
    public async Task Unknown_order_is_404() =>
        await AssertStatusAsync("/api/orders/AC00000000", HttpStatusCode.NotFound);

    [Fact]
    public async Task The_order_book_is_not_readable_without_the_admin_passcode()
    {
        var (_, created) = await PostAsync("/api/orders", ValidOrder([Line("p-002", 1)]));
        var orderId = created.GetProperty("orderId").GetString();

        // There is deliberately no anonymous "recent orders" route: it listed
        // every customer's name, phone number and address to anybody who asked.
        var anonymous = await Client.GetAsync("/api/admin/orders?pageSize=200");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var book = await GetAdminJsonAsync("/api/admin/orders?pageSize=200");
        var ids = book.GetProperty("items").EnumerateArray()
            .Select(o => o.GetProperty("orderId").GetString())
            .ToList();

        Assert.Contains(orderId, ids);
    }

    /* ---------------------------------------------------------------------- */
    /* Bulk enquiries, contact, newsletter                                     */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Bulk_enquiry_is_recorded_and_retrievable()
    {
        var (status, body) = await PostAsync("/api/bulk-enquiries", new
        {
            name = "Senthil Kumar",
            organisation = "Tiruppur Knitwear Ltd",
            phone = "9842011997",
            email = "senthil@example.in",
            district = "Tiruppur",
            budget = "₹1,00,000+",
            quantity = "180 boxes",
            message = "Gift boxes for factory staff, GST invoice needed.",
        });

        Assert.Equal(HttpStatusCode.Created, status);

        var id = body.GetProperty("enquiryId").GetString()!;
        Assert.StartsWith("BQ", id);
        Assert.Equal("received", body.GetProperty("status").GetString());

        var fetched = await GetJsonAsync($"/api/bulk-enquiries/{id}");
        Assert.Equal("Tiruppur", fetched.GetProperty("district").GetString());
    }

    [Fact]
    public async Task Bulk_enquiry_rejects_an_unlisted_district()
    {
        var (status, body) = await PostAsync("/api/bulk-enquiries", new
        {
            name = "Test Person",
            phone = "9842011998",
            district = "Atlantis",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("district", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bulk_enquiry_rejects_a_bad_phone_number()
    {
        var (status, _) = await PostAsync("/api/bulk-enquiries", new
        {
            name = "Test Person",
            phone = "12345",
            district = "Chennai",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Unknown_enquiry_is_404() =>
        await AssertStatusAsync("/api/bulk-enquiries/BQ00000000", HttpStatusCode.NotFound);

    [Fact]
    public async Task Contact_message_is_recorded_and_retrievable()
    {
        var (status, body) = await PostAsync("/api/contact-messages", new
        {
            name = "Fathima Beevi",
            phone = "9842011999",
            email = "fathima@example.in",
            subject = "Silent range",
            message = "Do the smoke sticks work indoors on a covered balcony?",
        });

        Assert.Equal(HttpStatusCode.Created, status);

        var id = body.GetProperty("messageId").GetString()!;
        Assert.StartsWith("MSG", id);

        var fetched = await GetJsonAsync($"/api/contact-messages/{id}");
        Assert.Equal("Silent range", fetched.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Contact_message_rejects_a_one_word_message()
    {
        var (status, _) = await PostAsync("/api/contact-messages", new
        {
            name = "Test Person",
            phone = "9842011999",
            message = "hi",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Unknown_message_is_404() =>
        await AssertStatusAsync("/api/contact-messages/MSG00000000", HttpStatusCode.NotFound);

    [Fact]
    public async Task Newsletter_subscribe_is_idempotent()
    {
        var email = $"reader-{Guid.NewGuid():N}@example.in";

        var first = await PostAsync("/api/newsletter/subscribe", new { email });
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.False(first.Body.GetProperty("alreadySubscribed").GetBoolean());

        var second = await PostAsync("/api/newsletter/subscribe", new { email });
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(second.Body.GetProperty("alreadySubscribed").GetBoolean());

        // The original timestamp survives the second attempt.
        Assert.Equal(
            first.Body.GetProperty("subscribedAt").GetDateTimeOffset(),
            second.Body.GetProperty("subscribedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Newsletter_rejects_a_malformed_address()
    {
        var (status, _) = await PostAsync("/api/newsletter/subscribe", new { email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /* ---------------------------------------------------------------------- */
    /* Protocol behaviour                                                      */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Malformed_json_is_a_400_not_a_500()
    {
        var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        var response = await Client.PostAsync("/api/cart/quote", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_verb_is_a_405()
    {
        var response = await Client.PostAsync("/api/products", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_is_a_404() =>
        await AssertStatusAsync("/api/does-not-exist", HttpStatusCode.NotFound);

    [Fact]
    public async Task Cors_allows_the_vite_dev_server()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/products");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await Client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "preflight did not return an Access-Control-Allow-Origin header");
    }

    [Fact]
    public async Task Responses_are_json()
    {
        var response = await Client.GetAsync("/api/products/p-001");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
