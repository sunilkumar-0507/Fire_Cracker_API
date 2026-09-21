using System.Text.Json;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The analytics log as <c>analytics.json</c>.
///
/// The whole window is rewritten on each flush rather than appended to, because
/// a JSON array cannot be appended to without reading it back first. The store
/// batches behind a timer for exactly this reason: losing the last few seconds
/// of page views if the process is killed costs nothing, and an fsync on every
/// scroll would cost a great deal.
///
/// <see cref="Trim"/> is a no-op: the store hands over an already-capped
/// window, so the file is trimmed by being rewritten.
/// </summary>
public sealed class JsonAnalyticsPersistence(DataFiles files, ILogger<JsonAnalyticsPersistence> logger)
    : IAnalyticsPersistence
{
    private const string File = "analytics.json";
    private readonly Lock _gate = new();

    public string Backend => "files";

    public IReadOnlyList<AnalyticsEvent> Load(int capacity)
    {
        lock (_gate)
        {
            var path = files.Path(File);
            if (!System.IO.File.Exists(path)) return [];

            try
            {
                using var stream = System.IO.File.OpenRead(path);
                var stored = JsonSerializer.Deserialize<List<AnalyticsEvent>>(stream, PersistenceJson.Options) ?? [];
                var window = stored.TakeLast(capacity).ToList();
                logger.LogInformation("Restored {Count} analytics events from {Path}", window.Count, path);
                return window;
            }
            catch (Exception ex)
            {
                // Analytics are the least important thing here. A corrupt file means
                // starting the counts again, never a shop that will not take orders.
                logger.LogError(ex, "Could not read {Path}; starting with no analytics", path);
                return [];
            }
        }
    }

    public void Flush(IReadOnlyList<AnalyticsEvent> all, IReadOnlyList<AnalyticsEvent> appended)
    {
        lock (_gate)
        {
            files.WriteAtomically(
                File,
                JsonSerializer.Serialize(all, PersistenceJson.Options) + Environment.NewLine);
        }
    }

    public void Trim(int capacity) { }
}
