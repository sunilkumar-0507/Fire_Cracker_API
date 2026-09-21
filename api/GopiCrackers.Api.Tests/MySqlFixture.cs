using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Boots the API against a real MySQL or MariaDB server.
///
/// Everything else in this suite runs on the JSON files, which need no
/// configuration and therefore run everywhere. That leaves the entire MySQL
/// half of the persistence seam — the provider, the migrations, the seeder and
/// the four <c>MySql*Persistence</c> stores — executing for the first time on
/// the production server, which is the worst possible place to find out about
/// it. These tests close that gap when a server is available and skip
/// themselves when one is not.
///
/// Point them at a server with:
///
/// <code>
///   set GOPI_TEST_MYSQL=Server=203.0.113.10;Port=3306;Database=gopicrackers;User ID=gopi;Password=…;
///   dotnet test
/// </code>
///
/// <b>It does not touch the database named in that string.</b> The name is used
/// only to derive a scratch one beside it — <c>gopicrackers_apitest</c> — which
/// the API is then pointed at and left to create for itself. So the run also
/// proves the thing hardest to prove any other way: that pointing this API at a
/// server where the schema does not exist yet is enough, and no manual CREATE
/// DATABASE step is needed on deployment day.
///
/// The scratch database is left behind so its schema can be inspected
/// afterwards. Set <c>GOPI_TEST_MYSQL_DROP=1</c> to have it dropped instead.
/// </summary>
public sealed class MySqlFixture : WebApplicationFactory<Program>
{
    /// <summary>The server to test against, or null when none was configured.</summary>
    public static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable("GOPI_TEST_MYSQL") is { } value
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>
    /// Why these tests are being skipped, or null when they should run. xUnit
    /// reads <c>Skip</c> at discovery, so this has to be answerable without a
    /// server: it checks only that a string was supplied, not that it works.
    /// A configured server that turns out to be unreachable is a failure, not
    /// a skip — silently passing is exactly what this suite exists to prevent.
    /// </summary>
    public static string? SkipReason =>
        ServerConnectionString is null
            ? "Set GOPI_TEST_MYSQL to a MySQL/MariaDB connection string to run the MySQL-backed tests."
            : null;

    /// <summary>
    /// The suffix that marks a database as this suite's to create and drop.
    /// Nothing without it is ever written to or removed.
    /// </summary>
    private const string Suffix = "_apitest";

    public const string AdminPasscode = ApiFixture.AdminPasscode;

    private readonly string _dataPath = ApiFixture.CopyCatalogue();

    /// <summary>
    /// The supplied connection string redirected at the scratch database.
    /// </summary>
    public static string TestConnectionString => Redirect(ServerConnectionString!);

    /// <summary>
    /// Points a connection string at the scratch database beside the one it
    /// names, leaving everything else about it alone.
    ///
    /// Pure and internal so it can be tested without a server. It is the only
    /// thing standing between this suite and somebody's live shop data, and
    /// "we were fairly sure the suffix logic was right" is not the standard
    /// that deserves.
    /// </summary>
    internal static string Redirect(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);

        var baseName = string.IsNullOrWhiteSpace(builder.Database)
            ? "gopicrackers"
            : builder.Database;

        // Idempotent, so re-pointing an already-redirected string at the
        // scratch database does not produce gopicrackers_apitest_apitest.
        builder.Database = baseName.EndsWith(Suffix, StringComparison.Ordinal)
            ? baseName
            : baseName + Suffix;

        return builder.ConnectionString;
    }

    /// <summary>The suffix, exposed so the tests can assert on it by name.</summary>
    internal static string ScratchSuffix => Suffix;

    /// <summary>The scratch database's name, for messages and for the drop.</summary>
    public static string TestDatabaseName =>
        new MySqlConnectionStringBuilder(TestConnectionString).Database;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        => builder
            .UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development")
            .UseSetting("Catalog:DataPath", _dataPath)
            .UseSetting("Storefront:Admin:Passcode", AdminPasscode)
            .UseSetting("Storefront:Database:ConnectionString", TestConnectionString)

            // Both left on deliberately: they are the defaults a deployment
            // gets, so testing with them off would test a configuration nobody
            // runs. Together they are the claim being checked — point the API
            // at an empty server and it arrives with its schema and its
            // catalogue already in place.
            .UseSetting("Storefront:Database:AutoMigrate", "true")
            .UseSetting("Storefront:Database:SeedOnStart", "true");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try { Directory.Delete(_dataPath, recursive: true); }
        catch (IOException) { /* A temp directory left behind is not a test failure. */ }

        if (Environment.GetEnvironmentVariable("GOPI_TEST_MYSQL_DROP") == "1") DropTestDatabase();
    }

    /// <summary>
    /// Drops the scratch database, and only ever that one.
    ///
    /// The suffix is re-checked here rather than trusted from the property that
    /// built it. This is the one destructive statement in the suite, and the
    /// cost of the check is nothing next to the cost of a bug upstream of it
    /// pointing DROP DATABASE at a shop's live data.
    /// </summary>
    private static void DropTestDatabase()
    {
        var name = TestDatabaseName;

        if (!name.EndsWith(Suffix, StringComparison.Ordinal) || name.Contains('`')) return;

        try
        {
            var builder = new MySqlConnectionStringBuilder(TestConnectionString) { Database = string.Empty };

            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{name}`;";
            command.ExecuteNonQuery();
        }
        catch (MySqlException)
        {
            // A scratch database left behind is untidy, not a test failure —
            // and failing teardown would mask the result of the run itself.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "mysql";
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when no MySQL server has
/// been configured, so the suite stays green on a machine that has not got one.
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute() => Skip = MySqlFixture.SkipReason!;
}
