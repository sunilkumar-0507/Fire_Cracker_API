using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Controllers;

/* -------------------------------------------------------------------------- */
/* Bulk enquiries                                                              */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/bulk-enquiries")]
public sealed class BulkEnquiriesController(
    OrderStore store,
    AnalyticsStore analytics,
    INotificationSender notifications,
    IOptions<StorefrontOptions> options) : ControllerBase
{
    /// <summary>Submits the bulk / institutional order form.</summary>
    [HttpPost]
    [ProducesResponseType<BulkEnquiry>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BulkEnquiry>> Create([FromBody] BulkEnquiryRequest request)
    {
        var districts = options.Value.Districts;
        if (!districts.Any(d => d.Equals(request.District?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(request.District),
                "Pick a district from GET /api/meta/districts.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(ModelState)
            {
                Title = "The enquiry could not be submitted",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        var enquiry = store.RecordEnquiry(request);

        analytics.Record(
            AnalyticsEvents.Enquiry, enquiry.EnquiryId, enquiry.District, 0, request.Session);
        await notifications.EnquiryReceivedAsync(enquiry, HttpContext.RequestAborted);

        return CreatedAtAction(nameof(Get), new { enquiryId = enquiry.EnquiryId }, enquiry);
    }

    [HttpGet("{enquiryId}")]
    [ProducesResponseType<BulkEnquiry>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BulkEnquiry> Get(string enquiryId)
    {
        var enquiry = store.FindEnquiry(enquiryId);
        return enquiry is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Enquiry not found",
                detail: $"No bulk enquiry with the id '{enquiryId}'.")
            : Ok(enquiry);
    }
}

/* -------------------------------------------------------------------------- */
/* Contact form                                                                */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/contact-messages")]
public sealed class ContactMessagesController(OrderStore store) : ControllerBase
{
    /// <summary>Submits the contact form.</summary>
    [HttpPost]
    [ProducesResponseType<ContactMessage>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ContactMessage> Create([FromBody] ContactMessageRequest request)
    {
        var message = store.RecordMessage(request);
        return CreatedAtAction(nameof(Get), new { messageId = message.MessageId }, message);
    }

    [HttpGet("{messageId}")]
    [ProducesResponseType<ContactMessage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ContactMessage> Get(string messageId)
    {
        var message = store.FindMessage(messageId);
        return message is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Message not found",
                detail: $"No contact message with the id '{messageId}'.")
            : Ok(message);
    }
}

/* -------------------------------------------------------------------------- */
/* Newsletter                                                                  */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/newsletter")]
public sealed class NewsletterController(OrderStore store) : ControllerBase
{
    /// <summary>
    /// Adds an email to the list. Idempotent — subscribing twice returns 200
    /// with <c>alreadySubscribed: true</c> rather than failing.
    /// </summary>
    [HttpPost("subscribe")]
    [ProducesResponseType<Subscription>(StatusCodes.Status201Created)]
    [ProducesResponseType<Subscription>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<Subscription> Subscribe([FromBody] SubscribeRequest request)
    {
        var subscription = store.Subscribe(request.Email);

        return subscription.AlreadySubscribed
            ? Ok(subscription)
            : StatusCode(StatusCodes.Status201Created, subscription);
    }
}
