using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// Catalogue writes for the admin UI. Every route here is guarded by
/// <see cref="AdminOnlyAttribute"/> and every successful call rewrites the JSON
/// file behind it, so a change is on the storefront the moment it reloads.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin")]
public sealed partial class AdminCatalogController(CatalogStore catalog) : ControllerBase
{
    /* ---------------------------------------------------------------------- */
    /* Products                                                                */
    /* ---------------------------------------------------------------------- */

    [HttpPost("products")]
    [ProducesResponseType<Product>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<Product> CreateProduct([FromBody] ProductWrite body)
    {
        var slug = Slugify(body.Slug ?? body.Name);
        if (catalog.FindProduct(slug) is not null)
            return Conflict($"A product already lives at '{slug}'. Give this one a different name or slug.");

        if (Validate(body, slug, existingId: null) is { } problem) return problem;

        var product = Compose(body, slug, NextProductId(), NextProductCode());
        catalog.SaveProduct(product);

        return CreatedAtAction(nameof(ProductsController.Get), "Products",
            new { slugOrId = product.Slug }, product);
    }

    [HttpPut("products/{id}")]
    [ProducesResponseType<Product>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Product> UpdateProduct(string id, [FromBody] ProductWrite body)
    {
        var existing = catalog.FindProduct(id);
        if (existing is null) return NotFoundProblem("product", id);

        var slug = Slugify(body.Slug ?? body.Name);
        var clash = catalog.FindProduct(slug);
        if (clash is not null && !clash.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
            return Conflict($"'{slug}' is already taken by {clash.Name}.");

        if (Validate(body, slug, existing.Id) is { } problem) return problem;

        var product = Compose(body, slug, existing.Id, existing.Code);
        return Ok(catalog.SaveProduct(product));
    }

    [HttpDelete("products/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteProduct(string id)
    {
        var existing = catalog.FindProduct(id);
        if (existing is null) return NotFoundProblem("product", id);

        // A combo that lists this product would start advertising a bundle the
        // shop can no longer assemble, so the delete is refused until it is out.
        var usedBy = catalog.Combos
            .Where(c => c.Includes.Any(i =>
                i.Slug is not null && i.Slug.Equals(existing.Slug, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name)
            .ToList();

        if (usedBy.Count > 0)
        {
            return Conflict(
                $"{existing.Name} is still in {string.Join(", ", usedBy)}. " +
                "Take it out of those combo packs first.");
        }

        catalog.DeleteProduct(existing.Id);
        return NoContent();
    }

    /// <summary>
    /// Activates or deactivates a product — requirement 10's "activate or
    /// deactivate" without making the shopkeeper re-submit the whole form (and
    /// risk saving a stale copy of every other field alongside the one switch
    /// they meant to flip).
    /// </summary>
    [HttpPatch("products/{id}/active")]
    [ProducesResponseType<Product>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Product> SetProductActive(string id, [FromBody] ProductActiveWrite body)
    {
        var existing = catalog.FindProduct(id);
        if (existing is null) return NotFoundProblem("product", id);

        return Ok(catalog.SaveProduct(existing with { Active = body.Active }));
    }

    /// <summary>Bulk stock take. Unknown ids are reported, not fatal.</summary>
    [HttpPatch("stock")]
    [ProducesResponseType<StockResult>(StatusCodes.Status200OK)]
    public ActionResult<StockResult> UpdateStock([FromBody] StockWrite body)
    {
        var levels = body.Levels
            .GroupBy(l => l.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Stock, StringComparer.OrdinalIgnoreCase);

        var (updated, unknown) = catalog.SetStock(levels);
        return Ok(new StockResult(updated, unknown));
    }

    /* ---------------------------------------------------------------------- */
    /* Categories                                                              */
    /* ---------------------------------------------------------------------- */

    [HttpPost("categories")]
    [ProducesResponseType<Category>(StatusCodes.Status201Created)]
    public ActionResult<Category> CreateCategory([FromBody] CategoryWrite body)
    {
        var slug = Slugify(body.Slug ?? body.Name);
        if (catalog.FindCategory(slug) is not null)
            return Conflict($"A category already lives at '{slug}'.");

        var category = Compose(body, slug, $"cat-{slug}");
        catalog.SaveCategory(category);
        return CreatedAtAction(nameof(CategoriesController.Get), "Categories",
            new { slug = category.Slug }, category);
    }

    [HttpPut("categories/{id}")]
    [ProducesResponseType<Category>(StatusCodes.Status200OK)]
    public ActionResult<Category> UpdateCategory(string id, [FromBody] CategoryWrite body)
    {
        var existing = catalog.Categories.FirstOrDefault(c =>
            c.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            c.Slug.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (existing is null) return NotFoundProblem("category", id);

        var slug = Slugify(body.Slug ?? body.Name);
        var clash = catalog.FindCategory(slug);
        if (clash is not null && !clash.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
            return Conflict($"'{slug}' is already taken by {clash.Name}.");

        // Renaming a slug would orphan every product pointing at the old one.
        if (!slug.Equals(existing.Slug, StringComparison.OrdinalIgnoreCase) &&
            catalog.Products.Any(p => p.Category == existing.Slug))
        {
            return Conflict(
                $"{existing.Name} still holds products under '{existing.Slug}'. " +
                "Move them to another category before changing the slug.");
        }

        return Ok(catalog.SaveCategory(Compose(body, slug, existing.Id)));
    }

    [HttpDelete("categories/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteCategory(string id)
    {
        var existing = catalog.Categories.FirstOrDefault(c =>
            c.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            c.Slug.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (existing is null) return NotFoundProblem("category", id);

        var count = catalog.Products.Count(p => p.Category == existing.Slug);
        if (count > 0)
        {
            return Conflict(
                $"{existing.Name} still holds {count} product{(count == 1 ? "" : "s")}. " +
                "Move or delete those first.");
        }

        catalog.DeleteCategory(existing.Id);
        return NoContent();
    }

    /* ---------------------------------------------------------------------- */
    /* Combos                                                                  */
    /* ---------------------------------------------------------------------- */

    [HttpPost("combos")]
    [ProducesResponseType<Combo>(StatusCodes.Status201Created)]
    public ActionResult<Combo> CreateCombo([FromBody] ComboWrite body)
    {
        var slug = Slugify(body.Slug ?? body.Name);
        if (catalog.FindCombo(slug) is not null)
            return Conflict($"A combo already lives at '{slug}'.");

        var built = BuildCombo(body, slug, $"cmb-{slug}");
        if (built.Problem is { } problem) return problem;

        catalog.SaveCombo(built.Combo!);
        return CreatedAtAction(nameof(CombosController.Get), "Combos",
            new { slugOrId = built.Combo!.Slug }, built.Combo);
    }

    [HttpPut("combos/{id}")]
    [ProducesResponseType<Combo>(StatusCodes.Status200OK)]
    public ActionResult<Combo> UpdateCombo(string id, [FromBody] ComboWrite body)
    {
        var existing = catalog.FindCombo(id);
        if (existing is null) return NotFoundProblem("combo", id);

        var slug = Slugify(body.Slug ?? body.Name);
        var clash = catalog.FindCombo(slug);
        if (clash is not null && !clash.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
            return Conflict($"'{slug}' is already taken by {clash.Name}.");

        var built = BuildCombo(body, slug, existing.Id);
        if (built.Problem is { } problem) return problem;

        return Ok(catalog.SaveCombo(built.Combo!));
    }

    [HttpDelete("combos/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteCombo(string id)
    {
        var existing = catalog.FindCombo(id);
        if (existing is null) return NotFoundProblem("combo", id);

        catalog.DeleteCombo(existing.Id);
        return NoContent();
    }

    /* ---------------------------------------------------------------------- */
    /* Offers                                                                  */
    /* ---------------------------------------------------------------------- */

    [HttpPost("offers")]
    [ProducesResponseType<Offer>(StatusCodes.Status201Created)]
    public ActionResult<Offer> CreateOffer([FromBody] OfferWrite body)
    {
        var code = body.Code.Trim().ToUpperInvariant();
        if (catalog.FindOffer(code) is not null)
            return Conflict($"{code} is already in use.");

        var offer = Compose(body, code, $"off-{Slugify(code)}");
        catalog.SaveOffer(offer);
        return CreatedAtAction(nameof(OffersController.Get), "Offers", new { code = offer.Code }, offer);
    }

    [HttpPut("offers/{id}")]
    [ProducesResponseType<Offer>(StatusCodes.Status200OK)]
    public ActionResult<Offer> UpdateOffer(string id, [FromBody] OfferWrite body)
    {
        var existing = catalog.Offers.FirstOrDefault(o =>
            o.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            o.Code.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (existing is null) return NotFoundProblem("offer", id);

        var code = body.Code.Trim().ToUpperInvariant();
        var clash = catalog.FindOffer(code);
        if (clash is not null && !clash.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
            return Conflict($"{code} is already in use by another offer.");

        return Ok(catalog.SaveOffer(Compose(body, code, existing.Id)));
    }

    [HttpDelete("offers/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteOffer(string id)
    {
        var existing = catalog.Offers.FirstOrDefault(o =>
            o.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            o.Code.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (existing is null) return NotFoundProblem("offer", id);

        catalog.DeleteOffer(existing.Id);
        return NoContent();
    }

    /* ---------------------------------------------------------------------- */
    /* Composition                                                             */
    /* ---------------------------------------------------------------------- */

    private ActionResult? Validate(ProductWrite body, string slug, string? existingId)
    {
        if (catalog.FindCategory(body.Category) is null)
            return Conflict($"There is no category '{body.Category}'.");

        if (body.Mrp < body.Price)
            return Conflict($"MRP (₹{body.Mrp:N0}) cannot be below the selling price (₹{body.Price:N0}).");

        _ = slug;
        _ = existingId;
        return null;
    }

    private static Product Compose(ProductWrite body, string slug, string id, string code) => new(
        Id: id,
        Code: code,
        Slug: slug,
        Name: body.Name.Trim(),
        Category: body.Category.Trim(),
        Brand: string.IsNullOrWhiteSpace(body.Brand) ? "SKV Pyros" : body.Brand.Trim(),
        Price: body.Price,
        Mrp: body.Mrp,
        Discount: Percent(body.Mrp, body.Price),
        Unit: body.Unit.Trim(),
        Description: body.Description.Trim(),
        Highlights: [.. body.Highlights.Select(h => h.Trim()).Where(h => h.Length > 0)],
        Images: [.. body.Images.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct()],
        Stock: body.Stock,
        Tags: [.. body.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct()],
        Specs: body.Specs.ToDictionary(kv => kv.Key, kv => kv.Value),
        Featured: body.Featured,
        BestSeller: body.BestSeller,
        Combo: false,
        IsNew: body.IsNew,
        Active: body.Active);

    private static Category Compose(CategoryWrite body, string slug, string id) => new(
        Id: id,
        Slug: slug,
        Name: body.Name.Trim(),
        TamilName: body.TamilName.Trim(),
        Tagline: body.Tagline.Trim(),
        Description: body.Description.Trim(),
        Art: body.Art.Trim(),
        Accent: Accents.Hex(body.Tone),
        AccentSoft: Accents.Soft(body.Tone),
        ProductCount: 0, // Recomputed from the catalogue on every snapshot build.
        NoiseLevel: body.NoiseLevel.Trim(),
        Featured: body.Featured,
        Tone: Accents.Normalise(body.Tone));

    private static Offer Compose(OfferWrite body, string code, string id) => new(
        Id: id,
        Code: code,
        Title: body.Title.Trim(),
        Subtitle: body.Subtitle.Trim(),
        Description: body.Description.Trim(),
        Type: body.Type.Trim().ToLowerInvariant(),
        Value: body.Value,
        MinOrder: body.MinOrder,
        Art: body.Art.Trim(),
        Accent: Accents.Hex(body.Tone),
        AccentTo: Accents.Soft(body.Tone),
        EndsAt: body.EndsAt,
        FallbackHours: body.FallbackHours,
        Badge: body.Badge.Trim(),
        Featured: body.Featured,
        Terms: [.. body.Terms.Select(t => t.Trim()).Where(t => t.Length > 0)],
        Tone: Accents.Normalise(body.Tone));

    /// <summary>
    /// Prices a bundle from its parts: MRP is the sum of the lines' MRPs, and the
    /// price is the sum of their (already discounted) prices less the bundle
    /// discount. Nothing about a combo's money is taken on trust from the form.
    /// </summary>
    private (Combo? Combo, ActionResult? Problem) BuildCombo(ComboWrite body, string slug, string id)
    {
        var lines = new List<(Product Product, int Qty)>();

        foreach (var line in body.Includes)
        {
            var product = catalog.FindProduct(line.Slug);
            if (product is null)
                return (null, Conflict($"There is no product '{line.Slug}'."));

            lines.Add((product, line.Qty));
        }

        var mrp = lines.Sum(l => l.Product.Mrp * l.Qty);
        var parts = lines.Sum(l => l.Product.Price * l.Qty);
        var price = (int)Math.Round(parts * (1 - body.BundleDiscount / 100.0));
        var count = lines.Sum(l => l.Qty);

        var combo = new Combo(
            Id: id,
            Slug: slug,
            Name: body.Name.Trim(),
            Tagline: body.Tagline.Trim(),
            Description: body.Description.Trim(),
            Art: body.Art.Trim(),
            Accent: Accents.Hex(body.Tone),
            Price: price,
            Mrp: mrp,
            Discount: Percent(mrp, price),
            Saves: mrp - price,
            ItemCount: count,
            Serves: body.Serves.Trim(),
            Duration: body.Duration.Trim(),
            Badge: body.Badge.Trim(),
            Stock: body.Stock,
            Featured: body.Featured,
            Includes: [.. lines.Select(l => new ComboItem(l.Product.Name, l.Qty, l.Product.Slug))],
            Tone: Accents.Normalise(body.Tone));

        return (combo, null);
    }

    /* ---------------------------------------------------------------------- */
    /* Ids and helpers                                                         */
    /* ---------------------------------------------------------------------- */

    /// <summary>Next free <c>p-###</c>, filling gaps left by deletions.</summary>
    private string NextProductId()
    {
        var taken = catalog.Products
            .Select(p => int.TryParse(p.Id.AsSpan(2), out var n) ? n : 0)
            .ToHashSet();

        var next = 1;
        while (taken.Contains(next)) next++;
        return $"p-{next:D3}";
    }

    /// <summary>Continues the price list's own numbering rather than restarting it.</summary>
    private string NextProductCode()
    {
        var highest = catalog.Products
            .Select(p => int.TryParse(p.Code, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return (highest + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static int Percent(int mrp, int price) =>
        mrp <= 0 || mrp <= price ? 0 : (int)Math.Round((mrp - price) / (double)mrp * 100);

    private ActionResult NotFoundProblem(string what, string id) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: $"No such {what}",
        detail: $"Nothing in the catalogue answers to '{id}'.");

    private ActionResult Conflict(string detail) => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "That change would break the catalogue",
        detail: detail);

    /// <summary>
    /// Names carry ½, ″, × and & — the storefront's own slugs are built the same
    /// way in the price-list importer, and the two have to agree.
    /// </summary>
    internal static string Slugify(string value)
    {
        var expanded = value
            .Replace("½", ".5").Replace("¼", ".25").Replace("¾", ".75")
            .Replace("×", " x ").Replace("&", " and ")
            .Replace("″", " inch ").Replace("’", string.Empty);

        var ascii = new StringBuilder();
        foreach (var ch in expanded.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                ascii.Append(ch);
        }

        var slug = NonSlugChars().Replace(ascii.ToString().ToLowerInvariant(), "-").Trim('-');
        slug = LeadingDot().Replace(slug, string.Empty);

        return slug.Length == 0 ? "item" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    [GeneratedRegex(@"^-?\.")]
    private static partial Regex LeadingDot();
}
