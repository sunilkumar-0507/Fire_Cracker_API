using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The API as it runs on a server: in Production, behind a reverse proxy that
/// has already terminated TLS.
///
/// Every other fixture boots in Development, where HTTPS redirection is off —
/// so the one piece of the pipeline that only exists in Production was, until
/// this file, never executed by anything. That is the piece that decides
/// whether the deployed site answers or loops.
/// </summary>
public sealed class ReverseProxyTests
{
    /// <summary>
    /// Production rather than Development, which is the whole point: it is what
    /// turns <c>UseHttpsRedirection</c> on.
    /// </summary>
    private sealed class ProductionFixture : WebApplicationFactory<Program>
    {
        private readonly string _dataPath = ApiFixture.CopyCatalogue();
        private readonly bool _behindProxy;

        public ProductionFixture(bool behindProxy = true) => _behindProxy = behindProxy;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder
                .UseSetting(WebHostDefaults.EnvironmentKey, "Production")
                .UseSetting("Catalog:DataPath", _dataPath)
                .UseSetting("Storefront:Hosting:BehindReverseProxy", _behindProxy ? "true" : "false")

                // Without a port to redirect to, UseHttpsRedirection logs that
                // it could not work one out and passes the request through — so
                // every assertion below would see 200 and the suite would prove
                // nothing. A real deployment learns the port from its bindings;
                // the test host has none, so it is told.
                .UseSetting("https_port", "443");

        /// <summary>Redirects are the subject here, so they are not followed.</summary>
        public HttpClient Direct() => CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;

            try { Directory.Delete(_dataPath, recursive: true); }
            catch (IOException) { /* A temp directory left behind is not a test failure. */ }
        }
    }

    /// <summary>
    /// The regression this exists for.
    ///
    /// nginx terminates TLS and forwards plain HTTP to Kestrel with
    /// <c>X-Forwarded-Proto: https</c>. If the pipeline ignores that header it
    /// sees an http:// request, answers 307 to the https:// address, nginx
    /// forwards it back as http, and the site becomes an infinite redirect —
    /// not one broken endpoint but all of them.
    /// </summary>
    [Fact]
    public async Task A_proxied_https_request_is_served_rather_than_redirected()
    {
        using var fixture = new ProductionFixture();
        using var client = fixture.Direct();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"A request the proxy already served over HTTPS was answered with " +
            $"{(int)response.StatusCode} {response.StatusCode}" +
            (response.Headers.Location is { } to ? $" to {to}" : string.Empty) +
            ". In production this is an infinite redirect loop.");
    }

    /// <summary>
    /// The other half, without which the test above proves nothing: redirection
    /// really is on in Production, so being served a 200 was the forwarded
    /// header being honoured and not the redirect being absent.
    /// </summary>
    [Fact]
    public async Task An_unproxied_http_request_is_still_redirected_to_https()
    {
        using var fixture = new ProductionFixture();
        using var client = fixture.Direct();

        var response = await client.GetAsync("/api/health");

        Assert.True(response.StatusCode is HttpStatusCode.Redirect
                        or HttpStatusCode.TemporaryRedirect
                        or HttpStatusCode.MovedPermanently
                        or HttpStatusCode.PermanentRedirect,
            $"Plain HTTP in production should be redirected to HTTPS, but got " +
            $"{(int)response.StatusCode}.");
    }

    /* ---------------------------------------------------------------------- */
    /* CORS, in the pipeline production actually runs                          */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// The regression that only exists in Production.
    ///
    /// UseHttpsRedirection is switched on there and nowhere else, so it is the
    /// one piece of middleware that can turn a preflight into a redirect — and
    /// a browser does not follow a redirect on a preflight. It fails the
    /// request outright, which means the admin panel cannot send so much as a
    /// GET: X-Admin-Passcode is not a header a browser will send without
    /// asking permission for it first.
    ///
    /// Answered here by the CORS middleware, ahead of the redirect, so the
    /// browser gets its 204 and its allow header and goes on to make the real
    /// request.
    /// </summary>
    [Fact]
    public async Task An_admin_preflight_is_answered_rather_than_redirected()
    {
        using var fixture = new ProductionFixture();
        using var client = fixture.Direct();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/admin/summary");
        request.Headers.Add("Origin", "https://admin.skvpyros.in");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-admin-passcode");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"The admin preflight was answered with {(int)response.StatusCode} {response.StatusCode}" +
            (response.Headers.Location is { } to ? $" to {to}" : string.Empty) +
            ". A browser does not follow a redirect on a preflight, so in production " +
            "this is the whole admin panel being unable to send a single request.");

        Assert.Equal("https://admin.skvpyros.in",
            response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    /// <summary>
    /// The storefront's ordinary GET, arriving the way nginx delivers it: plain
    /// HTTP on loopback, with the scheme it was really served over in the
    /// forwarded header. 200 and the allow header is the shop working.
    /// </summary>
    [Fact]
    public async Task A_proxied_storefront_request_carries_the_allow_header()
    {
        using var fixture = new ProductionFixture();
        using var client = fixture.Direct();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Origin", "https://skvpyros.in");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://skvpyros.in",
            response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    /// <summary>
    /// Even the redirect carries the header. This is the case where nginx has
    /// been set up without X-Forwarded-Proto: the API still answers 307, but a
    /// browser can now read the redirect and follow it, so the site degrades
    /// into an extra round trip rather than into "blocked by CORS" — which
    /// would have sent whoever is debugging it to the origin list instead of
    /// to the nginx config.
    /// </summary>
    [Fact]
    public async Task Even_a_redirect_carries_the_allow_header()
    {
        using var fixture = new ProductionFixture();
        using var client = fixture.Direct();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://skvpyros.in");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect
                        or HttpStatusCode.TemporaryRedirect
                        or HttpStatusCode.MovedPermanently
                        or HttpStatusCode.PermanentRedirect,
            $"Expected this to be the redirect case, got {(int)response.StatusCode}.");

        Assert.Equal("https://skvpyros.in",
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
                ? values.FirstOrDefault()
                : null);
    }

    /// <summary>
    /// Turning the setting off must actually stop the headers being trusted —
    /// that is the escape hatch for a Kestrel exposed directly, where a caller
    /// could otherwise spoof X-Forwarded-Proto to skip the redirect.
    /// </summary>
    [Fact]
    public async Task Forwarded_headers_are_ignored_when_the_setting_is_off()
    {
        using var fixture = new ProductionFixture(behindProxy: false);
        using var client = fixture.Direct();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect
                        or HttpStatusCode.TemporaryRedirect
                        or HttpStatusCode.MovedPermanently
                        or HttpStatusCode.PermanentRedirect,
            $"With BehindReverseProxy off the header should carry no weight, but the " +
            $"request was answered with {(int)response.StatusCode}.");
    }
}
