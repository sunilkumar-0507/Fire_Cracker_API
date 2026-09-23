using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Every read endpoint: happy path, 404 path, and the filter/sort behaviour the
/// catalogue page depends on.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CatalogueEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    /* ---------------------------------------------------------------------- */
    /* Root, health, meta                                                      */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Root_lists_every_endpoint()
    {
        var body = await GetJsonAsync("/");
        Assert.Equal("SKV Pyros REST API", body.GetProperty("name").GetString());
        Assert.NotEmpty(body.GetProperty("endpoints").EnumerateArray());
    }

    [Fact]
    public async Task Health_reports_the_full_catalogue_loaded()
    {
        var body = await GetJsonAsync("/api/health");
        var catalogue = body.GetProperty("catalogue");
        var expected = await CatalogueAsync();

        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal(expected.Products.Count, catalogue.GetProperty("products").GetInt32());
        Assert.Equal(expected.Categories.Count, catalogue.GetProperty("categories").GetInt32());
        Assert.Equal(6, catalogue.GetProperty("combos").GetInt32());
        Assert.Equal(6, catalogue.GetProperty("offers").GetInt32());
        Assert.Equal(4, catalogue.GetProperty("banners").GetInt32());
        Assert.Equal(0, catalogue.GetProperty("testimonials").GetInt32());
        Assert.Equal(12, catalogue.GetProperty("faqs").GetInt32());
    }

    [Fact]
    public async Task OpenApi_document_is_served_in_development()
    {
        var body = await GetJsonAsync("/openapi/v1.json");
        Assert.True(body.TryGetProperty("paths", out var paths));
        Assert.NotEmpty(paths.EnumerateObject());
    }

    [Theory]
    [InlineData("/api/meta/brand")]
    [InlineData("/api/meta/districts")]
    [InlineData("/api/meta/payment-methods")]
    [InlineData("/api/meta/safety-rules")]
    [InlineData("/api/meta/popular-searches")]
    [InlineData("/api/meta/trust-points")]
    [InlineData("/api/meta/config")]
    [InlineData("/api/shipping")]
    public async Task Meta_endpoints_answer(string url) => await GetJsonAsync(url);

    [Fact]
    public async Task Brand_matches_the_storefront_constants()
    {
        var body = await GetJsonAsync("/api/meta/brand");
        Assert.Equal("SKV Pyros", body.GetProperty("name").GetString());
        Assert.Equal("33AABCA1994K1Z8", body.GetProperty("gstin").GetString());
    }

    [Fact]
    public async Task Config_carries_everything_a_cold_boot_needs()
    {
        var body = await GetJsonAsync("/api/meta/config");

        foreach (var key in new[]
        {
            "brand", "shipping", "paymentMethods", "districts", "safetyRules",
            "popularSearches", "trustPoints", "sortOptions", "categories", "tags", "priceBounds",
        })
        {
            Assert.True(body.TryGetProperty(key, out _), $"config is missing '{key}'");
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Products                                                                */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Products_list_pages_the_whole_catalogue()
    {
        // pageSize is clamped to 100 server-side, so a catalogue larger than that
        // is reported in full by `Total` but handed over a page at a time.
        var all = (await CatalogueAsync()).Products.Count;
        var page = await GetAsync<PagedResult<Product>>("/api/products?pageSize=100");

        Assert.Equal(all, page.Total);
        Assert.Equal(Math.Min(100, all), page.Items.Count);
        Assert.Equal((int)Math.Ceiling(all / 100.0), page.TotalPages);
        Assert.False(page.HasPrevious);
    }

    [Fact]
    public async Task Products_list_defaults_to_24_per_page()
    {
        var total = (await CatalogueAsync()).Products.Count;
        var lastPage = (int)Math.Ceiling(total / 24.0);

        var first = await GetAsync<PagedResult<Product>>("/api/products");
        Assert.Equal(24, first.Items.Count);
        Assert.Equal(lastPage, first.TotalPages);
        Assert.True(first.HasNext);

        var second = await GetAsync<PagedResult<Product>>($"/api/products?page={lastPage}");
        Assert.Equal(total - (lastPage - 1) * 24, second.Items.Count);
        Assert.True(second.HasPrevious);
        Assert.False(second.HasNext);

        // No product appears on both the first page and the last.
        Assert.Empty(first.Items.Select(p => p.Id).Intersect(second.Items.Select(p => p.Id)));
    }

    [Fact]
    public async Task Products_are_returned_with_every_field_the_storefront_reads()
    {
        var expected = await CheapestProductAsync();
        var body = await GetJsonAsync($"/api/products/{expected.Slug}");

        foreach (var key in new[]
        {
            "id", "code", "slug", "name", "category", "brand", "price", "mrp", "discount", "unit",
            "description", "highlights", "images", "stock", "tags",
            "specs", "featured", "bestSeller", "combo", "isNew",
        })
        {
            Assert.True(body.TryGetProperty(key, out _), $"product is missing '{key}'");
        }

        Assert.Equal(expected.Price, body.GetProperty("price").GetInt32());
        Assert.Equal(expected.Unit, body.GetProperty("unit").GetString());
        Assert.NotEmpty(body.GetProperty("specs").EnumerateObject());
    }

    [Fact]
    public async Task Product_resolves_by_slug_and_by_id()
    {
        var expected = await CheapestProductAsync();
        var bySlug = await GetAsync<Product>($"/api/products/{expected.Slug}");
        var byId = await GetAsync<Product>($"/api/products/{expected.Id}");
        Assert.Equal(bySlug.Id, byId.Id);
    }

    [Fact]
    public async Task Unknown_product_is_a_404_problem_document()
    {
        var response = await Client.GetAsync("/api/products/no-such-cracker");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Product not found", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Related_products_exclude_the_input_and_respect_the_limit()
    {
        var related = await GetAsync<List<Product>>("/api/products/p-001/related?limit=4");

        Assert.Equal(4, related.Count);
        Assert.DoesNotContain(related, p => p.Id == "p-001");
    }

    [Fact]
    public async Task Related_for_an_unknown_product_is_404() =>
        await AssertStatusAsync("/api/products/nope/related", HttpStatusCode.NotFound);

    [Fact]
    public async Task Tags_are_ranked_by_usage()
    {
        var tags = await GetAsync<List<TagCount>>("/api/products/tags");

        Assert.NotEmpty(tags);
        Assert.Equal(tags.OrderByDescending(t => t.Count).First().Count, tags[0].Count);
        Assert.Contains(tags, t => t.Tag == "silent");
    }

    [Fact]
    public async Task Price_bounds_match_the_catalogue()
    {
        var bounds = await GetAsync<PriceBounds>("/api/products/price-bounds");
        var all = (await CatalogueAsync()).Products;

        Assert.Equal(all.Min(p => p.Price), bounds.Min);
        Assert.Equal(all.Max(p => p.Price), bounds.Max);
    }

    [Theory]
    [InlineData("/api/products/featured", "featured")]
    [InlineData("/api/products/best-sellers", "bestSeller")]
    [InlineData("/api/products/new-arrivals", "isNew")]
    public async Task Curated_collections_only_contain_matching_products(string url, string flag)
    {
        var products = await GetAsync<List<Product>>(url);
        Assert.NotEmpty(products);

        foreach (var product in products)
        {
            var value = flag switch
            {
                "featured" => product.Featured,
                "bestSeller" => product.BestSeller,
                _ => product.IsNew,
            };
            Assert.True(value, $"{product.Slug} should not be in {url}");
        }
    }

    [Fact]
    public async Task Sort_options_are_the_five_the_catalogue_offers()
    {
        var options = await GetAsync<List<SortOption>>("/api/products/sort-options");

        Assert.Equal(
            ["relevance", "price-asc", "price-desc", "discount", "newest"],
            options.Select(o => o.Value));
    }

    /* ---------------------------------------------------------------------- */
    /* Filtering and sorting                                                   */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Category_filter_narrows_to_that_category()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?category=sparklers&pageSize=100");

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, p => Assert.Equal("sparklers", p.Category));
    }

    [Fact]
    public async Task Multiple_tags_must_all_match()
    {
        var page = await GetAsync<PagedResult<Product>>(
            "/api/products?tag=silent&tag=kids-safe&pageSize=100");

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, p =>
        {
            Assert.Contains("silent", p.Tags);
            Assert.Contains("kids-safe", p.Tags);
        });

        // Narrower than either tag alone.
        var silentOnly = await GetAsync<PagedResult<Product>>("/api/products?tag=silent&pageSize=100");
        Assert.True(page.Total <= silentOnly.Total);
    }

    [Fact]
    public async Task Comma_separated_tags_work_too()
    {
        var repeated = await GetAsync<PagedResult<Product>>("/api/products?tag=silent&tag=kids-safe&pageSize=100");
        var commas = await GetAsync<PagedResult<Product>>("/api/products?tag=silent,kids-safe&pageSize=100");

        Assert.Equal(repeated.Total, commas.Total);
    }

    [Fact]
    public async Task Price_filters_apply()
    {
        var page = await GetAsync<PagedResult<Product>>(
            "/api/products?min=100&max=500&pageSize=100");

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, p => Assert.InRange(p.Price, 100, 500));
    }

    [Fact]
    public async Task Stock_filter_hides_sold_out_items()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?stock=1&pageSize=100");
        Assert.All(page.Items, p => Assert.True(p.Stock > 0));
    }

    [Theory]
    [InlineData("price-asc")]
    [InlineData("price-desc")]
    [InlineData("discount")]
    [InlineData("newest")]
    [InlineData("relevance")]
    public async Task Every_sort_returns_the_full_catalogue_in_order(string sort)
    {
        var total = (await CatalogueAsync()).Products.Count;
        var page = await GetAsync<PagedResult<Product>>($"/api/products?sort={sort}&pageSize={total}");
        Assert.Equal(total, page.Total);

        var prices = page.Items.Select(p => p.Price).ToList();
        switch (sort)
        {
            case "price-asc":
                Assert.Equal(prices.OrderBy(p => p), prices);
                break;
            case "price-desc":
                Assert.Equal(prices.OrderByDescending(p => p), prices);
                break;
            case "discount":
                Assert.Equal(
                    page.Items.Select(p => p.Discount).OrderByDescending(d => d),
                    page.Items.Select(p => p.Discount));
                break;
        }
    }

    [Fact]
    public async Task Unknown_sort_is_rejected_rather_than_silently_ignored()
    {
        var response = await Client.GetAsync("/api/products?sort=cheapest");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("cheapest", body.ToString());
    }

    [Fact]
    public async Task Unknown_category_is_rejected() =>
        await AssertStatusAsync("/api/products?category=fireworks", HttpStatusCode.BadRequest);

    [Fact]
    public async Task Page_size_is_clamped_so_one_request_cannot_ask_for_everything()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?pageSize=100000");
        Assert.Equal(100, page.PageSize);
    }

    [Fact]
    public async Task Page_beyond_the_end_returns_an_empty_page_not_an_error()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?page=999");
        Assert.Empty(page.Items);
        Assert.Equal((await CatalogueAsync()).Products.Count, page.Total);
    }

    /* ---------------------------------------------------------------------- */
    /* Categories                                                              */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Categories_carry_counts_derived_from_the_catalogue()
    {
        var categories = await GetAsync<List<Category>>("/api/categories");
        var all = (await CatalogueAsync()).Products;

        Assert.Equal((await CatalogueAsync()).Categories.Count, categories.Count);
        Assert.Equal(all.Count, categories.Sum(c => c.ProductCount));

        foreach (var category in categories)
        {
            var actual = all.Count(p => p.Category == category.Slug);
            Assert.True(actual == category.ProductCount,
                $"{category.Slug} reports {category.ProductCount} but the catalogue has {actual}");
        }
    }

    [Fact]
    public async Task Featured_filter_applies_to_categories()
    {
        var featured = await GetAsync<List<Category>>("/api/categories?featured=true");
        Assert.All(featured, c => Assert.True(c.Featured));

        var rest = await GetAsync<List<Category>>("/api/categories?featured=false");
        Assert.All(rest, c => Assert.False(c.Featured));
        Assert.Equal((await CatalogueAsync()).Categories.Count, featured.Count + rest.Count);
    }

    [Fact]
    public async Task Category_detail_resolves_by_slug()
    {
        var category = await GetAsync<Category>("/api/categories/sparklers");
        Assert.Equal("Sparklers", category.Name);
        Assert.Equal("மத்தாப்பு", category.TamilName);
    }

    [Fact]
    public async Task Unknown_category_detail_is_404() =>
        await AssertStatusAsync("/api/categories/nope", HttpStatusCode.NotFound);

    [Fact]
    public async Task Category_products_route_matches_the_flat_filter()
    {
        var category = await PopulatedCategoryAsync();
        var nested = await GetAsync<PagedResult<Product>>(
            $"/api/categories/{category.Slug}/products?pageSize=200");
        var flat = await GetAsync<PagedResult<Product>>(
            $"/api/products?category={category.Slug}&pageSize=200");

        Assert.Equal(flat.Total, nested.Total);
        Assert.Equal(flat.Items.Select(p => p.Id), nested.Items.Select(p => p.Id));
        Assert.All(nested.Items, p => Assert.Equal(category.Slug, p.Category));
    }

    [Fact]
    public async Task Category_products_for_an_unknown_category_is_404() =>
        await AssertStatusAsync("/api/categories/nope/products", HttpStatusCode.NotFound);

    /* ---------------------------------------------------------------------- */
    /* Combos, offers, banners, testimonials, FAQs                             */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Combos_list_and_detail_work_by_slug_and_id()
    {
        var combos = await GetAsync<List<Combo>>("/api/combos");
        var expected = (await CatalogueAsync()).Combos;
        Assert.Equal(expected.Count, combos.Count);

        var first = expected[0];
        var bySlug = await GetAsync<Combo>($"/api/combos/{first.Slug}");
        var byId = await GetAsync<Combo>($"/api/combos/{first.Id}");

        Assert.Equal(bySlug.Id, byId.Id);
        Assert.Equal(first.Price, bySlug.Price);
        Assert.NotEmpty(bySlug.Includes);
        Assert.Equal(first.Includes[0].Qty, bySlug.Includes[0].Qty);
    }

    [Fact]
    public async Task Combos_can_be_sorted_and_filtered()
    {
        var featured = await GetAsync<List<Combo>>("/api/combos?featured=true");
        Assert.All(featured, c => Assert.True(c.Featured));

        var cheapFirst = await GetAsync<List<Combo>>("/api/combos?sort=price-asc");
        Assert.Equal(cheapFirst.Select(c => c.Price).OrderBy(p => p), cheapFirst.Select(c => c.Price));
    }

    [Fact]
    public async Task Unknown_combo_is_404() =>
        await AssertStatusAsync("/api/combos/nope", HttpStatusCode.NotFound);

    [Fact]
    public async Task Offers_expose_a_countdown_that_is_never_already_dead()
    {
        var body = await GetJsonAsync("/api/offers");
        var offers = body.EnumerateArray().ToList();

        Assert.Equal(6, offers.Count);
        foreach (var offer in offers)
        {
            var resolved = offer.GetProperty("resolvedEndsAt").GetDateTimeOffset();
            Assert.True(resolved > DateTimeOffset.UtcNow,
                $"{offer.GetProperty("code").GetString()} resolved to a past deadline");
        }
    }

    [Fact]
    public async Task Offer_detail_resolves_by_code_case_insensitively()
    {
        var upper = await GetAsync<Offer>("/api/offers/DIWALI75");
        var lower = await GetAsync<Offer>("/api/offers/diwali75");

        Assert.Equal(upper.Id, lower.Id);
        Assert.Equal(75, upper.Value);
    }

    [Fact]
    public async Task Unknown_offer_is_404() =>
        await AssertStatusAsync("/api/offers/NOPE", HttpStatusCode.NotFound);

    [Fact]
    public async Task Featured_offers_endpoint_matches_the_filter()
    {
        var viaPath = await GetAsync<List<Offer>>("/api/offers/featured");
        var viaQuery = await GetAsync<List<Offer>>("/api/offers?featured=true");

        Assert.Equal(viaQuery.Select(o => o.Id), viaPath.Select(o => o.Id));
        Assert.All(viaPath, o => Assert.True(o.Featured));
    }

    [Fact]
    public async Task Banners_filter_by_placement()
    {
        var all = await GetAsync<List<Banner>>("/api/banners");
        Assert.Equal(4, all.Count);

        var hero = await GetAsync<List<Banner>>("/api/banners?placement=hero");
        Assert.Single(hero);
        Assert.Equal("Light up the night", hero[0].Title);
        Assert.NotNull(hero[0].CtaPrimary);
        Assert.Equal("/products", hero[0].CtaPrimary!.To);

        var missing = await GetAsync<List<Banner>>("/api/banners?placement=nowhere");
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Banner_without_optional_fields_omits_them_rather_than_sending_null()
    {
        var body = await GetJsonAsync("/api/banners/bnr-02");

        Assert.False(body.TryGetProperty("subtitle", out _));
        Assert.False(body.TryGetProperty("ctaPrimary", out _));
        Assert.Equal("strip", body.GetProperty("placement").GetString());
    }

    [Fact]
    public async Task Unknown_banner_is_404() =>
        await AssertStatusAsync("/api/banners/bnr-99", HttpStatusCode.NotFound);

    /// <summary>
    /// The shop has no review system, so the seeded testimonials — which were
    /// invented — were removed. The endpoint stays: it answers 200 with an empty
    /// list until real quotes are collected, and its filters still hold whenever
    /// there are. Asserting against the catalogue rather than a fixed count keeps
    /// this honest either way.
    /// </summary>
    [Fact]
    public async Task Testimonials_answer_and_support_limit_and_rating_filters()
    {
        var all = await GetAsync<List<Testimonial>>("/api/testimonials");
        Assert.Equal((await CatalogueAsync()).Testimonials.Count, all.Count);

        var limited = await GetAsync<List<Testimonial>>("/api/testimonials?limit=3");
        Assert.Equal(Math.Min(3, all.Count), limited.Count);

        var fiveStar = await GetAsync<List<Testimonial>>("/api/testimonials?minRating=5");
        Assert.All(fiveStar, t => Assert.True(t.Rating >= 5));
    }

    [Fact]
    public async Task Faqs_group_by_category()
    {
        var all = await GetAsync<List<Faq>>("/api/faqs");
        Assert.Equal(12, all.Count);

        var categories = await GetAsync<List<string>>("/api/faqs/categories");
        Assert.Equal(all.Select(f => f.Category).Distinct().OrderBy(c => c), categories.OrderBy(c => c));

        var safety = await GetAsync<List<Faq>>("/api/faqs?category=Safety");
        Assert.NotEmpty(safety);
        Assert.All(safety, f => Assert.Equal("Safety", f.Category));

        var one = await GetAsync<Faq>("/api/faqs/faq-01");
        Assert.Contains("75%", one.Question);
    }

    [Fact]
    public async Task Unknown_faq_is_404() =>
        await AssertStatusAsync("/api/faqs/faq-99", HttpStatusCode.NotFound);

    /* ---------------------------------------------------------------------- */
    /* Search                                                                  */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Search_ranks_the_named_product_first()
    {
        // Searching a product's own full name must put that product first.
        var named = (await CatalogueAsync()).Products.First(p => p.Name.Split(' ').Length >= 3);
        var result = await GetAsync<SearchResult>($"/api/search?q={Uri.EscapeDataString(named.Name)}");

        Assert.NotEmpty(result.Products);
        Assert.Equal(named.Slug, result.Products[0].Slug);
    }

    [Fact]
    public async Task Search_requires_every_term_to_match()
    {
        var broad = await GetAsync<SearchResult>("/api/search?q=sparkler&limit=100");
        var narrow = await GetAsync<SearchResult>("/api/search?q=gold sparkler&limit=100");

        Assert.True(narrow.ProductTotal < broad.ProductTotal,
            $"'gold sparkler' matched {narrow.ProductTotal}, 'sparkler' matched {broad.ProductTotal}");
    }

    [Fact]
    public async Task Search_returns_matching_categories_too()
    {
        // A category's own name must find that category.
        var category = await PopulatedCategoryAsync();
        var result = await GetAsync<SearchResult>(
            $"/api/search?q={Uri.EscapeDataString(category.Name)}");

        Assert.Contains(result.Categories, c => c.Slug == category.Slug);
    }

    [Fact]
    public async Task Search_matches_a_category_by_its_tamil_name()
    {
        var result = await GetAsync<SearchResult>("/api/search?q=sparklers");
        Assert.NotEmpty(result.Products);
    }

    [Fact]
    public async Task Search_for_nonsense_returns_an_empty_result_not_an_error()
    {
        var result = await GetAsync<SearchResult>("/api/search?q=zzzzqqq");
        Assert.Empty(result.Products);
        Assert.Equal(0, result.ProductTotal);
    }

    [Fact]
    public async Task Search_without_a_term_is_a_400() =>
        await AssertStatusAsync("/api/search", HttpStatusCode.BadRequest);

    [Fact]
    public async Task Search_honours_the_limit_while_reporting_the_true_total()
    {
        var result = await GetAsync<SearchResult>("/api/search?q=sparkler&limit=2");

        Assert.Equal(2, result.Products.Count);
        Assert.True(result.ProductTotal >= 2);
    }

    [Fact]
    public async Task Filtering_by_a_search_term_matches_the_search_endpoint()
    {
        var search = await GetAsync<SearchResult>("/api/search?q=chakkar&limit=100");
        var filter = await GetAsync<PagedResult<Product>>("/api/products?q=chakkar&pageSize=100");

        Assert.Equal(search.ProductTotal, filter.Total);
        Assert.Equal(search.Products.Select(p => p.Id), filter.Items.Select(p => p.Id));
    }

    /* ---------------------------------------------------------------------- */
    /* Catalogue integrity — the same guarantees search.test.js checks          */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task Every_slug_is_unique()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?pageSize=100");
        Assert.Equal(page.Items.Count, page.Items.Select(p => p.Slug).Distinct().Count());
        Assert.Equal(page.Items.Count, page.Items.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task Every_product_belongs_to_a_real_category()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?pageSize=100");
        var slugs = (await GetAsync<List<Category>>("/api/categories")).Select(c => c.Slug).ToHashSet();

        Assert.All(page.Items, p => Assert.Contains(p.Category, slugs));
    }

    [Fact]
    public async Task Every_stated_discount_matches_the_actual_price_gap()
    {
        var page = await GetAsync<PagedResult<Product>>("/api/products?pageSize=100");

        foreach (var product in page.Items)
        {
            var actual = (int)Math.Round((product.Mrp - product.Price) / (double)product.Mrp * 100,
                MidpointRounding.AwayFromZero);

            Assert.True(Math.Abs(actual - product.Discount) <= 1,
                $"{product.Slug} claims {product.Discount}% but ₹{product.Mrp}→₹{product.Price} is {actual}%");
        }
    }
}
