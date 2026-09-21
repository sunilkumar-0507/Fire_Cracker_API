using GopiCrackers.Api.Data;
using GopiCrackers.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The analytics log in MySQL / MariaDB.
///
/// This is the one table where the database earns its keep over the file: a
/// flush inserts only what has happened since the last one, where the file
/// backend has to rewrite the entire window because a JSON array cannot be
/// appended to. A busy evening costs a few dozen INSERTs rather than fifty
/// thousand rows of rewriting.
/// </summary>
public sealed class MySqlAnalyticsPersistence(
    IDbContextFactory<GopiCrackersDbContext> factory,
    ILogger<MySqlAnalyticsPersistence> logger) : IAnalyticsPersistence
{
    public string Backend => "mysql";

    public IReadOnlyList<AnalyticsEvent> Load(int capacity)
    {
        using var db = factory.CreateDbContext();

        // Newest `capacity` rows, handed back oldest-first so the in-memory
        // window is in the same order the file backend produces.
        var events = db.AnalyticsEvents
            .AsNoTracking()
            .OrderByDescending(e => e.Key)
            .Take(capacity)
            .AsEnumerable()
            .Select(Mapping.ToModel)
            .Reverse()
            .ToList();

        logger.LogInformation("Restored {Count} analytics events from the database", events.Count);
        return events;
    }

    public void Flush(IReadOnlyList<AnalyticsEvent> all, IReadOnlyList<AnalyticsEvent> appended)
    {
        if (appended.Count == 0) return;

        using var db = factory.CreateDbContext();
        db.AnalyticsEvents.AddRange(appended.Select(Mapping.ToRow));
        db.SaveChanges();
    }

    /// <summary>
    /// Drops everything past the newest <paramref name="capacity"/> rows.
    ///
    /// Nothing here expires on its own, and a shop's traffic over a season is
    /// small but not bounded — so the cap that protects memory has to protect
    /// the table too, or the one place this design could grow without limit is
    /// the one nobody is watching.
    /// </summary>
    public void Trim(int capacity)
    {
        using var db = factory.CreateDbContext();

        var total = db.AnalyticsEvents.Count();
        if (total <= capacity) return;

        // The key is auto-increment, so "older than the newest N" is one
        // comparison rather than an offset scan.
        var cutoff = db.AnalyticsEvents
            .OrderByDescending(e => e.Key)
            .Skip(capacity - 1)
            .Select(e => e.Key)
            .FirstOrDefault();

        if (cutoff <= 0) return;

        var removed = db.AnalyticsEvents.Where(e => e.Key < cutoff).ExecuteDelete();
        logger.LogInformation("Trimmed {Removed} analytics events past the {Capacity} kept", removed, capacity);
    }
}
