using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The payment path with no merchant credentials configured — which is the
/// state this repository ships in, and the one most deployments will run in
/// until somebody adds keys.
///
/// The point of these is that "off" is a coherent state rather than a broken
/// one: the shop keeps taking orders, every payment endpoint answers honestly,
/// and nothing can be talked into marking an order paid.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static object ValidOrder(string payment = "cod") => new
    {
        name = "Payment Test",
        phone = "9842011994",
        email = "payment@example.in",
        address = "12 Second Cross Street, Adyar",
        city = "Chennai",
        district = "Chennai",
        pincode = "600020",
        payment,
        items = new[] { new { id = "p-011", qty = 2 } },
    };

    [Fact]
    public async Task Payment_config_says_online_payment_is_off()
    {
        var body = await GetJsonAsync("/api/payments/config");

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("none", body.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task A_new_order_starts_unpaid()
    {
        var (status, order) = await PostAsync("/api/orders", ValidOrder());

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("pending", order.GetProperty("paymentStatus").GetString());
    }

    /// <summary>
    /// 503 rather than 500 or a silent success: there is nothing wrong with the
    /// request, the shop simply has no way to take the money.
    /// </summary>
    [Fact]
    public async Task Opening_a_payment_session_is_unavailable_without_credentials()
    {
        var (_, order) = await PostAsync("/api/orders", ValidOrder());

        var (status, _) = await PostAsync("/api/payments/cashfree/session", new
        {
            orderId = order.GetProperty("orderId").GetString(),
            phone = "9842011994",
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }

    /// <summary>
    /// An unsigned webhook must never be able to mark an order paid. With no
    /// secret configured there is nothing to verify against, so every webhook
    /// is rejected — which is the right way to fail.
    /// </summary>
    [Fact]
    public async Task An_unsigned_webhook_is_rejected()
    {
        var (_, order) = await PostAsync("/api/orders", ValidOrder());
        var orderId = order.GetProperty("orderId").GetString()!;

        var response = await Client.PostAsync("/api/payments/cashfree/webhook",
            JsonContent.Create(new { data = new { order = new { order_id = orderId } } }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And the order is untouched.
        var after = await GetJsonAsync($"/api/payments/status/{orderId}?phone=9842011994");
        Assert.Equal("pending", after.GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task A_forged_signature_is_rejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/cashfree/webhook")
        {
            Content = JsonContent.Create(new { data = new { order = new { order_id = "AC00000000" } } }),
        };
        request.Headers.Add("x-webhook-signature", "not-a-real-signature");
        request.Headers.Add("x-webhook-timestamp", "1700000000");

        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The status endpoint is guarded by the phone number, exactly as the
    /// tracking page is — a short reference on its own must not open an order.
    /// </summary>
    [Fact]
    public async Task Payment_status_needs_the_phone_the_order_was_placed_with()
    {
        var (_, order) = await PostAsync("/api/orders", ValidOrder());
        var orderId = order.GetProperty("orderId").GetString()!;

        await AssertStatusAsync($"/api/payments/status/{orderId}?phone=9842011994", HttpStatusCode.OK);
        await AssertStatusAsync($"/api/payments/status/{orderId}?phone=9000000000", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Payment_status_for_an_unknown_order_is_the_same_404() =>
        await AssertStatusAsync("/api/payments/status/AC99999999?phone=9842011994", HttpStatusCode.NotFound);
}
