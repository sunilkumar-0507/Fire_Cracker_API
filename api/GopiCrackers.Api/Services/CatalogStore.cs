using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services.Persistence;

namespace GopiCrackers.Api.Services;

/// <summary>
/// The catalogue, in memory, with the lookups and derived collections the
/// storefront expects.
///
/// This is the server-side twin of <c>src/data/index.js</c>: the single place
/// that knows what the catalogue is. It no longer knows where it came from —
/// <see cref="ICatalogPersistence"/> does, and is either the seven JSON files
/// the project started on or a MySQL database, decided once in Program.cs.
///
/// Reads are lock-free. Every mutation builds a whole new <see cref="CatalogSnapshot"/>
/// and swaps it in under a lock, so a request that is midway through rendering a
/// page never sees a half-applied edit. Writers also persist the affected part
/// before the swap — if the write throws, the in-memory catalogue is left
/// untouched rather than drifting ahead of what is stored.
/// </summary>
public sealed class CatalogStore
{
    private readonly Lock _writeLock = new();
    private readonly ICatalogPersistence _persistence;
    private readonly ILogger<CatalogStore> _logger;

    private volatile CatalogSnapshot _snapshot;

    public CatalogStore(ICatalogPersistence persistence, ILogger<CatalogStore> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _snapshot = Read();

        logger.LogInformation(
            "Catalogue loaded from {Backend}: {Products} products, {Categories} categories, " +
            "{Combos} combos, {Offers} offers, {Banners} banners, {Testimonials} testimonials, {Faqs} FAQs",
            persistence.Backend, Products.Count, Categories.Count, Combos.Count, Offers.Count,
            Banners.Count, Testimonials.Count, Faqs.Count);
    }

    private CatalogSnapshot Read()
    {
        var data = _persistence.Load();

        return CatalogSnapshot.Build(
            products: data.Products,
            rawCategories: data.Categories,
            combos: data.Combos,
            offers: data.Offers,
            banners: data.Banners,
            testimonials: data.Testimonials,
            faqs: data.Faqs);
    }

    /// <summary>
    /// Re-reads the whole catalogue from storage and publishes it.
    ///
    /// The admin endpoints keep memory and storage in step on their own, so
    /// nothing in normal use needs this. It exists for the case the database
    /// introduced: a price list imported straight into MySQL, or a row edited
    /// by hand, which this API would otherwise not see until it restarted.
    /// </summary>
    public CatalogSnapshot Reload()
    {
        lock (_writeLock)
        {
            var next = Read();
            _snapshot = next;
            _logger.LogInformation(
                "Catalogue reloaded from {Backend}: {Products} products", _persistence.Backend, next.Products.Count);
            return next;
        }
    }

    /// <summary>Which backend the catalogue is stored in — <c>files</c> or <c>mysql</c>.</summary>
    public string Backend => _persistence.Backend;

    /* ---------------------------------------------------------------------- */
    /* Reads                                                                   */
    /* ---------------------------------------------------------------------- */

    public IReadOnlyList<Product> Products => _snapshot.Products;
    public IReadOnlyList<Category> Categories => _snapshot.Categories;
    public IReadOnlyList<Combo> Combos => _snapshot.Combos;
    public IReadOnlyList<Offer> Offers => _snapshot.Offers;
    public IReadOnlyList<Banner> Banners => _snapshot.Banners;
    public IReadOnlyList<Testimonial> Testimonials => _snapshot.Testimonials;
    public IReadOnlyList<Faq> Faqs => _snapshot.Faqs;

    public IReadOnlyList<Product> FeaturedProducts => _snapshot.FeaturedProducts;
    public IReadOnlyList<Product> BestSellers => _snapshot.BestSellers;
    public IReadOnlyList<Product> NewArrivals => _snapshot.NewArrivals;
    public IReadOnlyList<Combo> FeaturedCombos => _snapshot.FeaturedCombos;
    public IReadOnlyList<Offer> FeaturedOffers => _snapshot.FeaturedOffers;
    public IReadOnlyList<TagCount> Tags => _snapshot.Tags;
    public PriceBounds PriceBounds => _snapshot.PriceBounds;

    /// <summary>Slug first, then id — matching the storefront's <c>findProduct</c>.</summary>
    public Product? FindProduct(string slugOrId) => _snapshot.FindProduct(slugOrId);

    public Category? FindCategory(string slug) => _snapshot.FindCategory(slug);

    public Combo? FindCombo(string slugOrId) => _snapshot.FindCombo(slugOrId);

    public Offer? FindOffer(string code) => _snapshot.FindOffer(code);

    /// <summary>
    /// Same category first, then anything sharing a tag. Never returns the input —
    /// a direct port of <c>getRelated</c>.
    /// </summary>
    public IReadOnlyList<Product> GetRelated(Product product, int limit = 4)
    {
        var products = Products;

        var sameCategory = products
            .Where(p => p.Category == product.Category && p.Id != product.Id)
            .ToList();

        if (sameCategory.Count >= limit) return sameCategory.Take(limit).ToList();

        var tagged = products.Where(p =>
            p.Id != product.Id &&
            p.Category != product.Category &&
            p.Tags.Any(t => product.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));

        return [.. sameCategory.Concat(tagged).Take(limit)];
    }

