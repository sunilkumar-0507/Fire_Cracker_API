using GopiCrackers.Api.Data;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// The database, from the outside.
///
/// Four things a shopkeeper or whoever set the server up needs to be able to do
/// without a MySQL client and without an SSH session: see whether the API can
/// reach the database at all, apply the schema, fill an empty one from the
/// price list, and pick up a change somebody made to the tables directly.
///
/// Everything here works whether or not a database is configured. With none,
/// the status endpoint says so in a sentence and the rest answer 409 rather
/// than 500 — "there is nothing to migrate" is an answer, not a crash.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/database")]
public sealed class AdminDatabaseController(
    DatabaseBootstrapper bootstrapper,
    DatabaseSeeder seeder,
    CatalogStore catalog,
    OrderStore orders,
    InventoryStore inventory,
    AnalyticsStore analytics,
    IOptions<DatabaseOptions> options,
    TimeProvider clock) : ControllerBase
{
    private readonly DatabaseOptions _options = options.Value;

    /// <summary>
    /// Where the shop's data is kept, whether the API can reach it, what the
    /// schema is, and how many rows are in each table.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<DatabaseStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DatabaseStatus>> Status(CancellationToken cancellationToken) =>
        Ok(await bootstrapper.StatusAsync(catalog.Backend, clock.GetUtcNow(), cancellationToken));

    /// <summary>
    /// Which backend each store ended up on.
    ///
    /// They are wired together and cannot disagree, but a status endpoint that
    /// asserts that rather than assuming it is the one that catches the day
    /// somebody wires a fifth store and forgets.
    /// </summary>
    [HttpGet("backends")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Backends() => Ok(new
    {
        catalogue = catalog.Backend,
        orders = orders.Backend,
        inventory = inventory.Backend,
        analytics = analytics.Backend,
        configured = _options.Enabled ? "mysql" : "files",
    });

    /// <summary>
    /// Applies any pending migrations.
    ///
    /// Startup does this on its own unless <c>AutoMigrate</c> is off, which is
    /// what a deployment wants when several instances could otherwise race to
    /// apply the same migration.
    /// </summary>
    [HttpPost("migrate")]
    [ProducesResponseType<MigrationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MigrationResult>> Migrate(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return NoDatabase();

        var result = await bootstrapper.MigrateAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Fills empty tables from the JSON catalogue.
    ///
    /// <c>overwrite: true</c> replaces the catalogue with the files' version —
    /// how a re-imported price list gets in. Orders, the stock ledger and
    /// analytics are never overwritten whatever is asked: a file cannot be a
    /// more recent truth about the shop's own trade than the database is.
    ///
    /// The in-memory catalogue is reloaded afterwards, so a seed shows up on
    /// the storefront immediately rather than at the next restart.
    /// </summary>
    [HttpPost("seed")]
    [ProducesResponseType<SeedResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SeedResult> Seed([FromBody] SeedRequest? body)
    {
        if (!_options.Enabled) return NoDatabase();

        var result = seeder.Seed(body?.Overwrite ?? false);
        if (result.Seeded) catalog.Reload();

        return Ok(result);
    }

    /// <summary>
    /// Re-reads the catalogue from storage.
    ///
    /// The admin endpoints keep memory and the database in step on their own,
    /// so nothing in normal use needs this. It is here for the case a database
    /// makes possible and files did not: a price list imported straight into
    /// MySQL, or a row edited by hand, which this API would otherwise not see
    /// until somebody restarted it.
    /// </summary>
    [HttpPost("reload")]
    [ProducesResponseType<ReloadResult>(StatusCodes.Status200OK)]
    public ActionResult<ReloadResult> Reload()
    {
        var snapshot = catalog.Reload();

        return Ok(new ReloadResult(
            Backend: catalog.Backend,
            Products: snapshot.Products.Count,
            Categories: snapshot.Categories.Count,
            Combos: snapshot.Combos.Count,
            Offers: snapshot.Offers.Count,
            Banners: snapshot.Banners.Count,
            Testimonials: snapshot.Testimonials.Count,
            Faqs: snapshot.Faqs.Count));
    }

    /// <summary>
    /// Everything the shop knows, in one JSON document.
    ///
    /// A backup a person can read, diff and restore from by hand — which for a
    /// shop of this size is worth more than a <c>mysqldump</c> nobody on the
    /// premises knows how to run. Served from memory, so it is a snapshot of
    /// one moment and costs the database nothing.
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Export() => Ok(new
    {
        exportedAt = clock.GetUtcNow(),
        backend = catalog.Backend,
        catalogue = new Bootstrap(
            Products: catalog.Products,
            Categories: catalog.Categories,
            Combos: catalog.Combos,
            Offers: catalog.Offers,
            Banners: catalog.Banners,
            Testimonials: catalog.Testimonials,
            Faqs: catalog.Faqs),
        orders = orders.AllOrders(),
        enquiries = orders.AllEnquiries(),
        messages = orders.AllMessages(),
        subscribers = orders.AllSubscribers(),
        stockIntake = inventory.Entries,
    });

    private ActionResult NoDatabase() => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "No database is configured",
        detail: "The shop is running on its JSON files. Set " +
                "Storefront:Database:ConnectionString (or ConnectionStrings:GopiCrackers) " +
                "and restart the API to move it onto MySQL or MariaDB.");
}
