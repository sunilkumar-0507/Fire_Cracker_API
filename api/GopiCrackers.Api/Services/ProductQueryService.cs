using System.Text;
using System.Globalization;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services;

/// <summary>
/// Search, filter and sort — a faithful port of <c>src/utils/search.js</c>.
///
/// The scoring, the every-term-must-match rule, the ranking nudges and all six
/// sort comparators behave identically to the client, so moving the catalogue
/// page onto the API cannot silently reorder anybody's results.
/// </summary>
public sealed class ProductQueryService
{
    /// <summary>Pre-computed haystack per product, built once at startup.</summary>
    private sealed record Entry(Product Product, string Name, string Haystack);

    private readonly CatalogStore _catalog;
    private readonly List<Entry> _index;
    private readonly Dictionary<string, string> _categoryNames;

    public static readonly IReadOnlyList<SortOption> SortOptions =
    [
        new("relevance", "Recommended"),
        new("price-asc", "Price: low to high"),
        new("price-desc", "Price: high to low"),
        new("discount", "Biggest discount"),
        new("newest", "New arrivals"),
    ];

    public ProductQueryService(CatalogStore catalog)
    {
        _catalog = catalog;

        _index = [.. catalog.Products.Select(p => new Entry(
            p,
            Normalize(p.Name),
            Normalize(string.Join(' ',
                p.Name, p.Category, p.Brand, p.Description, string.Join(' ', p.Tags), p.Unit))))];

        _categoryNames = catalog.Categories.ToDictionary(
            c => c.Slug, c => Normalize(c.Name), StringComparer.OrdinalIgnoreCase);
    }

