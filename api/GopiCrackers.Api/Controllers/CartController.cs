using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

[ApiController]
[Route("api/cart")]
public sealed class CartController(PricingService pricing) : ControllerBase
{
    /// <summary>
    /// Prices a basket. Send ids and quantities only — every rupee is re-read
    /// from the catalogue, so a tampered basket cannot change what an order
    /// costs. Quantities above stock are capped and reported in <c>notices</c>.
    /// </summary>
    [HttpPost("quote")]
    [ProducesResponseType<CartQuote>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<CartQuote> Quote([FromBody] CartQuoteRequest request)
    {
        var outcome = pricing.BuildQuote(request.Items, request.Coupon, request.Fulfilment);

        if (!outcome.Ok)
        {
            foreach (var line in outcome.Unknown)
                ModelState.AddModelError(line.Id, line.Reason);

            return ValidationProblem(new ValidationProblemDetails(ModelState)
            {
                Title = "Some cart lines could not be priced",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        return Ok(outcome.Quote);
    }
}

[ApiController]
[Route("api/coupons")]
public sealed class CouponsController(PricingService pricing) : ControllerBase
{
    /// <summary>Coupon codes the checkout accepts, with their minimum order values.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AppliedCoupon>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AppliedCoupon>> List() =>
        Ok(pricing.Coupons
            .Select(kv => new AppliedCoupon(kv.Key, kv.Value.Type, kv.Value.Value, kv.Value.MinOrder, kv.Value.Note))
            .OrderBy(c => c.MinOrder)
            .ToList());

    /// <summary>
    /// Checks a code against a subtotal. Always 200 — an unusable coupon is a
    /// result with <c>ok: false</c> and a message the UI can show, not an error.
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType<CouponValidationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<CouponValidationResult> Validate([FromBody] CouponValidationRequest request) =>
        Ok(pricing.ValidateCoupon(request.Code, request.Subtotal));
}

[ApiController]
[Route("api/shipping")]
public sealed class ShippingController(PricingService pricing) : ControllerBase
{
    /// <summary>Delivery thresholds and fees.</summary>
    [HttpGet]
    [ProducesResponseType<ShippingOptions>(StatusCodes.Status200OK)]
    public ActionResult<ShippingOptions> Get() => Ok(pricing.Shipping);
}
