using GopiCrackers.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GopiCrackers.Api.Data;

/// <summary>
/// How <c>dotnet ef</c> builds a context without starting the API.
///
/// The tooling would otherwise run <c>Program.cs</c>, which connects to the
/// database at startup — so generating a migration would need a live server,
/// and generating the <em>first</em> migration would need a live server with a
/// schema that does not exist yet. This sidesteps that: it reads the connection
/// string if there is one, and falls back to a placeholder when there is not.
///
/// The placeholder is never connected to. A migration is generated from the
/// model in <see cref="GopiCrackersDbContext"/> and the target's version, and
/// the version is pinned here rather than detected for the same reason.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GopiCrackersDbContext>
{
    /// <summary>
    /// MySQL 8.0 — the oldest release the generated SQL needs to run on. It is
    /// what every managed MySQL and every current MariaDB accepts, and picking
    /// something newer would generate DDL a shared host might refuse.
    /// </summary>
    private static readonly MySqlServerVersion Version = new(new System.Version(8, 0, 36));

    public GopiCrackersDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Coalescing on null is not enough: appsettings.json ships the key
        // present and blank, and an empty string is not a null one. Without the
        // whitespace check the tooling fails with "the string argument
        // 'connectionString' cannot be empty" on a clean checkout — that is,
        // exactly when somebody is trying to generate the first migration.
        var connectionString = FirstConfigured(
            configuration[$"{DatabaseOptions.SectionName}:ConnectionString"],
            configuration.GetConnectionString(DatabaseOptions.ConnectionStringName))
            ?? "Server=127.0.0.1;Port=3306;Database=gopicrackers;User ID=root;Password=;";

        var options = new DbContextOptionsBuilder<GopiCrackersDbContext>()
            .UseMySql(connectionString, Version)
            .Options;

        return new GopiCrackersDbContext(options);
    }

    private static string? FirstConfigured(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
