using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace GopiCrackers.Api.Data;

/// <summary>
/// Gets the database ready, and answers questions about it afterwards.
///
/// Two jobs, kept together because they are the same knowledge: what the schema
/// should be, and what it currently is.
///
/// <see cref="InitialiseAsync"/> runs once at startup, before anything resolves
/// a store — which matters, because the stores read their whole contents on
/// construction and a catalogue loaded from a table that does not exist yet is
/// not a recoverable situation. If the database is configured and unreachable,
/// startup fails loudly rather than falling back to the JSON files: a shop
/// quietly writing today's orders into a file it will never look at again is a
/// far worse outcome than one that refuses to start and says why.
/// </summary>
public sealed class DatabaseBootstrapper(
    IDbContextFactory<GopiCrackersDbContext> factory,
    DatabaseSeeder seeder,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseBootstrapper> logger)
{
    private readonly DatabaseOptions _options = options.Value;

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        logger.LogInformation("Connecting to {ConnectionString}", _options.Redacted);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            // Nearly always a first deploy rather than a broken one: the server
            // is up and the credentials are good, but nobody has run CREATE
            // DATABASE yet. MySQL reports a missing schema the same way it
            // reports a missing server — the connection simply fails — so the
            // only way to tell them apart is to connect to the server without
            // naming a database and look. Creating it here is what makes
            // "upload and start" enough on a clean host.
            await EnsureDatabaseExistsAsync(cancellationToken);

            // Re-checked rather than assumed: if the server was genuinely
            // unreachable we want to fail here, at startup, and not later on
            // somebody's checkout.
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "The API is configured for MySQL but cannot reach it. Check " +
                    $"Storefront:Database:ConnectionString ({_options.Redacted}), or clear it " +
                    "to run the shop on the JSON files instead.");
            }
        }

        if (_options.AutoMigrate)
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));

                await db.Database.MigrateAsync(cancellationToken);
            }
        }

        if (_options.SeedOnStart) seeder.Seed(overwrite: false);
    }

    /// <summary>
    /// Creates the schema named in the connection string if the server does not
    /// have it yet, so a fresh host needs no manual CREATE DATABASE step.
    ///
    /// Only the database is created here — every table inside it comes from the
    /// migrations, which run immediately afterwards. That ordering matters: an
    /// empty database is the one state EF can take from nothing to current, and
    /// creating tables any other way would leave the migration history empty and
    /// the next deploy trying to create them all over again.
    ///
    /// Failures are rethrown with the fix in the message rather than swallowed.
    /// The two that actually happen in practice are a server that is not up yet
    /// and a user without the CREATE privilege, and they need different fixes.
    /// </summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(_options.ConnectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Storefront:Database:ConnectionString is not a valid MySQL connection string. " +
                "Expected something like " +
                "\"Server=localhost;Port=3306;Database=gopicrackers;User ID=gopi;Password=…;\".", ex);
        }

        var name = builder.Database;

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "The connection string does not name a database. Add Database=gopicrackers; to " +
                $"Storefront:Database:ConnectionString ({_options.Redacted}).");
        }

        // The name is an identifier, so it is quoted rather than parameterised —
        // which means a backtick in it would end the quoting. It comes from the
        // operator's own configuration, but a database whose name could rewrite
        // the statement around it is not one worth creating.
        if (name.Contains('`'))
        {
            throw new InvalidOperationException(
                $"The database name '{name}' contains a backtick, which cannot be quoted safely. " +
                "Rename it to letters, digits and underscores.");
        }

        // The same credentials pointed at the server rather than at a schema on
        // it, which is the one connection that can succeed while the schema is
        // still missing.
        builder.Database = string.Empty;

        await using var connection = new MySqlConnection(builder.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not reach the MySQL server to create '{name}'. The server itself did not " +
                $"answer, so this is not just a missing database. Check host, port and credentials in " +
                $"Storefront:Database:ConnectionString ({_options.Redacted}), and that the user is " +
                $"allowed to connect from this machine. The server said: {ex.Message}", ex);
        }

        await using var command = connection.CreateCommand();

        // utf8mb4 so the catalogue can hold the rupee sign and Tamil product
        // names, and unicode_ci rather than MySQL 8's 0900 default because
        // MariaDB does not have the latter and this has to run on both.
        command.CommandText =
            $"CREATE DATABASE IF NOT EXISTS `{name}` " +
            "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            logger.LogInformation("Database {Database} is present", name);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Reached the MySQL server but could not create the database '{name}' — most likely " +
                $"the user has no CREATE privilege. Either grant it, or create the database by hand " +
                $"and let the API fill it: " +
                $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; " +
                $"The server said: {ex.Message}", ex);
        }
    }

    /// <summary>Applies whatever is pending, for a deployment that runs migrations by hand.</summary>
    public async Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new MigrationResult(false, [], [],
                "No connection string is configured, so there is no schema to migrate.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Reading the migration history needs the database to exist, so this
        // endpoint has to be able to create it too — otherwise "migrate the
        // schema" would fail on the one database that most needs migrating,
        // the empty one.
        if (!await db.Database.CanConnectAsync(cancellationToken))
            await EnsureDatabaseExistsAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return new MigrationResult(true, [], [], "The schema is already up to date.");
        }

        await db.Database.MigrateAsync(cancellationToken);

        var stillPending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        return new MigrationResult(
            Ok: stillPending.Count == 0,
            Applied: pending,
            Pending: stillPending,
            Message: $"Applied {pending.Count} migration(s).");
    }

    /// <summary>
    /// Everything worth knowing about the database, gathered defensively:
    /// a failure anywhere becomes a field on the response rather than a 500,
    /// because this is the endpoint somebody reaches for precisely when
    /// something is broken.
    /// </summary>
    public async Task<DatabaseStatus> StatusAsync(
        string backend,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return DatabaseStatus.Disabled(backend, at);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var provider = db.Database.ProviderName ?? "unknown";
        var database = db.Database.GetDbConnection().Database;

        bool connected;
        string? error = null;

        try
        {
            connected = await db.Database.CanConnectAsync(cancellationToken);
            if (!connected) error = "The server did not answer.";
        }
        catch (Exception ex)
        {
            connected = false;
            error = ex.Message;
        }

        if (!connected)
        {
            return new DatabaseStatus(
                Enabled: true, Backend: backend, Provider: provider,
                ConnectionString: _options.Redacted, Database: database,
                ServerVersion: null, CanConnect: false, Error: error,
                AppliedMigrations: [], PendingMigrations: [], Tables: [], CheckedAt: at);
        }

        var applied = new List<string>();
        var pending = new List<string>();

        try
        {
            applied = [.. await db.Database.GetAppliedMigrationsAsync(cancellationToken)];
            pending = [.. await db.Database.GetPendingMigrationsAsync(cancellationToken)];
        }
        catch (Exception ex)
        {
            error = $"Could not read the migration history: {ex.Message}";
        }

        var tables = new List<TableCount>();
        try
        {
            tables =
            [
                new("products", await db.Products.LongCountAsync(cancellationToken)),
                new("categories", await db.Categories.LongCountAsync(cancellationToken)),
                new("combos", await db.Combos.LongCountAsync(cancellationToken)),
                new("offers", await db.Offers.LongCountAsync(cancellationToken)),
                new("banners", await db.Banners.LongCountAsync(cancellationToken)),
                new("testimonials", await db.Testimonials.LongCountAsync(cancellationToken)),
                new("faqs", await db.Faqs.LongCountAsync(cancellationToken)),
                new("orders", await db.Orders.LongCountAsync(cancellationToken)),
                new("order_items", await db.OrderItems.LongCountAsync(cancellationToken)),
                new("order_events", await db.OrderEvents.LongCountAsync(cancellationToken)),
                new("bulk_enquiries", await db.Enquiries.LongCountAsync(cancellationToken)),
                new("contact_messages", await db.ContactMessages.LongCountAsync(cancellationToken)),
                new("newsletter_subscribers", await db.Subscribers.LongCountAsync(cancellationToken)),
                new("stock_intake", await db.StockIntake.LongCountAsync(cancellationToken)),
                new("analytics_events", await db.AnalyticsEvents.LongCountAsync(cancellationToken)),
            ];
        }
        catch (Exception ex)
        {
            // Almost always "the schema has not been applied yet", which the
            // pending-migrations list above already says more precisely.
            error ??= $"Could not count rows: {ex.Message}";
        }

        return new DatabaseStatus(
            Enabled: true,
            Backend: backend,
            Provider: provider,
            ConnectionString: _options.Redacted,
            Database: database,
            ServerVersion: ServerVersionOf(db),
            CanConnect: true,
            Error: error,
            AppliedMigrations: applied,
            PendingMigrations: pending,
            Tables: tables,
            CheckedAt: at);
    }

    private static string? ServerVersionOf(GopiCrackersDbContext db)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) connection.Open();
            return connection.ServerVersion;
        }
        catch
        {
            // A version string is a nicety; not having one is not an error.
            return null;
        }
    }
}
