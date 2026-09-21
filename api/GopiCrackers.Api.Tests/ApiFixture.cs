using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GopiCrackers.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Boots the real API in-process — real routing, real model binding, real JSON
/// serialisation. Nothing is stubbed, so a passing test means the endpoint
/// genuinely answers over HTTP.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    /// <summary>
    /// A throwaway copy of the catalogue for this run to write into.
    ///
    /// In development <c>CatalogStore</c> deliberately resolves to the repo's
    /// own <c>src/data</c> so an admin edit lands in the files the storefront is
    /// built from. That is right for `dotnet run` and wrong for `dotnet test`:
    /// the admin tests create and delete products, and every order the suite
    /// places is appended to a journal that has no delete — so a test run left
    /// fake orders sitting in the shopkeeper's order book. Copying first keeps
    /// the suite's writes real without letting them touch the working tree.
    /// </summary>
    private readonly string _dataPath = CopyCatalogue();

    /// <summary>
    /// The passcode the suite authenticates with. Set on the host below rather
    /// than read from <c>appsettings.Development.json</c>: a deployment is free
    /// to change its own dev passcode, and that must not turn every admin test
    /// into a 401.
    /// </summary>
    public const string AdminPasscode = "gopi-2026";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        // Development so the OpenAPI document is mapped and HTTPS redirection
        // stays off — the same pipeline `dotnet run` gives you locally.
        => builder
            .UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development")
            .UseSetting("Catalog:DataPath", _dataPath)
            .UseSetting("Storefront:Admin:Passcode", AdminPasscode);

    /// <summary>
    /// Internal rather than private so the MySQL fixture can take the same
    /// throwaway copy. A run backed by MySQL still seeds itself from these
    /// files, and still must not write to the working tree while doing it.
    /// </summary>
    internal static string CopyCatalogue()
    {
        var source = FindCatalogue();
        var target = Path.Combine(Path.GetTempPath(), "gopi-api-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        // A previous run's journal is not this run's starting state.
        var orders = Path.Combine(target, "orders.json");
        if (File.Exists(orders)) File.Delete(orders);

        return target;
    }

    /// <summary>Walks up from the test binary to the repo's <c>src/data</c>.</summary>
    private static string FindCatalogue()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "data");
            if (File.Exists(Path.Combine(candidate, "products.json"))) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find src/data above {AppContext.BaseDirectory}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try { Directory.Delete(_dataPath, recursive: true); }
        catch (IOException) { /* A temp directory left behind is not a test failure. */ }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

/// <summary>Shared helpers so each test reads as one assertion, not five lines of plumbing.</summary>
public abstract class ApiTestBase(ApiFixture fixture)
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected HttpClient Client { get; } = fixture.CreateClient();

    /// <summary>GETs a URL, asserts 200, and returns the parsed body.</summary>
    protected async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {url} returned {(int)response.StatusCode} {response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    protected async Task<T> GetAsync<T>(string url)
    {
        var response = await Client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {url} returned {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    protected async Task AssertStatusAsync(string url, HttpStatusCode expected)
    {
        var response = await Client.GetAsync(url);
        Assert.True(response.StatusCode == expected,
            $"GET {url} expected {(int)expected} but got {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");
    }

    protected async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object payload)
    {
        var response = await Client.PostAsJsonAsync(url, payload, Json);
        return await ReadAsync(response);
    }

    /* ---------------------------------------------------------------------- */
    /* Admin                                                                   */
    /* ---------------------------------------------------------------------- */

    /// <summary>The passcode the fixture boots the API with.</summary>
    protected const string AdminPasscode = ApiFixture.AdminPasscode;

    /// <summary>
    /// A second client that carries the admin header on every request. Separate
    /// from <see cref="Client"/> on purpose: a test that means to check an
    /// endpoint is *closed* needs an unauthenticated client to hand, and sharing
    /// one client with a mutable default header makes that easy to get wrong.
    /// </summary>
    private HttpClient AdminClient
    {
        get
        {
            if (_adminClient is not null) return _adminClient;

            _adminClient = fixture.CreateClient();
            _adminClient.DefaultRequestHeaders.Add("X-Admin-Passcode", AdminPasscode);
            return _adminClient;
        }
    }

    private HttpClient? _adminClient;

    protected async Task<JsonElement> GetAdminJsonAsync(string url)
    {
        var response = await AdminClient.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {url} (admin) returned {(int)response.StatusCode} {response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    protected async Task<(HttpStatusCode Status, JsonElement Body)> PostAdminAsync(string url, object payload)
    {
        var response = await AdminClient.PostAsJsonAsync(url, payload, Json);
        return await ReadAsync(response);
    }

    protected async Task<(HttpStatusCode Status, JsonElement Body)> PatchAdminAsync(string url, object payload)
    {
        var response = await AdminClient.PatchAsJsonAsync(url, payload, Json);
        return await ReadAsync(response);
    }

    protected async Task<HttpStatusCode> DeleteAdminAsync(string url) =>
        (await AdminClient.DeleteAsync(url)).StatusCode;

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        var body = string.IsNullOrWhiteSpace(text)
            ? default
            : JsonSerializer.Deserialize<JsonElement>(text, Json);

        return (response.StatusCode, body);
    }

    /* ---------------------------------------------------------------------- */
    /* Catalogue facts                                                         */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// The whole catalogue, fetched once per run.
    ///
    /// Tests read their expected counts, slugs and prices from here rather than
    /// hardcoding them. The catalogue is real shop data — it changed wholesale
    /// when the 2026 price list replaced the demo one, and every test that had
    /// baked in "43 products" or a specific slug broke at once. Asserting that
    /// the endpoints agree with each other is both a stronger check and one that
    /// survives the next price list.
    /// </summary>
    protected async Task<Bootstrap> CatalogueAsync() =>
        _catalogue ??= await GetAsync<Bootstrap>("/api/bootstrap");

    private static Bootstrap? _catalogue;

    /// <summary>The cheapest product — a safe basket line that stays under any threshold.</summary>
    protected async Task<Product> CheapestProductAsync() =>
        (await CatalogueAsync()).Products.OrderBy(p => p.Price).First();

    /// <summary>A category that actually holds products, for the filter tests.</summary>
    protected async Task<Category> PopulatedCategoryAsync() =>
        (await CatalogueAsync()).Categories.OrderByDescending(c => c.ProductCount).First();

    /// <summary>A combo priced above the ₹2,000 free-delivery line.</summary>
    protected async Task<Combo> ComboOverFreeDeliveryAsync() =>
        (await CatalogueAsync()).Combos.First(c => c.Price > 2000);
}
