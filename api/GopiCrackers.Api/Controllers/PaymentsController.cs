using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>Which gateway, if any, this shop can take money through.</summary>
public sealed record PaymentConfig(bool Enabled, string Provider, string Mode);

public sealed record PaymentSessionRequest
{
    /// <summary>The reference <c>POST /api/orders</c> already issued.</summary>
    public string OrderId { get; init; } = string.Empty;

    /// <summary>
    /// The phone the order was placed with. This is the access check, exactly as
    /// on the tracking page: order references are short, and an endpoint that
    /// opened a payment from the reference alone would let anyone run up a
    /// payment page against a stranger's order.
    /// </summary>
    public string Phone { get; init; } = string.Empty;
}

/// <summary>
/// Online payment.
///
/// Nothing here creates an order — <c>POST /api/orders</c> does that, and it
/// stays the only way an order comes into being. These endpoints attach a
/// payment to an order that already exists, so a customer who abandons the
/// payment page still has a booking the shop can chase, rather than the order
/// vanishing with the browser tab.
/// </summary>
[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(
    IPaymentGateway gateway,
    OrderStore orders,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// Whether online payment is available, so the checkout can offer it or not.
    /// Public and free of credentials: it says which provider and which mode,
    /// never a key.
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType<PaymentConfig>(StatusCodes.Status200OK)]
    public ActionResult<PaymentConfig> Config() =>
        Ok(new PaymentConfig(gateway.Enabled, gateway.Name, gateway.Enabled ? "live" : "off"));

    /// <summary>
    /// Opens a payment attempt against an existing order and returns the session
    /// the checkout SDK needs.
    /// </summary>
    [HttpPost("cashfree/session")]
    [ProducesResponseType<PaymentSession>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateSession(
        [FromBody] PaymentSessionRequest request,
        CancellationToken cancellation)
    {
        if (!gateway.Enabled)
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Online payment is not available",
                detail: "This shop has no payment provider configured. Cash on delivery and pickup are available.");

        var order = orders.FindOrder(request.OrderId?.Trim() ?? string.Empty);

        // The same 404 for a wrong phone as for an unknown reference, so the
        // response cannot be used to work out which references exist.
        if (order is null || !string.Equals(order.Phone, request.Phone?.Trim(), StringComparison.Ordinal))
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Order not found",
                detail: "No order matches that reference and phone number.");

        if (order.PaymentStatus.Equals(PaymentStatus.Paid, StringComparison.OrdinalIgnoreCase))
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already paid",
                detail: $"Order {order.OrderId} has already been paid for.");

        var result = await gateway.CreateSessionAsync(order, cancellation);

        if (!result.Ok || result.Session is null)
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Could not start the payment",
                detail: result.Message);

        orders.SetPaymentStatus(order.OrderId, PaymentStatus.Processing, result.Session.OrderReference);
        return Ok(result.Session);
    }

    /// <summary>
    /// Cashfree's callback.
    ///
    /// Read as raw text, because the signature covers the exact bytes sent and
    /// model binding would re-serialise them into something that no longer
    /// matches. A bad signature is a flat 401 with no detail — whoever sent it
    /// is not Cashfree, and they learn nothing from the reply.
    ///
    /// The body says what happened; the gateway is asked anyway. A webhook is a
    /// notification, not evidence, and marking an order paid is not something to
    /// do on an unverified claim.
    /// </summary>
    [HttpPost("cashfree/webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Webhook(CancellationToken cancellation)
    {
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync(cancellation);

        var signature = Request.Headers["x-webhook-signature"].ToString();
        var timestamp = Request.Headers["x-webhook-timestamp"].ToString();

        if (!gateway.VerifyWebhook(raw, signature, timestamp))
        {
            logger.LogWarning("Rejected a payment webhook with an invalid signature");
            return Unauthorized();
        }

        string? orderId = null;
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(raw);
            if (parsed.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("order", out var order)
                && order.TryGetProperty("order_id", out var id))
            {
                orderId = id.GetString();
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogError(ex, "A signed payment webhook did not contain readable JSON");
            return Ok();
        }

        if (string.IsNullOrWhiteSpace(orderId)) return Ok();

        var status = await gateway.GetPaymentStatusAsync(orderId, cancellation);
        orders.SetPaymentStatus(orderId, status, orderId);

        logger.LogInformation("Payment webhook: order {OrderId} is {Status}", orderId, status);

        // Always 200 once the signature holds. A gateway retries anything else,
        // and re-delivering a webhook we have already applied helps nobody.
        return Ok();
    }

    /// <summary>
    /// What the storefront calls when the customer lands back from the payment
    /// page. The webhook is the source of truth, but it can arrive after the
    /// customer does, so this asks the gateway directly rather than showing a
    /// receipt that says "pending" for a payment that went through.
    /// </summary>
    [HttpGet("status/{orderId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(string orderId, [FromQuery] string phone, CancellationToken cancellation)
    {
        var order = orders.FindOrder(orderId?.Trim() ?? string.Empty);

        if (order is null || !string.Equals(order.Phone, phone?.Trim(), StringComparison.Ordinal))
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Order not found",
                detail: "No order matches that reference and phone number.");

        if (gateway.Enabled && !order.PaymentStatus.Equals(PaymentStatus.Paid, StringComparison.OrdinalIgnoreCase))
        {
            var live = await gateway.GetPaymentStatusAsync(order.OrderId, cancellation);
            if (!live.Equals(order.PaymentStatus, StringComparison.OrdinalIgnoreCase))
                order = orders.SetPaymentStatus(order.OrderId, live, order.OrderId) ?? order;
        }

        return Ok(new { order.OrderId, order.Status, order.PaymentStatus, order.PaymentReference, order.Totals });
    }
}
