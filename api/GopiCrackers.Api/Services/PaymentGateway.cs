using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>What the storefront needs to open a payment page.</summary>
/// <param name="PaymentSessionId">Handed to Cashfree's checkout SDK.</param>
/// <param name="OrderReference">The gateway's id for this payment attempt.</param>
public sealed record PaymentSession(string PaymentSessionId, string OrderReference, string Mode);

/// <summary>The outcome of a gateway call, with a sentence fit to show a customer.</summary>
public sealed record PaymentResult(bool Ok, string Message, PaymentSession? Session = null);

/// <summary>
/// The seam between this shop and whoever is moving the money.
///
/// One interface so a second gateway, or a test double, is a registration
/// change rather than an edit to the checkout. <see cref="Enabled"/> is the
/// switch the rest of the code asks about: false means "there is no online
/// payment here", and the checkout stays cash-on-delivery.
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }

    bool Enabled { get; }

    /// <summary>Opens a payment attempt for an order that has already been placed.</summary>
    Task<PaymentResult> CreateSessionAsync(Order order, CancellationToken cancellation = default);

    /// <summary>
    /// Checks a webhook really came from the gateway. Takes the raw body, not a
    /// parsed object: the signature is over the exact bytes sent, and
    /// re-serialising JSON changes them.
    /// </summary>
    bool VerifyWebhook(string rawBody, string signature, string timestamp);

    /// <summary>Asks the gateway what actually happened, rather than trusting the webhook body.</summary>
    Task<string> GetPaymentStatusAsync(string orderId, CancellationToken cancellation = default);
}

/// <summary>
/// The gateway used when no credentials are configured.
///
/// It refuses every call with a sentence explaining why, which is the honest
/// behaviour: a shop with no merchant account cannot take a card, and saying so
/// is better than a payment page that fails halfway through.
/// </summary>
public sealed class DisabledPaymentGateway : IPaymentGateway
{
    public string Name => "none";

    public bool Enabled => false;

    private const string Message =
        "Online payment is not configured for this shop. Cash on delivery and pickup are available.";

    public Task<PaymentResult> CreateSessionAsync(Order order, CancellationToken cancellation = default) =>
        Task.FromResult(new PaymentResult(false, Message));

    public bool VerifyWebhook(string rawBody, string signature, string timestamp) => false;

    public Task<string> GetPaymentStatusAsync(string orderId, CancellationToken cancellation = default) =>
        Task.FromResult(Models.PaymentStatus.Pending);
}

