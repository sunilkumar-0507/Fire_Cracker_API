using System.Text.Json;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The order book as <c>orders.json</c> beside the catalogue — the former
/// <c>OrderJournal</c>, now behind an interface.
///
/// Orders are the one thing here a shopkeeper cannot re-derive: losing the
/// catalogue means re-running the price-list importer, losing the orders means
/// losing the day's trade. So they are flushed on every write, through a temp
/// file that is swapped into place — an interrupted save leaves the previous
/// list intact rather than a truncated one.
///
/// A read failure is logged and treated as "no orders yet" rather than thrown:
/// a corrupt journal should not stop the shop from taking today's orders.
///
/// <b>Enquiries, contact messages and subscribers are deliberately not kept
/// here.</b> They are names, phone numbers and email addresses, and a permanent
/// plain-text file of those sitting inside the repository's own <c>src/data</c>
/// — the folder the storefront is built from and the folder that gets committed
/// — is a liability the shop never asked for. On this backend they live as long
/// as the process does and the notification email is the durable copy. Point
/// the API at a database and they are kept properly, in a table, where personal
/// data belongs; see <see cref="MySqlOrderPersistence"/>.
/// </summary>
public sealed class JsonOrderPersistence(DataFiles files, ILogger<JsonOrderPersistence> logger)
    : IOrderPersistence
{
    private const string File = "orders.json";
    private readonly Lock _gate = new();

    public string Backend => "files";

    public IReadOnlyList<Order> LoadOrders()
    {
        lock (_gate)
        {
            var path = files.Path(File);
            if (!System.IO.File.Exists(path)) return [];

            try
            {
                using var stream = System.IO.File.OpenRead(path);
                var orders = JsonSerializer.Deserialize<List<Order>>(stream, PersistenceJson.Options) ?? [];
                logger.LogInformation("Restored {Count} orders from {Path}", orders.Count, path);
                return orders;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not read {Path}; starting with no orders", path);
                return [];
            }
        }
    }

    public void SaveOrder(Order order, IReadOnlyList<Order> all)
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
                // The order is already accepted in memory and the customer has a
                // confirmation. Failing the response now would be worse than
                // carrying on with a journal that is one write behind.
                logger.LogError(ex, "Could not write {Path}", files.Path(File));
            }
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Personal data — held for the life of the process only. See the note above. */
    /* ---------------------------------------------------------------------- */

    public IReadOnlyList<BulkEnquiry> LoadEnquiries() => [];

    public void SaveEnquiry(BulkEnquiry enquiry) { }

    public IReadOnlyList<ContactMessage> LoadMessages() => [];

    public void SaveMessage(ContactMessage message) { }

    public IReadOnlyDictionary<string, DateTimeOffset> LoadSubscribers() =>
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    public void SaveSubscriber(string email, DateTimeOffset since) { }

    public bool DeleteSubscriber(string email) => true;
}
