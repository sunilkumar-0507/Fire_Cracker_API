using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services;

/// <summary>
/// One immutable view of the whole catalogue: the seven collections, the derived
/// lists the storefront reads, and the lookup dictionaries behind
/// <c>FindProduct</c> and friends.
///
/// Every mutation in <see cref="CatalogStore"/> produces a new instance rather
/// than editing this one, which is what lets readers run without a lock.
/// Building one is O(n) over ~180 products — cheap enough to do on every admin
/// save, and far simpler to reason about than patching a dozen indexes in place.
/// </summary>
public sealed class CatalogSnapshot
{
    private readonly Dictionary<string, Product> _productsBySlug;
    private readonly Dictionary<string, Product> _productsById;
    private readonly Dictionary<string, Category> _categoriesBySlug;
    private readonly Dictionary<string, Combo> _combosBySlug;
    private readonly Dictionary<string, Combo> _combosById;
    private readonly Dictionary<string, Offer> _offersByCode;

    /// <summary>Categories as they are on disk, before product counts are applied.</summary>
    public IReadOnlyList<Category> RawCategories { get; }

    public IReadOnlyList<Product> Products { get; }
    public IReadOnlyList<Category> Categories { get; }
    public IReadOnlyList<Combo> Combos { get; }
    public IReadOnlyList<Offer> Offers { get; }
    public IReadOnlyList<Banner> Banners { get; }
    public IReadOnlyList<Testimonial> Testimonials { get; }
    public IReadOnlyList<Faq> Faqs { get; }

    public IReadOnlyList<Product> FeaturedProducts { get; }
    public IReadOnlyList<Product> BestSellers { get; }
    public IReadOnlyList<Product> NewArrivals { get; }
    public IReadOnlyList<Combo> FeaturedCombos { get; }
    public IReadOnlyList<Offer> FeaturedOffers { get; }
    public IReadOnlyList<TagCount> Tags { get; }
    public PriceBounds PriceBounds { get; }

    private CatalogSnapshot(
        IReadOnlyList<Product> products,
        IReadOnlyList<Category> rawCategories,
        IReadOnlyList<Combo> combos,
        IReadOnlyList<Offer> offers,
        IReadOnlyList<Banner> banners,
        IReadOnlyList<Testimonial> testimonials,
        IReadOnlyList<Faq> faqs)
    {
        Products = products;
        RawCategories = rawCategories;
        Combos = combos;
        Offers = offers;
        Banners = banners;
        Testimonials = testimonials;
        Faqs = faqs;

        // Counts are derived from the catalogue rather than trusted from the file,
        // so the two can never drift — the same rule as `categoriesWithCounts`.
        Categories = [.. rawCategories.Select(c => c with
        {
            ProductCount = products.Count(p => p.Category == c.Slug),
        })];

        // Duplicate slugs would throw out of ToDictionary and take the whole API
        // down on a bad save, so the first entry wins and the rest are ignored.
        // The admin endpoints reject duplicates up front; this is the backstop.
        _productsBySlug = FirstWins(products, p => p.Slug);
        _productsById = FirstWins(products, p => p.Id);
        _categoriesBySlug = FirstWins(Categories, c => c.Slug);
        _combosBySlug = FirstWins(combos, c => c.Slug);
        _combosById = FirstWins(combos, c => c.Id);
        _offersByCode = FirstWins(offers, o => o.Code);

        FeaturedProducts = [.. products.Where(p => p.Featured)];
        BestSellers = [.. products.Where(p => p.BestSeller)];
        NewArrivals = [.. products.Where(p => p.IsNew)];
        FeaturedCombos = [.. combos.Where(c => c.Featured)];
        FeaturedOffers = [.. offers.Where(o => o.Featured)];

        Tags =
        [
            .. products
                .SelectMany(p => p.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TagCount(g.Key, g.Count()))
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
        ];

        PriceBounds = products.Count == 0
            ? new PriceBounds(0, 0)
            : new PriceBounds(products.Min(p => p.Price), products.Max(p => p.Price));
    }

    public static CatalogSnapshot Build(
        IReadOnlyList<Product> products,
        IReadOnlyList<Category> rawCategories,
        IReadOnlyList<Combo> combos,
        IReadOnlyList<Offer> offers,
        IReadOnlyList<Banner> banners,
        IReadOnlyList<Testimonial> testimonials,
        IReadOnlyList<Faq> faqs) =>
        new(products, rawCategories, combos, offers, banners, testimonials, faqs);

    /// <summary>A copy with only the named collections replaced.</summary>
    public CatalogSnapshot With(
        IReadOnlyList<Product>? products = null,
        IReadOnlyList<Category>? rawCategories = null,
        IReadOnlyList<Combo>? combos = null,
        IReadOnlyList<Offer>? offers = null) =>
        new(products ?? Products,
            rawCategories ?? RawCategories,
            combos ?? Combos,
            offers ?? Offers,
            Banners,
            Testimonials,
            Faqs);

    public Product? FindProduct(string slugOrId) =>
        _productsBySlug.GetValueOrDefault(slugOrId) ?? _productsById.GetValueOrDefault(slugOrId);

    public Category? FindCategory(string slug) => _categoriesBySlug.GetValueOrDefault(slug);

    public Combo? FindCombo(string slugOrId) =>
        _combosBySlug.GetValueOrDefault(slugOrId) ?? _combosById.GetValueOrDefault(slugOrId);

    public Offer? FindOffer(string code) => _offersByCode.GetValueOrDefault(code);

    private static Dictionary<string, T> FirstWins<T>(IEnumerable<T> source, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source) map.TryAdd(key(item), item);
        return map;
    }
}