/// <summary>
/// Cashfree Payments.
///
/// The flow, in full:
///
/// <list type="number">
/// <item>The customer places an order as they always did. It is real, it has a
/// reference, and it is <c>pending</c> on both goods and money.</item>
/// <item>The storefront asks for a payment session. This creates the matching
/// order at Cashfree and returns a <c>payment_session_id</c>.</item>
/// <item>Cashfree's SDK takes the customer through payment and returns them to
/// <c>ReturnUrl</c>.</item>
/// <item>Cashfree calls the webhook. The signature is checked, then the payment
/// is <em>re-read from their API</em> rather than believed from the body — a
/// webhook is a notification, not proof.</item>
/// </list>
///
/// Amounts are sent in rupees as a decimal, which is what their API expects;
/// every figure comes from the order the API priced, never from the browser.
/// </summary>
public sealed class CashfreePaymentGateway(
    IHttpClientFactory factory,
    IOptions<PaymentOptions> options,
    ILogger<CashfreePaymentGateway> logger) : IPaymentGateway
{
    private readonly CashfreeOptions _cashfree = options.Value.Cashfree;

    public string Name => "cashfree";

    public bool Enabled => _cashfree.Configured;

    private HttpClient Client()
    {
        var client = factory.CreateClient(nameof(CashfreePaymentGateway));
        client.BaseAddress = new Uri(_cashfree.BaseUrl + "/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("x-api-version", _cashfree.ApiVersion);
        client.DefaultRequestHeaders.Add("x-client-id", _cashfree.AppId);
        client.DefaultRequestHeaders.Add("x-client-secret", _cashfree.SecretKey);
        return client;
    }

    public async Task<PaymentResult> CreateSessionAsync(Order order, CancellationToken cancellation = default)
    {
        if (!Enabled)
            return new PaymentResult(false, "Online payment is not configured for this shop.");

        // Their customer id has a tighter character set than a name does.
        var customerId = $"cust_{order.Phone}";

        var payload = new
        {
            order_id = order.OrderId,
            order_amount = (decimal)order.Totals.Total,
            order_currency = "INR",
            customer_details = new
            {
                customer_id = customerId,
                customer_name = order.Name,
                customer_phone = order.Phone,
                customer_email = string.IsNullOrWhiteSpace(order.Email) ? null : order.Email,
            },
            order_meta = new
            {
                // Cashfree substitutes the reference into this placeholder.
                return_url = $"{_cashfree.ReturnUrl}?order_id={{order_id}}",
            },
            order_note = $"SKV Pyros order {order.OrderId}",
        };

        try
        {
            using var client = Client();
            using var response = await client.PostAsJsonAsync("orders", payload, cancellation);
            var body = await response.Content.ReadAsStringAsync(cancellation);

            if (!response.IsSuccessStatusCode)
            {
                // Their message is written for a developer, so it is logged, not
                // shown. The customer gets something they can act on.
                logger.LogError("Cashfree rejected order {OrderId}: {Status} {Body}",
                    order.OrderId, (int)response.StatusCode, body);

                return new PaymentResult(false,
                    "The payment provider could not start this payment. Please try again, or choose cash on delivery.");
            }

            using var parsed = JsonDocument.Parse(body);
            var sessionId = parsed.RootElement.TryGetProperty("payment_session_id", out var s)
                ? s.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                logger.LogError("Cashfree accepted order {OrderId} but returned no payment_session_id: {Body}",
                    order.OrderId, body);

                return new PaymentResult(false,
                    "The payment provider did not return a payment session. Please try again.");
            }

            return new PaymentResult(true, "Payment session created",
                new PaymentSession(sessionId, order.OrderId, _cashfree.Mode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Could not reach Cashfree for order {OrderId}", order.OrderId);
            return new PaymentResult(false,
                "Could not reach the payment provider. Please try again, or choose cash on delivery.");
        }
    }

    /// <summary>
    /// Base64 HMAC-SHA256 over <c>timestamp + rawBody</c>, keyed with the secret.
    /// Compared in fixed time, because a signature check that leaks its progress
    /// through timing is not a signature check.
    /// </summary>
    public bool VerifyWebhook(string rawBody, string signature, string timestamp)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(signature)) return false;

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_cashfree.SecretKey));
            var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + rawBody)));

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(signature));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not verify a Cashfree webhook signature");
            return false;
        }
    }

    public async Task<string> GetPaymentStatusAsync(string orderId, CancellationToken cancellation = default)
    {
        if (!Enabled) return Models.PaymentStatus.Pending;

        try
        {
            using var client = Client();
            using var response = await client.GetAsync($"orders/{Uri.EscapeDataString(orderId)}", cancellation);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Cashfree status lookup for {OrderId} returned {Status}",
                    orderId, (int)response.StatusCode);
                return Models.PaymentStatus.Pending;
            }

            using var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var status = parsed.RootElement.TryGetProperty("order_status", out var s)
                ? s.GetString()
                : null;

            // Their vocabulary, mapped onto ours. Anything unrecognised stays
            // pending rather than being guessed into "paid".
            return status?.ToUpperInvariant() switch
            {
                "PAID" => Models.PaymentStatus.Paid,
                "ACTIVE" => Models.PaymentStatus.Processing,
                "EXPIRED" or "TERMINATED" or "TERMINATION_REQUESTED" => Models.PaymentStatus.Failed,
                _ => Models.PaymentStatus.Pending,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Could not reach Cashfree to check order {OrderId}", orderId);
            return Models.PaymentStatus.Pending;
        }
    }
}