    /* ---------------------------------------------------------------------- */
    /* Normalisation                                                           */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Lowercase, decompose, then keep only <c>[a-z0-9]</c> — everything else
    /// collapses to a single space. Mirrors the JS <c>normalize()</c> exactly,
    /// including the way an accented letter loses its combining mark.
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormKD);

        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }
            else
            {
                // Anything else — punctuation, whitespace, Tamil script, combining
                // marks — becomes a separator that is collapsed and trimmed.
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /* ---------------------------------------------------------------------- */
    /* Scored search                                                           */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Higher score = better match: exact name &gt; name prefix &gt; name contains
    /// &gt; category &gt; any field. Every term must match somewhere, so
    /// "gold sparkler" narrows properly.
    /// </summary>
    public List<Product> SearchProducts(string? query, int limit = int.MaxValue)
    {
        var q = Normalize(query);
        if (q.Length == 0) return [];

        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(Product Product, double Score)>();

        foreach (var entry in _index)
        {
            double score = 0;
            var matchedAll = true;

            foreach (var term in terms)
            {
                if (!entry.Haystack.Contains(term, StringComparison.Ordinal))
                {
                    matchedAll = false;
                    break;
                }

                if (entry.Name == term) score += 100;
                else if (entry.Name.StartsWith(term, StringComparison.Ordinal)) score += 60;
                else if (entry.Name.Contains(term, StringComparison.Ordinal)) score += 40;
                else if (_categoryNames.GetValueOrDefault(entry.Product.Category, string.Empty)
                         .Contains(term, StringComparison.Ordinal)) score += 24;
                else score += 8;
            }

            if (!matchedAll) continue;

            // Nudge the catalogue's strongest items up when scores tie.
            if (entry.Product.BestSeller) score += 6;
            if (entry.Product.Featured) score += 4;

            scored.Add((entry.Product, score));
        }

        // OrderByDescending is stable, like Array.prototype.sort — ties keep
        // catalogue order rather than shuffling between requests.
        return [.. scored.OrderByDescending(s => s.Score).Take(limit).Select(s => s.Product)];
    }

    /// <summary>Matching categories, so a search can offer a jump-to-category.</summary>
    public List<Category> SearchCategories(string? query, int limit = 3)
    {
        var q = Normalize(query);
        if (q.Length == 0) return [];

        return [.. _catalog.Categories
            .Where(c => Normalize($"{c.Name} {c.TamilName} {c.Tagline}")
                .Contains(q, StringComparison.Ordinal))
            .Take(limit)];
    }

    /* ---------------------------------------------------------------------- */
    /* Filter + sort                                                           */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Single entry point for the catalogue listing. Returns the full filtered
    /// set; paging is applied by the controller.
    /// </summary>
    public List<Product> Filter(ProductQuery query)
    {
        IEnumerable<Product> result = string.IsNullOrWhiteSpace(query.Q)
            ? _catalog.Products
            : SearchProducts(query.Q);

        if (!string.IsNullOrWhiteSpace(query.Category) &&
            !query.Category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Where(p =>
                p.Category.Equals(query.Category, StringComparison.OrdinalIgnoreCase));
        }

        var tags = query.NormalisedTags;
        if (tags.Count > 0)
            result = result.Where(p => tags.All(t => p.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));

        if (query.Min is { } min) result = result.Where(p => p.Price >= min);
        if (query.Max is { } max) result = result.Where(p => p.Price <= max);

        // "In stock" has to mean "you can buy this". A deactivated line with 40
        // on the shelf is not something a customer can order, so it is filtered
        // out by the same switch that hides a sold-out one.
        if (query.InStockOnly) result = result.Where(p => p.Purchasable);

        if (query.NormalisedAvailability is { } availability)
            result = result.Where(p => p.Availability == availability);
        if (query.Featured is { } featured) result = result.Where(p => p.Featured == featured);
        if (query.BestSeller is { } best) result = result.Where(p => p.BestSeller == best);
        if (query.IsNew is { } isNew) result = result.Where(p => p.IsNew == isNew);

        var sort = string.IsNullOrWhiteSpace(query.Sort) ? "relevance" : query.Sort.ToLowerInvariant();

        // A search already returns results in relevance order — don't undo that.
        var searched = !string.IsNullOrWhiteSpace(query.Q);
        if (searched && sort == "relevance") return [.. result];

        return [.. Sort(result, sort)];
    }

    private static IOrderedEnumerable<Product> Sort(IEnumerable<Product> source, string sort) => sort switch
    {
        "price-asc" => source.OrderBy(p => p.Price),
        "price-desc" => source.OrderByDescending(p => p.Price),
        "discount" => source.OrderByDescending(p => p.Discount).ThenBy(p => p.Price),
        "newest" => source.OrderByDescending(p => p.IsNew).ThenByDescending(p => p.Discount),
        _ => source
            .OrderByDescending(p => p.BestSeller)
            .ThenByDescending(p => p.Featured)
            .ThenByDescending(p => p.Discount),
    };

    public static bool IsKnownSort(string? sort) =>
        string.IsNullOrWhiteSpace(sort) ||
        SortOptions.Any(o => o.Value.Equals(sort, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Catalogue query string. The parameter names match the storefront's own URL
/// state (<c>?q=&amp;category=&amp;tag=&amp;max=&amp;rating=&amp;stock=1&amp;sort=</c>),
/// so a shareable catalogue link can be forwarded to the API unchanged.
/// </summary>
public sealed record ProductQuery
{
    /// <summary>Free-text search across name, category, brand, description, tags and unit.</summary>
    public string? Q { get; init; }

    /// <summary>Category slug, or <c>all</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Repeatable (<c>?tag=silent&amp;tag=kids-safe</c>) or comma-separated. All must match.</summary>
    public string[]? Tag { get; init; }

    /// <summary>Minimum price in rupees.</summary>
    public int? Min { get; init; }

    /// <summary>Maximum price in rupees.</summary>
    public int? Max { get; init; }

    /// <summary><c>1</c> or <c>true</c> to show only what can actually be bought.</summary>
    public string? Stock { get; init; }

    /// <summary>
    /// Exactly one state: <c>available</c>, <c>out-of-stock</c> or
    /// <c>unavailable</c>. Narrower than <see cref="Stock"/>, which is the
    /// shopper's "hide what I cannot buy" switch — this is the admin's "show me
    /// only the withdrawn ones".
    /// </summary>
    public string? Availability { get; init; }

    /// <summary>One of: relevance, price-asc, price-desc, discount, newest.</summary>
    public string? Sort { get; init; }

    public bool? Featured { get; init; }
    public bool? BestSeller { get; init; }
    public bool? IsNew { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 24;

    public bool InStockOnly =>
        Stock is "1" or "true" or "True" or "yes" or "on";

    /// <summary>The availability filter, lowercased, or null when not asked for.</summary>
    public string? NormalisedAvailability =>
        Models.Availability.IsKnown(Availability) ? Availability!.Trim().ToLowerInvariant() : null;

    public IReadOnlyList<string> NormalisedTags =>
        Tag is null
            ? []
            : [.. Tag
                .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Clamped page/pageSize, so a hostile query cannot ask for everything at once.</summary>
    public ProductQuery Clamped() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, 100),
    };
}
