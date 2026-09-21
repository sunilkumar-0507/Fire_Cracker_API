using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(CatalogStore catalog, ProductQueryService query) : ControllerBase
{
    /// <summary>
    /// The catalogue, filtered, sorted and paged. Accepts the storefront's own
    /// URL parameters (<c>q, category, tag, min, max, rating, stock, sort</c>),
    /// so a shareable catalogue link works here unchanged.
    /// </summary>
    [HttpGet(Name = "ListProducts")]
    [ProducesResponseType<PagedResult<Product>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PagedResult<Product>> List([FromQuery] ProductQuery request)
    {
        if (!ProductQueryService.IsKnownSort(request.Sort))
        {
            return ValidationProblem(
                $"Unknown sort '{request.Sort}'. Valid values: " +
                string.Join(", ", ProductQueryService.SortOptions.Select(o => o.Value)));
        }

        if (!string.IsNullOrWhiteSpace(request.Category) &&
            !request.Category.Equals("all", StringComparison.OrdinalIgnoreCase) &&
            catalog.FindCategory(request.Category) is null)
        {
            return ValidationProblem($"Unknown category '{request.Category}'.");
        }

        var clamped = request.Clamped();
        var matches = query.Filter(clamped);
        return Ok(PagedResult<Product>.From(matches, clamped.Page, clamped.PageSize));
    }

    /// <summary>Sort options the catalogue UI offers, in display order.</summary>
    [HttpGet("sort-options")]
    [ProducesResponseType<IReadOnlyList<SortOption>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SortOption>> SortOptions() =>
        Ok(ProductQueryService.SortOptions);

    /// <summary>Every distinct tag in the catalogue with usage counts, most used first.</summary>
    [HttpGet("tags")]
    [ProducesResponseType<IReadOnlyList<TagCount>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TagCount>> Tags() => Ok(catalog.Tags);

    /// <summary>Lowest and highest price in the catalogue — the filter slider's range.</summary>
    [HttpGet("price-bounds")]
    [ProducesResponseType<PriceBounds>(StatusCodes.Status200OK)]
    public ActionResult<PriceBounds> Bounds() => Ok(catalog.PriceBounds);

    [HttpGet("featured")]
    [ProducesResponseType<IReadOnlyList<Product>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Product>> Featured([FromQuery] int? limit) =>
        Ok(Limit(catalog.FeaturedProducts, limit));

    [HttpGet("best-sellers")]
    [ProducesResponseType<IReadOnlyList<Product>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Product>> BestSellers([FromQuery] int? limit) =>
        Ok(Limit(catalog.BestSellers, limit));

    [HttpGet("new-arrivals")]
    [ProducesResponseType<IReadOnlyList<Product>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Product>> NewArrivals([FromQuery] int? limit) =>
        Ok(Limit(catalog.NewArrivals, limit));

    /// <summary>A single product by slug or id — <c>royal-gold-sparkler-30cm</c> or <c>p-001</c>.</summary>
    [HttpGet("{slugOrId}")]
    [ProducesResponseType<Product>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Product> Get(string slugOrId)
    {
        var product = catalog.FindProduct(slugOrId);
        return product is null ? NotFoundProblem(slugOrId) : Ok(product);
    }

    /// <summary>Same category first, then anything sharing a tag. Never the input product.</summary>
    [HttpGet("{slugOrId}/related")]
    [ProducesResponseType<IReadOnlyList<Product>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<Product>> Related(string slugOrId, [FromQuery] int limit = 4)
    {
        var product = catalog.FindProduct(slugOrId);
        if (product is null) return NotFoundProblem(slugOrId);

        return Ok(catalog.GetRelated(product, Math.Clamp(limit, 1, 24)));
    }

    private ActionResult NotFoundProblem(string slugOrId) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Product not found",
        detail: $"No product with the slug or id '{slugOrId}'.");

    private static IReadOnlyList<Product> Limit(IReadOnlyList<Product> source, int? limit) =>
        limit is > 0 ? [.. source.Take(limit.Value)] : source;
}
