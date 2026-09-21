using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController(CatalogStore catalog, ProductQueryService query) : ControllerBase
{
    /// <summary>
    /// All categories. <c>productCount</c> is derived from the catalogue on every
    /// load rather than trusted from the seed file, so the two cannot drift.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Category>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Category>> List([FromQuery] bool? featured)
    {
        var result = featured is { } f
            ? catalog.Categories.Where(c => c.Featured == f).ToList()
            : catalog.Categories;

        return Ok(result);
    }

    [HttpGet("{slug}")]
    [ProducesResponseType<Category>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Category> Get(string slug)
    {
        var category = catalog.FindCategory(slug);
        return category is null ? NotFoundProblem(slug) : Ok(category);
    }

    /// <summary>
    /// Products in one category. Takes the same filter and sort parameters as
    /// <c>GET /api/products</c>; any <c>category</c> in the query is ignored in
    /// favour of the route.
    /// </summary>
    [HttpGet("{slug}/products")]
    [ProducesResponseType<PagedResult<Product>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PagedResult<Product>> Products(string slug, [FromQuery] ProductQuery request)
    {
        var category = catalog.FindCategory(slug);
        if (category is null) return NotFoundProblem(slug);

        if (!ProductQueryService.IsKnownSort(request.Sort))
        {
            return ValidationProblem(
                $"Unknown sort '{request.Sort}'. Valid values: " +
                string.Join(", ", ProductQueryService.SortOptions.Select(o => o.Value)));
        }

        var clamped = (request with { Category = category.Slug }).Clamped();
        var matches = query.Filter(clamped);
        return Ok(PagedResult<Product>.From(matches, clamped.Page, clamped.PageSize));
    }

    private ActionResult NotFoundProblem(string slug) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Category not found",
        detail: $"No category with the slug '{slug}'.");
}
