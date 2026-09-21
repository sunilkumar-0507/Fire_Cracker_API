using System.Text.Json;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The stock intake ledger as <c>stock-intake.json</c>.
///
/// The ledger is append-only, so a write is the whole list again — which for a
/// shop taking a few deliveries a week is a file measured in kilobytes. Temp
/// file then swap, as the order journal does.
/// </summary>
public sealed class JsonInventoryPersistence(DataFiles files, ILogger<JsonInventoryPersistence> logger)
    : IInventoryPersistence
{
    private const string File = "stock-intake.json";
    private readonly Lock _gate = new();

    public string Backend => "files";

    public IReadOnlyList<StockIntake> Load()
    {
        lock (_gate)
        {
            var path = files.Path(File);
            if (!System.IO.File.Exists(path)) return [];

            try
            {
                using var stream = System.IO.File.OpenRead(path);
                var stored = JsonSerializer.Deserialize<List<StockIntake>>(stream, PersistenceJson.Options) ?? [];
                logger.LogInformation("Restored {Count} stock intake entries from {Path}", stored.Count, path);
                return stored;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not read {Path}; starting with an empty ledger", path);
                return [];
            }
        }
    }

    public void Append(StockIntake entry, IReadOnlyList<StockIntake> all)
    {
        lock (_gate)
        {
            try
            {
                files.WriteAtomically(
                    File,
                    JsonSerializer.Serialize(all, PersistenceJson.Options) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not write {Path}", files.Path(File));
            }
        }
    }
}
