using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The phones that ring when an order is placed — see <see cref="PushDeviceStore"/>.
///
/// Behind the admin passcode like the rest of the admin: registering a token is
/// asking to be told about every customer's order, which is not something to
/// let an anonymous caller do.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/devices")]
public sealed class AdminDevicesController(PushDeviceStore devices, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PushDevice>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PushDevice>> List() => Ok(devices.All());

    /// <summary>
    /// Adds a phone, or refreshes one already on the list. The app calls this
    /// on every launch, so repeating it is expected and harmless.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<PushDevice>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PushDevice> Register([FromBody] PushDeviceWrite body) =>
        Ok(devices.Register(body.Token.Trim(), body.Platform?.Trim(), body.Name?.Trim(), clock.GetUtcNow()));

    /// <summary>Takes a phone off the list — the app does this when it signs out.</summary>
    [HttpDelete("{token}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Remove(string token)
    {
        if (devices.Remove(token)) return NoContent();

        return Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not registered",
            detail: "That device is not on the push list.");
    }
}
