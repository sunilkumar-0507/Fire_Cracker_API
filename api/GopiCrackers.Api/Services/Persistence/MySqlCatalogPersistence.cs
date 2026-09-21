using GopiCrackers.Api.Data;
using GopiCrackers.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The catalogue in MySQL / MariaDB.
///
/// A save syncs one whole table against the snapshot that is about to be
/// published: rows that are new are inserted, rows that changed are updated,
/// rows that are gone are deleted. That sounds heavy and is not — the largest
/// table is the price list, which is under two hundred rows, and the only thing
/// that ever triggers a save is a shopkeeper pressing Save on one form. The
/// round trip that carried the request costs more than the sync does.
///
/// It also buys the property that matters most: the table cannot drift from the
/// snapshot. There is no per-row bookkeeping to get wrong, no "which of these
/// three lists did the edit touch", and a half-applied edit is impossible
/// because the whole thing rides on one transaction.
/// </summary>
public sealed class MySqlCatalogPersistence(
    IDbContextFactory<GopiCrackersDbContext> factory,
    ILogger<MySqlCatalogPersistence> logger) : ICatalogPersistence
{
    public string Backend => "mysql";

    public CatalogData Load()
    {
        using var db = factory.CreateDbContext();

        // Products come back in price-list order — shortest code first, then
        // lexically, which for the numeric codes the importer assigns is plain
        // numeric order. A table has no inherent order, and the storefront's
        // "featured" and "new in" strips read straight off this list, so
        // leaving it to the server would let two deployments disagree about
        // which six products the home page shows.
        var data = new CatalogData(
            Products: [.. db.Products.AsNoTracking()
                .OrderBy(p => p.Code.Length).ThenBy(p => p.Code)
                .AsEnumerable().Select(Mapping.ToModel)],
            Categories: [.. db.Categories.AsNoTracking().OrderBy(c => c.Id)
                .AsEnumerable().Select(Mapping.ToModel)],
            Combos: [.. db.Combos.AsNoTracking().OrderBy(c => c.Id)
                .AsEnumerable().Select(Mapping.ToModel)],
            Offers: [.. db.Offers.AsNoTracking().OrderBy(o => o.Id)
                .AsEnumerable().Select(Mapping.ToModel)],
            Banners: [.. db.Banners.AsNoTracking().OrderBy(b => b.Id)
                .AsEnumerable().Select(Mapping.ToModel)],
            Testimonials: [.. db.Testimonials.AsNoTracking().OrderBy(t => t.Id)
                .AsEnumerable().Select(Mapping.ToModel)],
            Faqs: [.. db.Faqs.AsNoTracking().OrderBy(f => f.Id)
                .AsEnumerable().Select(Mapping.ToModel)]);

        logger.LogInformation(
            "Loaded catalogue from the database: {Products} products, {Categories} categories, " +
            "{Combos} combos, {Offers} offers",
            data.Products.Count, data.Categories.Count, data.Combos.Count, data.Offers.Count);

        return data;
    }

    public void Save(CatalogPart part, CatalogSnapshot snapshot)
    {
        using var db = factory.CreateDbContext();

        switch (part)
        {
            case CatalogPart.Products:
                Sync(db.Products, snapshot.Products, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            case CatalogPart.Categories:
                // The counted view, not the raw one — the column is a
                // convenience for anything reading the table directly, and it
                // should say what the catalogue actually holds.
                Sync(db.Categories, snapshot.Categories, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            case CatalogPart.Combos:
                Sync(db.Combos, snapshot.Combos, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            case CatalogPart.Offers:
                Sync(db.Offers, snapshot.Offers, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            case CatalogPart.Banners:
                Sync(db.Banners, snapshot.Banners, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            case CatalogPart.Testimonials:
                Sync(db.Testimonials, snapshot.Testimonials, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            case CatalogPart.Faqs:
                Sync(db.Faqs, snapshot.Faqs, r => r.Id, m => m.Id, Mapping.Fill);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(part), part, "Not a catalogue part");
        }

        var written = db.SaveChanges();
        logger.LogInformation("Synced {Part}: {Rows} row(s) changed", part, written);
    }

    /// <summary>
    /// Brings one table into line with one list.
    ///
    /// Ids are compared case-insensitively because every other lookup in this
    /// API is — <c>FindProduct</c>, the admin's conflict checks, the snapshot's
    /// dictionaries. A sync that disagreed with them would insert a duplicate
    /// rather than update the row that is already there.
    /// </summary>
    internal static void Sync<TRow, TModel>(
        DbSet<TRow> set,
        IReadOnlyList<TModel> models,
        Func<TRow, string> rowKey,
        Func<TModel, string> modelKey,
        Func<TRow, TModel, TRow> fill)
        where TRow : class, new()
    {
        var existing = new Dictionary<string, TRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in set) existing[rowKey(row)] = row;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            var key = modelKey(model);
            if (!seen.Add(key)) continue; // A duplicate id in memory is not two rows.

            if (existing.TryGetValue(key, out var row)) fill(row, model);
            else set.Add(fill(new TRow(), model));
        }

        foreach (var (key, row) in existing)
        {
            if (!seen.Contains(key)) set.Remove(row);
        }
    }
}
