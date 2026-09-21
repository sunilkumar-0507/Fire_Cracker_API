using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services;
using GopiCrackers.Api.Services.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Data;

/// <summary>
/// Fills a new database from the JSON files the project ships with.
///
/// This is what makes the switch to MySQL a migration rather than a fresh
/// start. Point the API at an empty database and it arrives with the whole 2026
/// price list, the categories, the combos and the offers already in it — and,
/// if the shop has been trading on files, its order book, its stock ledger and
/// its analytics too.
///
/// The rule throughout is <b>never overwrite without being asked</b>. Each
/// group is seeded only when its table is empty, so a restart cannot undo a
/// morning's edits by re-importing a stale file over them. The one way to
/// overwrite is to ask for it explicitly, through
/// <c>POST /api/admin/database/seed</c> with <c>overwrite: true</c>.
/// </summary>
public sealed class DatabaseSeeder(
    IDbContextFactory<GopiCrackersDbContext> factory,
    DataFiles files,
    JsonCatalogPersistence catalogFiles,
    JsonOrderPersistence orderFiles,
    JsonInventoryPersistence inventoryFiles,
    JsonAnalyticsPersistence analyticsFiles,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseSeeder> logger)
{
    /// <summary>
    /// Seeds whatever is empty. Returns what it put where, so the endpoint can
    /// say so and a log line can prove it happened.
    /// </summary>
    public SeedResult Seed(bool overwrite = false)
    {
        // The guard that makes the shared DbContext factory safe: with no
        // connection string nothing here may open a context, and saying so
        // once here is better than every caller remembering to ask.
        if (!options.Value.Enabled)
            return SeedResult.Nothing("No database is configured, so there is nothing to seed.");

        if (!files.Available)
        {
            logger.LogWarning("No catalogue files found, so there is nothing to seed from");
            return SeedResult.Nothing("No catalogue JSON was found to seed from.");
        }

        using var db = factory.CreateDbContext();

        var catalogue = SeedCatalogue(db, overwrite);
        var orders = SeedOrders(db, overwrite);
        var intake = SeedIntake(db, overwrite);
        var analytics = SeedAnalytics(db, overwrite);

        var result = new SeedResult(
            Seeded: catalogue.Seeded || orders > 0 || intake > 0 || analytics > 0,
            Products: catalogue.Products,
            Categories: catalogue.Categories,
            Combos: catalogue.Combos,
            Offers: catalogue.Offers,
            Banners: catalogue.Banners,
            Testimonials: catalogue.Testimonials,
            Faqs: catalogue.Faqs,
            Orders: orders,
            StockIntake: intake,
            AnalyticsEvents: analytics,
            Note: null);

        if (result.Seeded)
        {
            logger.LogInformation(
                "Seeded the database: {Products} products, {Categories} categories, {Combos} combos, " +
                "{Offers} offers, {Orders} orders, {Intake} intake entries, {Events} analytics events",
                result.Products, result.Categories, result.Combos, result.Offers,
                result.Orders, result.StockIntake, result.AnalyticsEvents);
        }

        return result;
    }

    /* ---------------------------------------------------------------------- */

    private (bool Seeded, int Products, int Categories, int Combos, int Offers,
             int Banners, int Testimonials, int Faqs)
        SeedCatalogue(GopiCrackersDbContext db, bool overwrite)
    {
        // Products are the test: a database with a price list in it is a
        // database that has been set up, whatever else is or is not there.
        if (!overwrite && db.Products.Any()) return (false, 0, 0, 0, 0, 0, 0, 0);

        var data = catalogFiles.Load();

        var snapshot = CatalogSnapshot.Build(
            products: data.Products,
            rawCategories: data.Categories,
            combos: data.Combos,
            offers: data.Offers,
            banners: data.Banners,
            testimonials: data.Testimonials,
            faqs: data.Faqs);

        MySqlCatalogPersistence.Sync(db.Products, snapshot.Products, r => r.Id, m => m.Id, Mapping.Fill);
        MySqlCatalogPersistence.Sync(db.Categories, snapshot.Categories, r => r.Id, m => m.Id, Mapping.Fill);
        MySqlCatalogPersistence.Sync(db.Combos, snapshot.Combos, r => r.Id, m => m.Id, Mapping.Fill);
        MySqlCatalogPersistence.Sync(db.Offers, snapshot.Offers, r => r.Id, m => m.Id, Mapping.Fill);
        MySqlCatalogPersistence.Sync(db.Banners, snapshot.Banners, r => r.Id, m => m.Id, Mapping.Fill);
        MySqlCatalogPersistence.Sync(db.Testimonials, snapshot.Testimonials, r => r.Id, m => m.Id, Mapping.Fill);
        MySqlCatalogPersistence.Sync(db.Faqs, snapshot.Faqs, r => r.Id, m => m.Id, Mapping.Fill);

        db.SaveChanges();

        return (true,
            snapshot.Products.Count, snapshot.Categories.Count, snapshot.Combos.Count,
            snapshot.Offers.Count, snapshot.Banners.Count, snapshot.Testimonials.Count,
            snapshot.Faqs.Count);
    }

    /// <summary>
    /// Copies the journalled order book across.
    ///
    /// Orders are never overwritten, even when overwrite is asked for. A
    /// catalogue can be re-imported because the price list is the source of
    /// truth for it; an order book cannot, because the shop's own trade is.
    /// Re-importing a stale journal over live orders would lose the ones placed
    /// since, and there would be no way back.
    /// </summary>
    private int SeedOrders(GopiCrackersDbContext db, bool overwrite)
    {
        if (db.Orders.Any()) return 0;
        _ = overwrite;

        var orders = orderFiles.LoadOrders();
        if (orders.Count == 0) return 0;

        db.Orders.AddRange(orders.Select(Mapping.ToRow));
        db.SaveChanges();
        return orders.Count;
    }

    private int SeedIntake(GopiCrackersDbContext db, bool overwrite)
    {
        if (db.StockIntake.Any()) return 0;
        _ = overwrite;

        var entries = inventoryFiles.Load();
        if (entries.Count == 0) return 0;

        db.StockIntake.AddRange(entries.Select(Mapping.ToRow));
        db.SaveChanges();
        return entries.Count;
    }

    private int SeedAnalytics(GopiCrackersDbContext db, bool overwrite)
    {
        if (db.AnalyticsEvents.Any()) return 0;
        _ = overwrite;

        var events = analyticsFiles.Load(AnalyticsStore.Capacity);
        if (events.Count == 0) return 0;

        db.AnalyticsEvents.AddRange(events.Select(Mapping.ToRow));
        db.SaveChanges();
        return events.Count;
    }
}
