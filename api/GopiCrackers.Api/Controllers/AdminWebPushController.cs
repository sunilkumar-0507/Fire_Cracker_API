using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The browsers that pop up a notification when an order or a bulk enquiry
/// comes in — see <see cref="WebPushSubscriptionStore"/>. The browser
/// counterpart of <see cref="AdminDevicesController"/>, behind the passcode for
/// the same reason.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/web-push")]
public sealed class AdminWebPushController(
    WebPushSubscriptionStore subscriptions,
    VapidKeys vapid,
    WebPushClient push,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// The push services browsers actually use. The API POSTs to whatever
    /// endpoint is registered, so it only accepts these — a subscription cannot
    /// turn it into a way to call some other server.
    /// </summary>
    private static readonly string[] PushServices =
    [
        "fcm.googleapis.com",              // Chrome, Edge on Android, Samsung Internet, Opera
        "push.services.mozilla.com",       // Firefox
        "notify.windows.com",              // Edge on Windows
        "push.apple.com",                  // Safari, and iPhone home-screen web apps
    ];

    /// <summary>The <c>applicationServerKey</c> to subscribe with.</summary>
    [HttpGet("key")]
    [ProducesResponseType<WebPushKey>(StatusCodes.Status200OK)]
    public ActionResult<WebPushKey> Key() => Ok(new WebPushKey(vapid.PublicKey));

    [HttpGet("subscriptions")]
    [ProducesResponseType<IReadOnlyList<WebPushSubscription>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<WebPushSubscription>> List() => Ok(subscriptions.All());

    /// <summary>
    /// Adds a browser, or refreshes one already on the list. The admin site
    /// calls this each time it is opened, so repeating it is expected.
    /// </summary>
    [HttpPost("subscriptions")]
    [ProducesResponseType<WebPushSubscription>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<WebPushSubscription> Subscribe([FromBody] WebPushSubscriptionWrite body)
    {
        var endpoint = body.Endpoint.Trim();
        if (!IsPushService(endpoint))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not a push service",
                detail: "That endpoint is not a browser push service this API sends to.");
        }

        var keys = body.Keys!;
        if (!IsKey(keys.P256dh, 65) || !IsKey(keys.Auth, 16))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad subscription keys",
                detail: "The p256dh key or the auth secret is not what a browser sends.");
        }

        return Ok(subscriptions.Register(endpoint, keys.P256dh, keys.Auth, body.Name?.Trim(), clock.GetUtcNow()));
    }

    /// <summary>
    /// Takes a browser off the list — the admin site does this when notifications
    /// are switched off. The endpoint is a URL, so it goes in the query.
    /// </summary>
    [HttpDelete("subscriptions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Unsubscribe([FromQuery] string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) && subscriptions.Remove(endpoint.Trim())) return NoContent();

        return Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not subscribed",
            detail: "That browser is not on the notification list.");
    }

    /// <summary>Sends a test notification to every subscribed browser.</summary>
    [HttpPost("test")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Test()
    {
        await push.SendAsync(
            "Notifications are on",
            "New orders and bulk enquiries will show up here.",
            "/orders",
            "test",
            new { test = true },
            HttpContext.RequestAborted);

        return Accepted(new { sentTo = subscriptions.All().Count });
    }

    private static bool IsPushService(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && PushServices.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool IsKey(string value, int length)
    {
        try
        {
            return System.Buffers.Text.Base64Url.DecodeFromChars(value).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
