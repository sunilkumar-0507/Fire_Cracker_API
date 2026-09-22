using GopiCrackers.Api.Security;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The spellings of an origin that a person writes into a config file, against
/// the one spelling a browser sends.
///
/// These need no fixture and no HTTP, which is the point: the failure they
/// guard is invisible over HTTP. A configured origin that does not match is
/// answered 200 like any other and discarded by the browser, so the only way
/// to catch "https://skvpyros.in/" being one character away from working is to
/// assert on the string before it ever reaches the middleware.
/// </summary>
public sealed class CorsOriginsTests
{
    private const string Shop = "https://skvpyros.in";

    [Theory]
    [InlineData("https://skvpyros.in")]
    [InlineData("https://skvpyros.in/")]          // trailing slash
    [InlineData("  https://skvpyros.in  ")]       // copy-paste whitespace
    [InlineData("https://SKVPyros.in")]           // capitals
    [InlineData("https://skvpyros.in:443")]       // the default port, spelled out
    [InlineData("https://skvpyros.in/api")]       // the API URL rather than the origin
    [InlineData("https://skvpyros.in/?utm=x#a")]  // query and fragment
    public void Spellings_of_the_shop_all_normalise_to_the_origin_a_browser_sends(string configured)
    {
        Assert.True(CorsOrigins.TryNormalise(configured, out var origin));
        Assert.Equal(Shop, origin);
    }

    /// <summary>A non-default port is part of the origin and has to survive.</summary>
    [Theory]
    [InlineData("http://localhost:5173/", "http://localhost:5173")]
    [InlineData("http://127.0.0.1:4174", "http://127.0.0.1:4174")]
    [InlineData("http://localhost:80", "http://localhost")]
    public void A_port_is_kept_unless_it_is_the_default_for_the_scheme(string configured, string expected)
    {
        Assert.True(CorsOrigins.TryNormalise(configured, out var origin));
        Assert.Equal(expected, origin);
    }

    /// <summary>
    /// Nothing here is guessed at. A bare hostname is not an origin — the
    /// scheme is half of what CORS compares — and it is reported rather than
    /// silently promoted to https.
    /// </summary>
    [Theory]
    [InlineData("skvpyros.in")]
    [InlineData("//skvpyros.in")]
    [InlineData("ftp://skvpyros.in")]
    [InlineData("not a url")]
    [InlineData("*")]   // never, on an API that takes orders
    public void An_entry_that_is_not_an_http_origin_is_refused(string configured)
    {
        Assert.False(CorsOrigins.TryNormalise(configured, out _));
    }

    [Fact]
    public void Refused_entries_are_reported_by_name_rather_than_dropped_quietly()
    {
        var result = CorsOrigins.Resolve([Shop, "skvpyros.in", "*"]);

        Assert.Equal([Shop], result.Allowed);
        Assert.Equal(["skvpyros.in", "*"], result.Rejected);
    }

    /// <summary>Two spellings of one origin are one entry, in the order first seen.</summary>
    [Fact]
    public void Duplicates_collapse_and_order_is_preserved()
    {
        var result = CorsOrigins.Resolve([Shop, "https://admin.skvpyros.in", "https://skvpyros.in/"]);

        Assert.Equal([Shop, "https://admin.skvpyros.in"], result.Allowed);
        Assert.Empty(result.Rejected);
    }

    /// <summary>Blank lines are padding in a config file, not a mistake to report.</summary>
    [Fact]
    public void Blank_entries_are_skipped_without_comment()
    {
        var result = CorsOrigins.Resolve([Shop, "", "   ", null]);

        Assert.Equal([Shop], result.Allowed);
        Assert.Empty(result.Rejected);
    }

    /// <summary>
    /// The three deployed origins, spelled exactly as appsettings.json spells
    /// them, must pass through untouched — normalisation has to be a no-op on
    /// the values that were already right.
    /// </summary>
    [Fact]
    public void The_deployed_origins_survive_normalisation_unchanged()
    {
        string[] deployed =
        [
            "https://skvpyros.in",
            "https://www.skvpyros.in",
            "https://admin.skvpyros.in",
        ];

        var result = CorsOrigins.Resolve(deployed);

        Assert.Equal(deployed, result.Allowed);
        Assert.Empty(result.Rejected);
    }
}
