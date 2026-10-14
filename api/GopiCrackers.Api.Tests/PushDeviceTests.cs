using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The shop admin app's push list, and the push an order sends to it.
/// Expo itself is <see cref="FakeExpoPush"/> — see the note on the fixture.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PushDeviceTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private readonly ApiFixture _fixture = fixture;

    private static string NewToken(string tag = "") => $"ExponentPushToken[{tag}{Guid.NewGuid():n}]";

    private static object Order() => new
    {
        name = "Meenakshi Raghavan",
        phone = "9842011994",
        address = "12 Second Cross Street, Adyar",
        city = "Chennai",
        district = "Chennai",
        pincode = "600020",
        payment = "upi",
        items = new[] { new { id = "p-002", qty = 2 } },
    };

    private async Task<List<string>> RegisteredTokensAsync() =>
        [.. (await GetAdminJsonAsync("/api/admin/devices")).EnumerateArray().Select(d => d.GetProperty("token").GetString()!)];

    [Fact]
    public async Task A_device_registers_once_however_often_it_asks_and_can_be_removed()
    {
        var token = NewToken();

        var (first, body) = await PostAdminAsync("/api/admin/devices", new { token, platform = "android", name = "Counter phone" });
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal("Counter phone", body.GetProperty("name").GetString());

        // The app registers on every launch.
        var (again, _) = await PostAdminAsync("/api/admin/devices", new { token, platform = "android", name = "Counter phone" });
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Single(await RegisteredTokensAsync(), t => t == token);

        Assert.Equal(HttpStatusCode.NoContent, await DeleteAdminAsync($"/api/admin/devices/{Uri.EscapeDataString(token)}"));
        Assert.DoesNotContain(token, await RegisteredTokensAsync());
        Assert.Equal(HttpStatusCode.NotFound, await DeleteAdminAsync($"/api/admin/devices/{Uri.EscapeDataString(token)}"));
    }

    [Fact]
    public async Task Something_that_is_not_an_Expo_token_is_refused()
    {
        var (status, _) = await PostAdminAsync("/api/admin/devices", new { token = "not-a-token" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Registering_needs_the_passcode()
    {
        var payload = JsonSerializer.Serialize(new { token = NewToken() });
        var response = await Client.PostAsync("/api/admin/devices",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Placing_an_order_pushes_it_to_every_registered_phone()
    {
        var token = NewToken();
        await PostAdminAsync("/api/admin/devices", new { token, platform = "ios" });

        try
        {
            var (status, order) = await PostAsync("/api/orders", Order());
            Assert.Equal(HttpStatusCode.Created, status);
            var orderId = order.GetProperty("orderId").GetString()!;

            var message = _fixture.Push.MessagesTo(token).Single();
            Assert.StartsWith($"New order {orderId}", message.GetProperty("title").GetString());
            Assert.Contains("Meenakshi Raghavan", message.GetProperty("body").GetString());
            Assert.Equal(orderId, message.GetProperty("data").GetProperty("orderId").GetString());
            Assert.Equal("orders", message.GetProperty("channelId").GetString());
            Assert.Equal("default", message.GetProperty("sound").GetString());
        }
        finally
        {
            await DeleteAdminAsync($"/api/admin/devices/{Uri.EscapeDataString(token)}");
        }
    }

    [Fact]
    public async Task A_bulk_enquiry_pushes_it_to_every_registered_phone()
    {
        var token = NewToken();
        await PostAdminAsync("/api/admin/devices", new { token, platform = "android" });

        try
        {
            var (status, enquiry) = await PostAsync("/api/bulk-enquiries", new
            {
                name = "Senthil Kumar",
                organisation = "Tiruppur Knitwear Ltd",
                phone = "9842011997",
                district = "Tiruppur",
                quantity = "180 boxes",
                message = "Gift boxes for factory staff, GST invoice needed.",
            });
            Assert.Equal(HttpStatusCode.Created, status);
            var enquiryId = enquiry.GetProperty("enquiryId").GetString()!;

            var message = _fixture.Push.MessagesTo(token).Single();
            Assert.Equal($"Bulk enquiry {enquiryId}", message.GetProperty("title").GetString());
            Assert.Equal(
                "Senthil Kumar (Tiruppur Knitwear Ltd) — Tiruppur — 180 boxes",
                message.GetProperty("body").GetString());
            Assert.Equal(enquiryId, message.GetProperty("data").GetProperty("enquiryId").GetString());
            Assert.Equal("orders", message.GetProperty("channelId").GetString());
        }
        finally
        {
            await DeleteAdminAsync($"/api/admin/devices/{Uri.EscapeDataString(token)}");
        }
    }

    [Fact]
    public async Task A_token_Expo_no_longer_knows_is_dropped_after_the_next_order()
    {
        var dead = NewToken(FakeExpoPush.DeadMarker);
        await PostAdminAsync("/api/admin/devices", new { token = dead });

        var (status, _) = await PostAsync("/api/orders", Order());
        Assert.Equal(HttpStatusCode.Created, status);

        Assert.Single(_fixture.Push.MessagesTo(dead));
        Assert.DoesNotContain(dead, await RegisteredTokensAsync());
    }
}

/// <summary>
/// Answers like <c>exp.host/--/api/v2/push/send</c>: one ticket per message, in
/// order. A token containing <see cref="DeadMarker"/> gets the
/// <c>DeviceNotRegistered</c> error an uninstalled app would.
/// </summary>
public sealed class FakeExpoPush : HttpMessageHandler
{
    public const string DeadMarker = "Dead";

    private readonly ConcurrentQueue<JsonElement> _messages = new();

    public IReadOnlyList<JsonElement> MessagesTo(string token) =>
        [.. _messages.Where(m => m.GetProperty("to").GetString() == token)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
        var tickets = new List<object>();

        foreach (var message in sent.EnumerateArray())
        {
            _messages.Enqueue(message.Clone());
            tickets.Add(message.GetProperty("to").GetString()!.Contains(DeadMarker)
                ? new { status = "error", message = "not registered", details = new { error = "DeviceNotRegistered" } }
                : new { status = "ok", id = Guid.NewGuid().ToString() });
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { data = tickets }), Encoding.UTF8, "application/json"),
        };
    }
}
