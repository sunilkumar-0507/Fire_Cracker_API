using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(
    PricingService pricing,
    OrderStore store,
    AnalyticsStore analytics,
    INotificationSender notifications,
    IOptions<StorefrontOptions> options) : ControllerBase
{
    private readonly StorefrontOptions _options = options.Value;

    /// <summary>
    /// Places an order. The basket is re-priced server-side from ids and
    /// quantities, so the totals on the confirmation are the API's own, not the
    /// client's.
    ///
    /// No card, UPI or bank detail is accepted, stored or transmitted — the
    /// request carries a payment *method* and nothing more.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<Order>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Order>> Place([FromBody] OrderRequest request)
    {
        var method = request.Payment?.Trim().ToLowerInvariant();
        if (!_options.PaymentMethods.Any(p => p.Id.Equals(method, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(request.Payment),
                "Choose one of: " + string.Join(", ", _options.PaymentMethods.Select(p => p.Id)));
        }

        var pickup = Fulfilment.IsPickup(request.Fulfilment);
        if (pickup && !_options.Pickup.Enabled)
        {
            ModelState.AddModelError(nameof(request.Fulfilment),
                "Collection from the shop is not available at the moment. Choose delivery.");
        }

        // A delivery outside the districts we actually serve is a promise the
        // shop cannot keep, so it is refused here rather than discovered later.
        if (!pickup && !string.IsNullOrWhiteSpace(request.District) &&
            !_options.Districts.Any(d => d.Equals(request.District.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(request.District),
                "Pick a district from GET /api/meta/districts.");
        }

        var outcome = pricing.BuildQuote(request.Items, request.Coupon, request.Fulfilment);
        if (!outcome.Ok)
        {
            foreach (var line in outcome.Unknown)
                ModelState.AddModelError($"items.{line.Id}", line.Reason);
        }

        // Cash on delivery is capped at ₹5,000 — the same rule the FAQ states.
        if (outcome.Ok && method == "cod" && outcome.Quote!.Totals.Total > 5000)
        {
            ModelState.AddModelError(nameof(request.Payment),
                $"Cash on delivery is available up to ₹5,000. This order totals ₹{outcome.Quote.Totals.Total:N0}.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(ModelState)
            {
                Title = "The order could not be placed",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var order = store.PlaceOrder(request, outcome.Quote!);

        analytics.Record(
            AnalyticsEvents.OrderPlaced,
            order.OrderId,
            order.Fulfilment,
            order.Totals.Total,
            request.Session);

        // The customer already has their reference number in the response body;
        // an email that will not send must not turn a placed order into a 500.
        await notifications.OrderPlacedAsync(order, HttpContext.RequestAborted);

        return CreatedAtAction(nameof(Track), new { orderId = order.OrderId }, order);
    }

    /// <summary>
    /// Looks up a placed order by reference and the phone number it was placed
    /// with — the customer-facing tracking view, requirement 9.
    ///
    /// The phone number is the whole access check. Order ids are short and
    /// sequential enough to guess, so an endpoint that served a full order from
    /// the id alone would hand anybody the shop's address book. A wrong number
    /// and an unknown id return the same 404 for the same reason.
    /// </summary>
    [HttpGet("{orderId}/track")]
    [ProducesResponseType<OrderTracking>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OrderTracking> Track(string orderId, [FromQuery] string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return ValidationProblem(
                "Add the mobile number the order was placed with, e.g. ?phone=9842011994.");
        }

        var tracking = store.Track(orderId, phone);
        return tracking is null ? NotFoundProblem(orderId) : Ok(tracking);
    }

    private ActionResult NotFoundProblem(string orderId) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Order not found",
        detail: $"No order matches the reference '{orderId}' and that mobile number.");
}
