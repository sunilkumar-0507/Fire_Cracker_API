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
    TimeProvider clock) : ControllerBase
{
    /// <summary>Every order, newest first, optionally narrowed to one status.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<Order>>(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<Order>> List(
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        IEnumerable<Order> matches = orders.AllOrders();

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
