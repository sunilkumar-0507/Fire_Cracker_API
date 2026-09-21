using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/* -------------------------------------------------------------------------- */
/* Combo packs                                                                 */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/combos")]
public sealed class CombosController(CatalogStore catalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Combo>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Combo>> List([FromQuery] bool? featured, [FromQuery] string? sort)
    {
        IEnumerable<Combo> result = catalog.Combos;
        if (featured is { } f) result = result.Where(c => c.Featured == f);

        result = sort?.ToLowerInvariant() switch
        {
            "price-asc" => result.OrderBy(c => c.Price),
            "price-desc" => result.OrderByDescending(c => c.Price),
            "saves" => result.OrderByDescending(c => c.Saves),
            _ => result,
        };

        return Ok(result.ToList());
    }

    [HttpGet("featured")]
    [ProducesResponseType<IReadOnlyList<Combo>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Combo>> Featured() => Ok(catalog.FeaturedCombos);

    /// <summary>A single combo by slug or id — <c>family-festival-box</c> or <c>cmb-01</c>.</summary>
    [HttpGet("{slugOrId}")]
    [ProducesResponseType<Combo>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Combo> Get(string slugOrId)
    {
        var combo = catalog.FindCombo(slugOrId);
        return combo is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Combo not found",
                detail: $"No combo with the slug or id '{slugOrId}'.")
            : Ok(combo);
    }
}

/* -------------------------------------------------------------------------- */
/* Offers                                                                      */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/offers")]
public sealed class OffersController(CatalogStore catalog) : ControllerBase
{
    /// <summary>
    /// Festival offers. Each carries <c>resolvedEndsAt</c>, which rolls a lapsed
    /// <c>endsAt</c> forward by <c>fallbackHours</c> so a countdown never shows
    /// a dead timer.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Offer>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Offer>> List([FromQuery] bool? featured, [FromQuery] bool? active)
    {
        IEnumerable<Offer> result = catalog.Offers;
        if (featured is { } f) result = result.Where(o => o.Featured == f);
        if (active is { } a) result = result.Where(o => o.Expired != a);

        return Ok(result.ToList());
    }

    [HttpGet("featured")]
    [ProducesResponseType<IReadOnlyList<Offer>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Offer>> Featured() => Ok(catalog.FeaturedOffers);

    /// <summary>One offer by its coupon code, e.g. <c>DIWALI75</c>.</summary>
    [HttpGet("{code}")]
    [ProducesResponseType<Offer>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Offer> Get(string code)
    {
        var offer = catalog.FindOffer(code);
        return offer is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Offer not found",
                detail: $"No offer with the code '{code}'.")
            : Ok(offer);
    }
}

/* -------------------------------------------------------------------------- */
/* Banners                                                                     */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/banners")]
public sealed class BannersController(CatalogStore catalog) : ControllerBase
{
    /// <summary>Marketing banners. Filter by <c>placement</c>: hero, strip, mid, bulk.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Banner>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Banner>> List([FromQuery] string? placement)
    {
        if (string.IsNullOrWhiteSpace(placement)) return Ok(catalog.Banners);

        return Ok(catalog.Banners
            .Where(b => b.Placement.Equals(placement, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    [HttpGet("{id}")]
    [ProducesResponseType<Banner>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Banner> Get(string id)
    {
        var banner = catalog.Banners
            .FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        return banner is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Banner not found",
                detail: $"No banner with the id '{id}'.")
            : Ok(banner);
    }
}

/* -------------------------------------------------------------------------- */
/* Testimonials                                                                */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/testimonials")]
public sealed class TestimonialsController(CatalogStore catalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Testimonial>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Testimonial>> List(
        [FromQuery] int? limit, [FromQuery] int? minRating)
    {
        IEnumerable<Testimonial> result = catalog.Testimonials;
        if (minRating is { } r) result = result.Where(t => t.Rating >= r);
        if (limit is > 0) result = result.Take(limit.Value);

        return Ok(result.ToList());
    }
}

/* -------------------------------------------------------------------------- */
/* FAQ                                                                         */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/faqs")]
public sealed class FaqsController(CatalogStore catalog) : ControllerBase
{
    /// <summary>FAQ entries, optionally narrowed to one category.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Faq>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Faq>> List([FromQuery] string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return Ok(catalog.Faqs);

        return Ok(catalog.Faqs
            .Where(f => f.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    /// <summary>The distinct FAQ groupings — Orders, Delivery, Safety, Payments, Products.</summary>
    [HttpGet("categories")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> Categories() =>
        Ok(catalog.Faqs.Select(f => f.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    [HttpGet("{id}")]
    [ProducesResponseType<Faq>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Faq> Get(string id)
    {
        var faq = catalog.Faqs.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        return faq is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "FAQ not found",
                detail: $"No FAQ entry with the id '{id}'.")
            : Ok(faq);
    }
}

/* -------------------------------------------------------------------------- */
/* Search                                                                      */
/* -------------------------------------------------------------------------- */

[ApiController]
[Route("api/search")]
public sealed class SearchController(ProductQueryService query) : ControllerBase
{
    /// <summary>
    /// Scored search across the catalogue, returning product hits and matching
    /// categories together — what the search overlay needs in one round trip.
    /// Every term must match somewhere, so "gold sparkler" narrows properly.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<SearchResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SearchResult> Search(
        [FromQuery] string? q,
        [FromQuery] int limit = 8,
        [FromQuery] int categoryLimit = 3)
    {
        if (string.IsNullOrWhiteSpace(q))
            return ValidationProblem("A search term is required — pass ?q=");

        var products = query.SearchProducts(q);
        var categories = query.SearchCategories(q, Math.Clamp(categoryLimit, 0, 20));

        return Ok(new SearchResult(
            Query: q,
            Products: products.Take(Math.Clamp(limit, 1, 100)).ToList(),
            Categories: categories,
            ProductTotal: products.Count));
    }
}
