using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The enquiry book — requirement 11's "orders <em>or enquiries</em>".
///
/// Bulk enquiries arrive from the storefront's bulk-order form and contact
/// messages from the contact page. Both are things somebody has to answer, so
/// both belong where the shop already looks.
///
/// Unlike orders, these are not journalled to disk: an enquiry that has been
/// answered is answered, and keeping a permanent file of names and phone
/// numbers is a liability the shop did not ask for. They live as long as the
/// process does, and the notification email is the durable copy.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/enquiries")]
public sealed class AdminEnquiriesController(OrderStore store) : ControllerBase
{
    /// <summary>Every bulk enquiry, newest first, optionally narrowed to one status.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<BulkEnquiry>>(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<BulkEnquiry>> List(
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        IEnumerable<BulkEnquiry> matches = store.AllEnquiries();

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!OrderStore.IsKnownEnquiryStatus(status))
            {
                return ValidationProblem(
                    $"Unknown status '{status}'. Valid: {string.Join(", ", OrderStore.EnquiryStatuses)}.");
            }

            matches = matches.Where(e => e.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            matches = matches.Where(e =>
                e.EnquiryId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Phone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (e.Organisation ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.District.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return Ok(PagedResult<BulkEnquiry>.From(
            [.. matches], Math.Max(1, page), Math.Clamp(pageSize, 1, 200)));
    }

    [HttpGet("statuses")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> KnownStatuses() => Ok(OrderStore.EnquiryStatuses);

    [HttpGet("{enquiryId}")]
    [ProducesResponseType<BulkEnquiry>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BulkEnquiry> Get(string enquiryId)
    {
        var enquiry = store.FindEnquiry(enquiryId);
        return enquiry is null ? NotFoundProblem(enquiryId) : Ok(enquiry);
    }

    [HttpPatch("{enquiryId}/status")]
    [ProducesResponseType<BulkEnquiry>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BulkEnquiry> SetStatus(string enquiryId, [FromBody] EnquiryStatusWrite body)
    {
        if (!OrderStore.IsKnownEnquiryStatus(body.Status))
        {
            return ValidationProblem(
                $"Unknown status '{body.Status}'. Valid: {string.Join(", ", OrderStore.EnquiryStatuses)}.");
        }

        var updated = store.SetEnquiryStatus(enquiryId, body.Status);
        return updated is null ? NotFoundProblem(enquiryId) : Ok(updated);
    }

    /// <summary>Contact-form messages, newest first.</summary>
    [HttpGet("/api/admin/messages")]
    [ProducesResponseType<PagedResult<ContactMessage>>(StatusCodes.Status200OK)]
    public ActionResult<PagedResult<ContactMessage>> Messages(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25) =>
        Ok(PagedResult<ContactMessage>.From(
            store.AllMessages(), Math.Max(1, page), Math.Clamp(pageSize, 1, 200)));

    private ActionResult NotFoundProblem(string id) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Enquiry not found",
        detail: $"No bulk enquiry with the id '{id}'.");
}
