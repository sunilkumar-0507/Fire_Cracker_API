using GopiCrackers.Api.Data;
using GopiCrackers.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The stock intake ledger in MySQL / MariaDB.
///
/// Append-only in the store and append-only here: one INSERT per delivery, no
/// UPDATE and no DELETE anywhere in this class. A mistake is corrected by
/// recording a negative entry, which is how a paper stock book is corrected and
/// leaves the same audit trail.
/// </summary>
public sealed class MySqlInventoryPersistence(
    IDbContextFactory<GopiCrackersDbContext> factory,
    ILogger<MySqlInventoryPersistence> logger) : IInventoryPersistence
{
    public string Backend => "mysql";

    public IReadOnlyList<StockIntake> Load()
    {
        using var db = factory.CreateDbContext();

        var entries = db.StockIntake
            .AsNoTracking()
            .OrderBy(e => e.ReceivedAt)
            .AsEnumerable()
            .Select(Mapping.ToModel)
            .ToList();

        logger.LogInformation("Restored {Count} stock intake entries from the database", entries.Count);
        return entries;
    }

    public void Append(StockIntake entry, IReadOnlyList<StockIntake> all)
    {
        using var db = factory.CreateDbContext();
        db.StockIntake.Add(entry.ToRow());
        db.SaveChanges();
    }
}
