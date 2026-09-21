using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The newsletter list.
///
/// <c>POST /api/newsletter/subscribe</c> has always accepted addresses, and
/// until now nothing could read them back — the count on the health endpoint
/// was the only evidence the list existed. A list a shop cannot see is a list
/// it cannot use, and one it cannot remove anybody from is one it should not be
/// keeping.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/subscribers")]
public sealed class AdminSubscribersController(OrderStore store) : ControllerBase
{
    /// <summary>Every subscriber, newest first, optionally narrowed by address.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<Subscription>>(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<Subscription>> List(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        IEnumerable<Subscription> matches = store.AllSubscribers();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            matches = matches.Where(s => s.Email.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return Ok(PagedResult<Subscription>.From(
            [.. matches], Math.Max(1, page), Math.Clamp(pageSize, 1, 500)));
    }

    /// <summary>
    /// Removes an address.
    ///
    /// The route carries the email, so it is URL-encoded on the way in —
    /// <c>DELETE /api/admin/subscribers/priya%40example.com</c>.
    /// </summary>
    [HttpDelete("{email}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Remove(string email)
    {
        if (store.Unsubscribe(email)) return NoContent();

        return Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not on the list",
            detail: $"'{email}' is not subscribed.");
    }
}

/// <summary>
/// Unsubscribing from the shop's side of the wire.
///
/// Public, because the link in a newsletter has to work for somebody who is not
/// signed into anything — that is the whole point of an unsubscribe link. It
/// answers 204 whether or not the address was on the list: a different answer
/// for "was subscribed" would turn this into a way to test whether an address
/// is on the shop's list.
/// </summary>
[ApiController]
[Route("api/newsletter")]
public sealed class NewsletterUnsubscribeController(OrderStore store) : ControllerBase
{
    [HttpPost("unsubscribe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Unsubscribe([FromBody] SubscribeRequest request)
    {
        store.Unsubscribe(request.Email);
        return NoContent();
    }
}
