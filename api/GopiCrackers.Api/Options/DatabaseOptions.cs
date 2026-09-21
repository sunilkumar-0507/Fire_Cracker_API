namespace GopiCrackers.Api.Options;

/// <summary>
/// Where the shop's data actually lives.
///
/// Everything here is optional, and that is the point: with no connection
/// string the API runs exactly as it always has, on the JSON files beside the
/// catalogue. Set one and every store switches to MySQL / MariaDB instead —
/// the same endpoints, the same responses, a different place to put them.
///
/// The connection string is read from <c>Storefront:Database:ConnectionString</c>
/// first and from the conventional <c>ConnectionStrings:GopiCrackers</c> second,
/// so a host that only knows how to set connection strings the standard way
/// (App Service, Docker's <c>ConnectionStrings__GopiCrackers</c>, a cPanel
/// environment variable) needs no special instructions.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Storefront:Database";

    /// <summary>The conventional key checked when the section leaves it blank.</summary>
    public const string ConnectionStringName = "GopiCrackers";

    /// <summary>
    /// A MySQL / MariaDB connection string, e.g.
    /// <c>Server=localhost;Port=3306;Database=gopicrackers;User ID=gopi;Password=…;</c>
    /// Blank means "use the JSON files", which is the default.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The server version Pomelo should generate SQL for, e.g. <c>8.0.36-mysql</c>
    /// or <c>10.11.6-mariadb</c>.
    ///
    /// Blank asks the provider to connect once at startup and find out. That is
    /// the friendlier default, but it means the API cannot start while the
    /// database is down — set this explicitly in production and startup stops
    /// depending on the database being awake.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Applies any pending EF Core migrations at startup. On by default because
    /// this is a single-instance shop API, not a fleet — the alternative is
    /// somebody having to remember `dotnet ef database update` after every
    /// deploy. Turn it off and use <c>POST /api/admin/database/migrate</c>.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// Copies the JSON catalogue into an empty database the first time it is
    /// seen. Only ever fills empty tables — see <see cref="Data.DatabaseSeeder"/>,
    /// which will not overwrite a shop's live catalogue without being asked.
    /// </summary>
    public bool SeedOnStart { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Retries on a transient connection failure. Shared hosting drops idle
    /// MySQL connections aggressively, and a dropped socket should cost a
    /// retry rather than a customer's order.
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// How many analytics events the table is trimmed to. Mirrors the in-memory
    /// cap, so the file, the memory and the table all hold the same window.
    /// </summary>
    public int AnalyticsRetention { get; set; } = 50_000;

    /// <summary>True when a connection string has been configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// The connection string with the password blanked, for logs and for
    /// <c>GET /api/admin/database</c>. A status endpoint that echoed the
    /// password back would be a worse leak than not having the endpoint.
    /// </summary>
    public string Redacted => Redact(ConnectionString);

    internal static string Redact(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return string.Empty;

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Select(part =>
        {
            var split = part.IndexOf('=');
            if (split <= 0) return part;

            var key = part[..split].Trim();
            return key.Replace(" ", string.Empty).ToLowerInvariant() is "password" or "pwd"
                ? $"{key}=***"
                : part;
        });

        return string.Join(';', kept);
    }
}
