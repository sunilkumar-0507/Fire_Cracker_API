using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The database and newsletter endpoints, exercised against an API running on
/// its JSON files — which is the configuration the suite boots and the one a
/// deployment is in before anybody has set a connection string.
///
/// That makes these tests about a specific promise: <b>every one of these
/// routes answers usefully with no database behind it</b>. Nothing 500s,
/// nothing says "object reference not set", and the two that genuinely need a
/// database say so in a sentence a shopkeeper could act on.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DatabaseEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Status_reports_the_file_backend_when_no_connection_string_is_set()
    {
        var body = await GetAdminJsonAsync("/api/admin/database");

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("files", body.GetProperty("backend").GetString());
        Assert.Equal("none", body.GetProperty("provider").GetString());
        Assert.False(body.GetProperty("canConnect").GetBoolean());

        // The sentence is the point: it names the setting to fill in.
        var error = body.GetProperty("error").GetString();
        Assert.Contains("ConnectionString", error);
    }

    [Fact]
    public async Task Status_never_echoes_a_password()
    {
        var body = await GetAdminJsonAsync("/api/admin/database");

        // Nothing is configured here, so there is nothing to redact — but the
        // field must not be a place a password could appear by default.
        Assert.True(
            body.TryGetProperty("connectionString", out var connection) is false ||
            connection.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Every_store_agrees_on_which_backend_it_is_using()
    {
        var body = await GetAdminJsonAsync("/api/admin/database/backends");

        Assert.Equal("files", body.GetProperty("catalogue").GetString());
        Assert.Equal("files", body.GetProperty("orders").GetString());
        Assert.Equal("files", body.GetProperty("inventory").GetString());
        Assert.Equal("files", body.GetProperty("analytics").GetString());
        Assert.Equal("files", body.GetProperty("configured").GetString());
    }

    [Fact]
    public async Task Health_says_where_the_data_is_kept()
    {
        var body = await GetJsonAsync("/api/health");
        var storage = body.GetProperty("storage");

        Assert.Equal("files", storage.GetProperty("backend").GetString());
        Assert.Equal("not configured", storage.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Migrating_without_a_database_is_a_conflict_not_a_crash()
    {
        var (status, body) = await PostAdminAsync("/api/admin/database/migrate", new { });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("JSON files", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Seeding_without_a_database_is_a_conflict_not_a_crash()
    {
        var (status, body) = await PostAdminAsync("/api/admin/database/seed", new { overwrite = false });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("ConnectionString", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Reloading_re_reads_the_catalogue_from_wherever_it_lives()
    {
        var catalogue = await CatalogueAsync();
        var (status, body) = await PostAdminAsync("/api/admin/database/reload", new { });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("files", body.GetProperty("backend").GetString());
        Assert.Equal(catalogue.Products.Count, body.GetProperty("products").GetInt32());
        Assert.Equal(catalogue.Categories.Count, body.GetProperty("categories").GetInt32());
    }

    [Fact]
    public async Task Export_carries_the_whole_shop()
    {
        var catalogue = await CatalogueAsync();
        var body = await GetAdminJsonAsync("/api/admin/database/export");

        Assert.Equal(
            catalogue.Products.Count,
            body.GetProperty("catalogue").GetProperty("products").GetArrayLength());

        foreach (var section in new[] { "orders", "enquiries", "messages", "subscribers", "stockIntake" })
            Assert.Equal(JsonValueKind.Array, body.GetProperty(section).ValueKind);
    }

    [Fact]
    public async Task The_database_endpoints_are_closed_to_an_anonymous_caller()
    {
        await AssertStatusAsync("/api/admin/database", HttpStatusCode.Unauthorized);
        await AssertStatusAsync("/api/admin/database/backends", HttpStatusCode.Unauthorized);
        await AssertStatusAsync("/api/admin/database/export", HttpStatusCode.Unauthorized);
        await AssertStatusAsync("/api/admin/subscribers", HttpStatusCode.Unauthorized);
    }

    /* ---------------------------------------------------------------------- */
    /* The newsletter list                                                     */
    /* ---------------------------------------------------------------------- */

    [Fact]
    public async Task A_subscriber_appears_on_the_admin_list_and_can_be_removed()
    {
        var email = $"priya.{Guid.NewGuid():n}@example.com";

        var (subscribed, _) = await PostAsync("/api/newsletter/subscribe", new { email });
        Assert.Equal(HttpStatusCode.Created, subscribed);

        var listed = await GetAdminJsonAsync($"/api/admin/subscribers?q={Uri.EscapeDataString(email)}");
        Assert.Equal(1, listed.GetProperty("total").GetInt32());
        Assert.Equal(email, listed.GetProperty("items")[0].GetProperty("email").GetString());

        var removed = await DeleteAdminAsync($"/api/admin/subscribers/{Uri.EscapeDataString(email)}");
        Assert.Equal(HttpStatusCode.NoContent, removed);

        var afterwards = await GetAdminJsonAsync($"/api/admin/subscribers?q={Uri.EscapeDataString(email)}");
        Assert.Equal(0, afterwards.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Removing_somebody_who_was_never_subscribed_is_a_404()
    {
        var status = await DeleteAdminAsync(
            $"/api/admin/subscribers/{Uri.EscapeDataString($"nobody.{Guid.NewGuid():n}@example.com")}");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    /// <summary>
    /// The public unsubscribe link cannot double as a way of asking whether an
    /// address is on the shop's list, so both answers look the same.
    /// </summary>
    [Fact]
    public async Task Unsubscribing_answers_the_same_whether_or_not_you_were_on_the_list()
    {
        var email = $"anand.{Guid.NewGuid():n}@example.com";

        await PostAsync("/api/newsletter/subscribe", new { email });

        var first = await Client.PostAsJsonAsync("/api/newsletter/unsubscribe", new { email }, Json);
        var second = await Client.PostAsJsonAsync("/api/newsletter/unsubscribe", new { email }, Json);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Unsubscribing_still_rejects_something_that_is_not_an_email()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/newsletter/unsubscribe", new { email = "not-an-address" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
