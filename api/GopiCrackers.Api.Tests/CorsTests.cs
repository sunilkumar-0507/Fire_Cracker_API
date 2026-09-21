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
}
