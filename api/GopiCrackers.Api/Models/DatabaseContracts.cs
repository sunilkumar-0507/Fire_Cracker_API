namespace GopiCrackers.Api.Models;

/// <summary>
/// What <c>GET /api/admin/database</c> answers.
///
/// Written for somebody staring at a deployment that is not behaving: is there
/// a database at all, can the API reach it, has the schema been applied, and is
/// there anything in it. Every one of those has been a five-minute puzzle at
/// some point, and every one of them is a field here.
/// </summary>
public sealed record DatabaseStatus(
    /// <summary>False when no connection string is configured — the API is on files.</summary>
    bool Enabled,
    /// <summary><c>files</c> or <c>mysql</c>. What the stores are actually using.</summary>
    string Backend,
    string Provider,
    /// <summary>The connection string with the password blanked out.</summary>
    string? ConnectionString,
    string? Database,
    /// <summary>The server's own version string, once it has been reached.</summary>
    string? ServerVersion,
    bool CanConnect,
    /// <summary>Why not, when <see cref="CanConnect"/> is false.</summary>
    string? Error,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<TableCount> Tables,
    DateTimeOffset CheckedAt)
{
    /// <summary>The answer when the API is running on JSON files.</summary>
    public static DatabaseStatus Disabled(string backend, DateTimeOffset at) => new(
        Enabled: false,
        Backend: backend,
        Provider: "none",
        ConnectionString: null,
        Database: null,
        ServerVersion: null,
        CanConnect: false,
        Error: "No connection string is configured. Set Storefront:Database:ConnectionString " +
               "(or ConnectionStrings:GopiCrackers) to move the shop onto MySQL or MariaDB.",
        AppliedMigrations: [],
        PendingMigrations: [],
        Tables: [],
        CheckedAt: at);
}

public sealed record TableCount(string Table, long Rows);

/// <summary>What a migrate call did.</summary>
public sealed record MigrationResult(
    bool Ok,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    string Message);

/// <summary>What a seed call put where.</summary>
public sealed record SeedResult(
    bool Seeded,
    int Products,
    int Categories,
    int Combos,
    int Offers,
    int Banners,
    int Testimonials,
    int Faqs,
    int Orders,
    int StockIntake,
    int AnalyticsEvents,
    /// <summary>Set when nothing was seeded and it is worth saying why.</summary>
    string? Note)
{
    public static SeedResult Nothing(string note) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, note);
}

public sealed record SeedRequest
{
    /// <summary>
    /// Replaces the catalogue with the JSON files' version. Off by default,
    /// because the usual answer to "seed a database that already has a
    /// catalogue in it" is "don't".
    ///
    /// Never touches orders, the stock ledger or analytics whatever it is set
    /// to: those are the shop's own trade, and a file cannot be a more recent
    /// truth about them than the database is.
    /// </summary>
    public bool Overwrite { get; init; }
}

/// <summary>What a catalogue reload found.</summary>
public sealed record ReloadResult(
    string Backend,
    int Products,
    int Categories,
    int Combos,
    int Offers,
    int Banners,
    int Testimonials,
    int Faqs);