    /* ---------------------------------------------------------------------- */
    /* Writes                                                                  */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Applies <paramref name="mutate"/> to the current snapshot, persists every
    /// file the edit touched, then publishes the result. Serialised against other
    /// writers; readers keep seeing the previous snapshot until the swap lands.
    /// </summary>
    private T Write<T>(Func<CatalogSnapshot, (CatalogSnapshot Next, CatalogPart[] Parts, T Result)> mutate)
    {
        lock (_writeLock)
        {
            var (next, parts, result) = mutate(_snapshot);

            foreach (var part in parts) _persistence.Save(part, next);
            _snapshot = next;

            return result;
        }
    }

    public Product SaveProduct(Product product) => Write<Product>(current =>
    {
        var products = current.Products.ToList();
        var at = products.FindIndex(p => p.Id.Equals(product.Id, StringComparison.OrdinalIgnoreCase));

        if (at >= 0) products[at] = product;
        else products.Add(product);

        return (current.With(products: products), [CatalogPart.Products, CatalogPart.Categories], product);
    });

    public bool DeleteProduct(string id) => Write<bool>(current =>
    {
        var products = current.Products
            .Where(p => !p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return products.Count == current.Products.Count
            ? (current, [], false)
            : (current.With(products: products), [CatalogPart.Products, CatalogPart.Categories], true);
    });

    /// <summary>
    /// Bulk stock edit. Ids the catalogue does not know are reported back rather
    /// than failing the whole batch, so one stale row cannot block a stock take.
    /// </summary>
    public (int Updated, IReadOnlyList<string> Unknown) SetStock(IReadOnlyDictionary<string, int> levels) =>
        Write<(int, IReadOnlyList<string>)>(current =>
        {
            var unknown = levels.Keys
                .Where(id => current.FindProduct(id) is null)
                .ToList();

            var products = current.Products
                .Select(p => levels.TryGetValue(p.Id, out var stock) ? p with { Stock = Math.Max(0, stock) } : p)
                .ToList();

            var updated = levels.Count - unknown.Count;
            return updated == 0
                ? (current, [], (0, (IReadOnlyList<string>)unknown))
                : (current.With(products: products), [CatalogPart.Products], (updated, (IReadOnlyList<string>)unknown));
        });

    /// <summary>
    /// Moves stock by a delta per line rather than to an absolute figure.
    ///
    /// Selling and cancelling both have to be relative: two orders placed in the
    /// same second must each take their own quantity off, which a "set it to N"
    /// call cannot express without one of them overwriting the other. Products
    /// and combos both carry stock, so the line's kind decides which list moves.
    /// Nothing is allowed below zero.
    /// </summary>
    public void AdjustStock(IReadOnlyList<CartLine> lines, int sign)
    {
        if (lines.Count == 0 || sign == 0) return;

        Write<bool>(current =>
        {
            var productMoves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var comboMoves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                var target = line.Kind.Equals("combo", StringComparison.OrdinalIgnoreCase)
                    ? comboMoves
                    : productMoves;

                target[line.Id] = target.GetValueOrDefault(line.Id) + (line.Qty * sign);
            }

            var touched = new List<CatalogPart>();

            var products = current.Products;
            if (productMoves.Count > 0)
            {
                products = [.. products.Select(p => productMoves.TryGetValue(p.Id, out var delta)
                    ? p with { Stock = Math.Max(0, p.Stock + delta) }
                    : p)];
                touched.Add(CatalogPart.Products);
            }

            var combos = current.Combos;
            if (comboMoves.Count > 0)
            {
                combos = [.. combos.Select(c => comboMoves.TryGetValue(c.Id, out var delta)
                    ? c with { Stock = Math.Max(0, c.Stock + delta) }
                    : c)];
                touched.Add(CatalogPart.Combos);
            }

            return touched.Count == 0
                ? (current, [], false)
                : (current.With(products: products, combos: combos), [.. touched], true);
        });
    }

    public Category SaveCategory(Category category) => Write<Category>(current =>
    {
        var categories = current.RawCategories.ToList();
        var at = categories.FindIndex(c => c.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase));

        if (at >= 0) categories[at] = category;
        else categories.Add(category);

        return (current.With(rawCategories: categories), [CatalogPart.Categories], category);
    });

    public bool DeleteCategory(string id) => Write<bool>(current =>
    {
        var categories = current.RawCategories
            .Where(c => !c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return categories.Count == current.RawCategories.Count
            ? (current, [], false)
            : (current.With(rawCategories: categories), [CatalogPart.Categories], true);
    });

    public Combo SaveCombo(Combo combo) => Write<Combo>(current =>
    {
        var combos = current.Combos.ToList();
        var at = combos.FindIndex(c => c.Id.Equals(combo.Id, StringComparison.OrdinalIgnoreCase));

        if (at >= 0) combos[at] = combo;
        else combos.Add(combo);

        return (current.With(combos: combos), [CatalogPart.Combos], combo);
    });

    public bool DeleteCombo(string id) => Write<bool>(current =>
    {
        var combos = current.Combos
            .Where(c => !c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return combos.Count == current.Combos.Count
            ? (current, [], false)
            : (current.With(combos: combos), [CatalogPart.Combos], true);
    });

    public Offer SaveOffer(Offer offer) => Write<Offer>(current =>
    {
        var offers = current.Offers.ToList();
        var at = offers.FindIndex(o => o.Id.Equals(offer.Id, StringComparison.OrdinalIgnoreCase));

        if (at >= 0) offers[at] = offer;
        else offers.Add(offer);

        return (current.With(offers: offers), [CatalogPart.Offers], offer);
    });

    public bool DeleteOffer(string id) => Write<bool>(current =>
    {
        var offers = current.Offers
            .Where(o => !o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return offers.Count == current.Offers.Count
            ? (current, [], false)
            : (current.With(offers: offers), [CatalogPart.Offers], true);
    });
}
