namespace GopiCrackers.Api.Tests;

/// <summary>
/// The deployment check: every endpoint this API claims to have, exercised
/// against the route table it actually builds.
///
/// The other suites test what endpoints <em>do</em>. This one tests that they
/// are all still there — which is the question you have at the moment of
/// putting the thing on a server, and the one a per-endpoint suite answers
/// only for the endpoints somebody remembered to write a test for. The
/// documented list at <c>GET /</c> is the contract; ASP.NET's own route table
/// is the truth. Drift in either direction is a failure here: an endpoint that
/// is documented and missing would 404 a caller who believed the index, and one
/// that exists and is undocumented is usually a route somebody forgot they
/// shipped.
///
/// This runs on the JSON backend, which is the one that needs no configuration.
/// <see cref="MySqlBackendTests"/> runs the same walks against MySQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointCoverageTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private readonly ApiFixture _fixture = fixture;

    [Fact]
    public async Task Every_documented_endpoint_exists_in_the_route_table()
    {
        var routed = EndpointAudit.Routed(_fixture.Services)
            .Select(r => $"{r.Method} {r.Template}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = (await EndpointAudit.DocumentedAsync(Client))
            .Where(d => !routed.Contains($"{d.Method} {d.Template}"))
            .Select(d => $"{d.Method} {d.Template}")
            .ToList();

        Assert.True(missing.Count == 0,
            "The index at GET / advertises endpoints the API does not route, so a caller who " +
            "trusted it would get a 404:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public async Task Every_routed_endpoint_is_documented()
    {
        var documented = (await EndpointAudit.DocumentedAsync(Client))
            .Select(d => $"{d.Method} {d.Template}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = EndpointAudit.Routed(_fixture.Services)
            .Select(r => $"{r.Method} {r.Template}")
            .Where(r => !documented.Contains(r))
            .Distinct()
            .OrderBy(r => r)
            .ToList();

        Assert.True(undocumented.Count == 0,
            "The API routes endpoints the index at GET / does not mention:\n  " +
            string.Join("\n  ", undocumented));
    }

    [Fact]
    public async Task Every_documented_GET_answers_200()
    {
        var failures = await EndpointAudit.FailingGetsAsync(Client, AdminPasscode);

        Assert.True(failures.Count == 0,
            $"{failures.Count} documented GET endpoint(s) did not return 200:\n  " +
            string.Join("\n  ", failures));
    }

    [Fact]
    public async Task Every_admin_endpoint_refuses_a_caller_without_the_passcode()
    {
        var failures = await EndpointAudit.UnprotectedAdminAsync(Client);

        Assert.True(failures.Count == 0,
            $"{failures.Count} admin endpoint(s) did not refuse an anonymous caller:\n  " +
            string.Join("\n  ", failures));
    }
}
