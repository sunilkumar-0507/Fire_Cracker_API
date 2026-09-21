using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The whole catalogue in one response.
///
/// The storefront's data module is synchronous by design — every page reads
/// <c>products</c>, <c>categories</c> and friends at render time with no
/// awaiting. Rather than unpick that into thirty loading states, the app makes
/// this one call before it mounts and hydrates the module from the result. One
/// round trip on boot, and the rest of the app carries on as it was written.
/// </summary>
[ApiController]
[Route("api/bootstrap")]
public sealed class BootstrapController(CatalogStore catalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<Bootstrap>(StatusCodes.Status200OK)]
    public ActionResult<Bootstrap> Get() => Ok(new Bootstrap(
        Products: catalog.Products,
        Categories: catalog.Categories,
        Combos: catalog.Combos,
        Offers: catalog.Offers,
        Banners: catalog.Banners,
        Testimonials: catalog.Testimonials,
        Faqs: catalog.Faqs));
}
