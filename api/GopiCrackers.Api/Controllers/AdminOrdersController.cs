using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The order book. Orders arrive from the storefront checkout on
/// <c>POST /api/orders</c>; everything here is the shop's side of that.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/orders")]
public sealed class AdminOrdersController(
    OrderStore orders,
    CatalogStore catalog,
    PricingService pricing,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Every order, newest first, optionally narrowed to one status and to one
    /// channel — <c>online</c> (the checkout) or <c>offline</c> (invoices raised here).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<Order>>(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<Order>> List(
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery] string? channel,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        IEnumerable<Order> matches = orders.AllOrders();

        if (!string.IsNullOrWhiteSpace(channel) && !channel.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (channel is not ("online" or "offline"))
                return ValidationProblem($"Unknown channel '{channel}'. Valid: online, offline.");

            matches = matches.Where(o => o.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!OrderStore.IsKnownStatus(status))
                return ValidationProblem($"Unknown status '{status}'. Valid: {string.Join(", ", OrderStore.Statuses)}.");

            matches = matches.Where(o => o.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            matches = matches.Where(o =>
                o.OrderId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                o.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                o.Phone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                o.City.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var all = matches.ToList();
        return Ok(PagedResult<Order>.From(all, Math.Max(1, page), Math.Clamp(pageSize, 1, 200)));
    }

    /// <summary>
    /// Raises an invoice for a sale made off the website. Priced from the
    /// catalogue exactly as the checkout is, coupon included, and saved as an
    /// order with an <c>INV</c> id.
    ///
    /// Unlike the checkout, a quantity above what is on the shelf is refused
    /// rather than quietly reduced — the customer is standing at the counter,
    /// and an invoice for fewer boxes than they were handed is wrong.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<Order>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<Order> Create([FromBody] OfflineOrderWrite body)
    {
        if (!OrderStore.IsKnownStatus(body.Status))
            ModelState.AddModelError(nameof(body.Status),
                $"Unknown status '{body.Status}'. Valid: {string.Join(", ", OrderStore.Statuses)}.");

        if (!Fulfilment.IsKnown(body.Fulfilment))
            ModelState.AddModelError(nameof(body.Fulfilment), "Choose delivery or pickup.");

        var outcome = pricing.BuildQuote(body.Items, body.Coupon, body.Fulfilment);
        foreach (var line in outcome.Unknown)
            ModelState.AddModelError($"items.{line.Id}", line.Reason);

        if (outcome.Quote is { } quote)
        {
            foreach (var line in quote.Items.Where(l => l.Capped))
                ModelState.AddModelError($"items.{line.Id}", $"Only {line.Stock} of “{line.Name}” in stock.");

            // A code typed on an invoice that does not apply should stop the
            // save, not vanish — the customer was promised that discount.
            if (!string.IsNullOrWhiteSpace(body.Coupon) && quote.Coupon is null)
            {
                var reason = pricing.ValidateCoupon(body.Coupon, quote.Totals.Subtotal).Message;
                ModelState.AddModelError(nameof(body.Coupon), reason);
            }
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(ModelState)
            {
                Title = "The invoice could not be saved",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var order = orders.RecordOfflineOrder(body, outcome.Quote!);
        return CreatedAtAction(nameof(Get), new { orderId = order.OrderId }, order);
    }

    [HttpGet("statuses")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> KnownStatuses() => Ok(OrderStore.Statuses);

    [HttpGet("{orderId}")]
    [ProducesResponseType<Order>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Order> Get(string orderId)
    {
        var order = orders.FindOrder(orderId);
        return order is null ? NotFoundProblem(orderId) : Ok(order);
    }

    [HttpPatch("{orderId}/status")]
    [ProducesResponseType<Order>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Order> SetStatus(string orderId, [FromBody] OrderStatusWrite body)
    {
        if (!OrderStore.IsKnownStatus(body.Status))
            return ValidationProblem($"Unknown status '{body.Status}'. Valid: {string.Join(", ", OrderStore.Statuses)}.");

        var updated = orders.SetStatus(orderId, body.Status, body.Note);
        return updated is null ? NotFoundProblem(orderId) : Ok(updated);
    }

    /// <summary>
    /// Deletes an order outright. Unlike cancelling, nothing is left behind —
    /// the order drops off the list, the dashboard and the tracking page.
    /// </summary>
    [HttpDelete("{orderId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(string orderId) =>
        orders.DeleteOrder(orderId) ? NoContent() : NotFoundProblem(orderId);

    /// <summary>Everything the admin dashboard puts on screen, in one call.</summary>
    [HttpGet("/api/admin/summary")]
    [ProducesResponseType<AdminSummary>(StatusCodes.Status200OK)]
    public ActionResult<AdminSummary> Summary()
    {
        var all = orders.AllOrders();

        // Cancelled orders are still in the book but are not money taken.
        var revenue = all
            .Where(o => !o.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            .Sum(o => o.Totals.Total);

        var lowStock = catalog.Products
            .Where(p => p.Active && p.Stock <= 20)
            .OrderBy(p => p.Stock)
            .Take(12)
            .ToList();

        var enquiries = orders.AllEnquiries();
        var weekAgo = clock.GetUtcNow().AddDays(-7);
        var thisWeek = all.Where(o => o.PlacedAt >= weekAgo).ToList();

        return Ok(new AdminSummary(
            Products: catalog.Products.Count,
            Categories: catalog.Categories.Count,
            Combos: catalog.Combos.Count,
            Offers: catalog.Offers.Count,
            Orders: all.Count,
            Revenue: revenue,
            // Anything not yet completed or cancelled still needs somebody to do
            // something about it — that is what the dashboard tile counts.
            PendingOrders: all.Count(o => OrderStore.OpenStatuses.Contains(o.Status)),
            OutOfStock: catalog.Products.Count(p => p.Active && p.Stock == 0),
            LowStock: catalog.Products.Count(p => p.Active && p.Stock is > 0 and <= 20),
            RecentOrders: [.. all.Take(6)],
            LowStockProducts: lowStock,
            ByStatus:
            [
                .. OrderStore.Statuses.Select(s =>
                    new StatusCount(s, all.Count(o => o.Status.Equals(s, StringComparison.OrdinalIgnoreCase))))
            ],
            Unavailable: catalog.Products.Count(p => !p.Active),
            OpenEnquiries: enquiries.Count(e => e.Status is "received" or "quoted"),
            Enquiries: enquiries.Count,
            RecentEnquiries: [.. enquiries.Take(6)],
            OrdersThisWeek: thisWeek.Count,
            RevenueThisWeek: thisWeek
                .Where(o => !o.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                .Sum(o => o.Totals.Total)));
    }

    private ActionResult NotFoundProblem(string orderId) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Order not found",
        detail: $"No order with the id '{orderId}'.");
}
