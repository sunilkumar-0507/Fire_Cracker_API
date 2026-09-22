using System.Net;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Which origins the browser is allowed to read a response from.
///
/// Worth its own file because the failure is so quiet. CORS is enforced in the
/// browser, not here: an origin that is not allowed still gets a perfectly
/// normal 200 with a perfectly normal body, and the browser throws it away
/// before the app sees it. Nothing shows up in the API's logs. So the shop
/// loads, every request "succeeds", and the screen stays empty.
///
/// The specific trap these guard is that
/// <c>Storefront:AllowedOrigins</c> in appsettings.json <em>replaces</em> the
/// built-in list rather than adding to it. Adding a production domain by
/// overwriting the file's array is how the eight localhost entries get dropped
/// by accident, and the first anyone knows is that `npm run dev` stopped
/// working against the API.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CorsTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const string AllowOrigin = "Access-Control-Allow-Origin";

    /// <summary>
    /// A plain GET carrying an Origin, which is what a browser sends for the
    /// shop's own requests. The allow header coming back is what lets the app
    /// read the body.
    /// </summary>
    private async Task<string?> AllowedOriginForAsync(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", origin);

        var response = await Client.SendAsync(request);

        // The request itself always succeeds — that is the whole problem.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return response.Headers.TryGetValues(AllowOrigin, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    [Theory]
    // The two Vite dev servers, and the two preview servers that serve a real
    // production bundle. Losing any of these breaks local work against the API.
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://localhost:5174")]
    [InlineData("http://127.0.0.1:5174")]
    [InlineData("http://localhost:4173")]
    [InlineData("http://127.0.0.1:4173")]
    [InlineData("http://localhost:4174")]
    [InlineData("http://127.0.0.1:4174")]
    public async Task Local_development_origins_stay_allowed(string origin)
    {
        Assert.Equal(origin, await AllowedOriginForAsync(origin));
    }

    /// <summary>
    /// The deployed admin. If this regresses, the admin panel loads and then
    /// cannot read a single response.
    /// </summary>
    [Fact]
    public async Task The_deployed_admin_origin_is_allowed()
    {
        const string admin = "https://admin.skvpyros.in";

        Assert.Equal(admin, await AllowedOriginForAsync(admin));
    }

    /// <summary>
    /// The deployed shop, both spellings — www is a CNAME onto the apex, so
    /// whichever one the customer typed is the origin the browser sends, and
    /// they are two different origins as far as CORS is concerned.
    ///
    /// This one has already regressed once: the shop went live while the array
    /// still listed only the admin, so every storefront request was fetched,
    /// answered 200, and thrown away by the browser unread. The shop fell back
    /// to the JSON in its own bundle and looked merely stale.
    /// </summary>
    [Theory]
    [InlineData("https://skvpyros.in")]
    [InlineData("https://www.skvpyros.in")]
    public async Task The_deployed_storefront_origin_is_allowed(string origin)
    {
        Assert.Equal(origin, await AllowedOriginForAsync(origin));
    }

    /// <summary>
    /// Without this the tests above would pass just as happily against a
    /// policy that allowed everything, which is not the policy anyone wants on
    /// an API that takes orders.
    /// </summary>
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://admin.skvpyros.in")]      // http, not https — a different origin
    [InlineData("https://skvpyros.in.evil.test")] // suffix trick
    public async Task An_unlisted_origin_is_not_allowed(string origin)
    {
        Assert.Null(await AllowedOriginForAsync(origin));
    }

    /// <summary>
    /// An allowed response must tell caches that it depends on who asked.
    /// Without <c>Vary: Origin</c> a proxy in front of the API can serve the
    /// shop a response it cached for the admin — allow header and all — and
    /// the browser discards it. The bug then appears and disappears with the
    /// cache, which is the hardest possible version of this to chase.
    /// </summary>
    [Fact]
    public async Task An_allowed_response_varies_on_the_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://skvpyros.in");

        var response = await Client.SendAsync(request);

        Assert.Contains("Origin", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    /* ---------------------------------------------------------------------- */
    /* Preflight                                                               */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// The OPTIONS request a browser sends on its own before the real one.
    /// </summary>
    private async Task<HttpResponseMessage> PreflightAsync(
        string origin, string method, string url, string? requestHeaders = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);

        if (requestHeaders is not null)
            request.Headers.Add("Access-Control-Request-Headers", requestHeaders);

        return await Client.SendAsync(request);
    }

    /// <summary>
    /// The admin's whole surface depends on this one exchange.
    ///
    /// <c>X-Admin-Passcode</c> is not a header a browser will send off its own
    /// bat, so every admin call — including the plain GETs — is preceded by a
    /// preflight asking whether that header is permitted. If the preflight is
    /// not answered cleanly, nothing in the admin panel works: not one request
    /// is ever sent, and the failure is reported as CORS rather than as
    /// whatever actually went wrong.
    /// </summary>
    [Fact]
    public async Task The_admin_passcode_header_survives_preflight()
    {
        var response = await PreflightAsync(
            "https://admin.skvpyros.in", "GET", "/api/admin/summary", "x-admin-passcode");

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"A preflight for the admin passcode header was answered with " +
            $"{(int)response.StatusCode} {response.StatusCode}" +
            (response.Headers.Location is { } to ? $" to {to}" : string.Empty) +
            ". The admin panel cannot send a single request when this fails.");

        Assert.Equal("https://admin.skvpyros.in",
            response.Headers.GetValues(AllowOrigin).FirstOrDefault());

        var allowedHeaders = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var h)
            ? string.Join(",", h)
            : string.Empty;

        Assert.Contains("x-admin-passcode", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The writing half of the admin: a PATCH is not a method a browser sends
    /// without asking either.
    /// </summary>
    [Fact]
    public async Task A_mutating_admin_method_survives_preflight()
    {
        var response = await PreflightAsync(
            "https://admin.skvpyros.in", "PATCH", "/api/admin/stock", "x-admin-passcode,content-type");

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"A PATCH preflight was answered with {(int)response.StatusCode}.");

        var allowedMethods = response.Headers.TryGetValues("Access-Control-Allow-Methods", out var m)
            ? string.Join(",", m)
            : string.Empty;

        Assert.Contains("PATCH", allowedMethods, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shop's own preflight, which it sends for the checkout POST because
    /// its Content-Type is JSON.
    /// </summary>
    [Fact]
    public async Task The_checkout_post_survives_preflight()
    {
        var response = await PreflightAsync("https://skvpyros.in", "POST", "/api/orders", "content-type");

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"The checkout preflight was answered with {(int)response.StatusCode}.");

        Assert.Equal("https://skvpyros.in", response.Headers.GetValues(AllowOrigin).FirstOrDefault());
    }

    /// <summary>
    /// A preflight from an origin nobody listed gets no allow header, so the
    /// browser never sends the request it was asking about.
    /// </summary>
    [Fact]
    public async Task A_preflight_from_an_unlisted_origin_is_not_allowed()
    {
        var response = await PreflightAsync(
            "https://example.com", "GET", "/api/admin/summary", "x-admin-passcode");

        Assert.False(response.Headers.Contains(AllowOrigin));
    }

    /* ---------------------------------------------------------------------- */
    /* Failures the browser must still be allowed to read                      */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// An error response needs the allow header as much as a successful one.
    ///
    /// Without it the shop's fetch() cannot see the 404 it got — the browser
    /// reports a CORS error instead — and whoever is debugging goes looking at
    /// the origin list for a problem that is a wrong URL. This is the reason
    /// UseCors sits ahead of the exception handler and the status-code pages
    /// rather than after them.
    /// </summary>
    [Theory]
    [InlineData("/api/products/no-such-product-slug")] // 404 from a controller
    [InlineData("/api/definitely-not-a-route")]        // 404 from routing itself
    public async Task An_error_response_still_carries_the_allow_header(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://skvpyros.in");

        var response = await Client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound,
            $"Expected this to be a 404 for the test to mean anything, got {(int)response.StatusCode}.");

        Assert.Equal("https://skvpyros.in",
            response.Headers.TryGetValues(AllowOrigin, out var values) ? values.FirstOrDefault() : null);
    }
}
