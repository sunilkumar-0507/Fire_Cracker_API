using GopiCrackers.Api.Models;
using GopiCrackers.Api.Services.Persistence;

namespace GopiCrackers.Api.Services;

/// <summary>
/// The stock intake ledger, and the report that reconciles it against sales.
///
/// Three numbers matter to a shopkeeper: what came in, what went out, and what
/// is left. Two of those already existed — the order book knows what sold, the
/// catalogue knows what is on the shelf — but nothing recorded stock arriving,
/// so "what is left" could never be checked against anything. This is that
/// missing third record.
///
/// Entries are appended, never edited or removed; a correction is a negative
/// entry. Where they are kept is <see cref="IInventoryPersistence"/>'s business:
/// a JSON file beside the catalogue, or a table nothing in this class ever
/// updates or deletes from.
/// </summary>
public sealed class InventoryStore
{
    private readonly CatalogStore _catalog;
    private readonly OrderStore _orders;
    private readonly TimeProvider _clock;
    private readonly IInventoryPersistence _persistence;
    private readonly Lock _gate = new();
    private readonly List<StockIntake> _entries = [];

    public InventoryStore(
        CatalogStore catalog,
        OrderStore orders,
        TimeProvider clock,
        IInventoryPersistence persistence)
    {
        _catalog = catalog;
        _orders = orders;
        _clock = clock;
        _persistence = persistence;

        _entries.AddRange(_persistence.Load());
    }

    /// <summary>Which backend the ledger is stored in — <c>files</c> or <c>mysql</c>.</summary>
    public string Backend => _persistence.Backend;

    /* ---------------------------------------------------------------------- */
    /* The ledger                                                              */
    /* ---------------------------------------------------------------------- */

    public IReadOnlyList<StockIntake> Entries
    {
        get { lock (_gate) return [.. _entries.OrderByDescending(e => e.ReceivedAt)]; }
    }

    /// <summary>
    /// Records a delivery and raises the product's stock by the same amount.
    ///
    /// The two move together deliberately: a shopkeeper who has just counted
    /// forty boxes onto the shelf should not have to also remember to edit the
    /// stock field, and a ledger that disagreed with the shelf would be worse
    /// than no ledger at all.
    /// </summary>
    public (StockIntake? Entry, string? Error) Record(StockIntakeWrite body)
    {
        var product = _catalog.FindProduct(body.ProductId.Trim());
        if (product is null) return (null, $"No product with the id or slug '{body.ProductId}'.");
        if (body.Quantity == 0) return (null, "A ledger entry of zero would record nothing.");

        if (body.Quantity < 0 && product.Stock + body.Quantity < 0)
            return (null, $"{product.Name} holds only {product.Stock}, so {-body.Quantity} cannot come off.");

        _catalog.SetStock(new Dictionary<string, int> { [product.Id] = product.Stock + body.Quantity });

        var entry = new StockIntake(
            Id: NextId(),
            ProductId: product.Id,
            Quantity: body.Quantity,
            ReceivedAt: _clock.GetUtcNow(),
            Supplier: string.IsNullOrWhiteSpace(body.Supplier) ? null : body.Supplier.Trim(),
            Note: string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim());

        lock (_gate)
        {
            _entries.Add(entry);
            _persistence.Append(entry, _entries);
        }

        return (entry, null);
    }

    /* ---------------------------------------------------------------------- */
    /* The report                                                              */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Intake, sales and holding per product, plus the totals the screen leads
    /// with. Built on each request: the catalogue and the order book are both in
    /// memory, so this is a scan of a few hundred rows, not a query.
    /// </summary>
    public InventoryReport Report(int lowStockAt = 20, int bestSellerLimit = 10)
    {
        var intakeByProduct = new Dictionary<string, (int Qty, DateTimeOffset Last)>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                var seen = intakeByProduct.GetValueOrDefault(entry.ProductId);
                intakeByProduct[entry.ProductId] =
                    (seen.Qty + entry.Quantity, entry.ReceivedAt > seen.Last ? entry.ReceivedAt : seen.Last);
            }
        }

        // A cancelled order never left the building, so it is not a sale.
        var soldByProduct = new Dictionary<string, (int Qty, int Revenue)>(StringComparer.OrdinalIgnoreCase);

        foreach (var order in _orders.AllOrders())
        {
            if (order.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var line in order.Items)
            {
                var seen = soldByProduct.GetValueOrDefault(line.Id);
                soldByProduct[line.Id] = (seen.Qty + line.Qty, seen.Revenue + line.LineTotal);
            }
        }

        var rows = _catalog.Products.Select(p =>
        {
            var intake = intakeByProduct.GetValueOrDefault(p.Id);
            var sold = soldByProduct.GetValueOrDefault(p.Id);

            return new InventoryRow(
                ProductId: p.Id,
                Code: p.Code,
                Name: p.Name,
                Slug: p.Slug,
                Category: p.Category,
                Image: p.Images.FirstOrDefault(),
                Price: p.Price,
                Intake: intake.Qty,
                Sold: sold.Qty,
                Holding: p.Stock,
                Revenue: sold.Revenue,
                // Only meaningful once something has been received; before that
                // every product would report its whole shelf as unaccounted for.
                Unaccounted: intake.Qty == 0 ? 0 : intake.Qty - sold.Qty - p.Stock,
                Availability: p.Availability,
                LastIntakeAt: intake.Last == default ? null : intake.Last);
        }).ToList();

        return new InventoryReport(
            Products: rows.Count,
            TotalIntake: rows.Sum(r => r.Intake),
            TotalSold: rows.Sum(r => r.Sold),
            TotalHolding: rows.Sum(r => r.Holding),
            TotalRevenue: rows.Sum(r => r.Revenue),
            OutOfStock: rows.Count(r => r.Holding <= 0),
            LowStock: rows.Count(r => r.Holding > 0 && r.Holding <= lowStockAt),
            HoldingValue: rows.Sum(r => r.Holding * r.Price),
            Rows: rows,
            BestSellers: [.. rows
                .Where(r => r.Sold > 0)
                .OrderByDescending(r => r.Sold)
                .ThenByDescending(r => r.Revenue)
                .Take(bestSellerLimit)],
            RecentIntake: [.. Entries.Take(25)]);
    }

    /* ---------------------------------------------------------------------- */
    /* Ids                                                                     */
    /* ---------------------------------------------------------------------- */

    /// <summary>IN######## — the shape order and enquiry references already use.</summary>
    private string NextId()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = $"IN{Random.Shared.Next(10_000_000, 99_999_999)}";
            if (!_entries.Any(e => e.Id == candidate)) return candidate;
        }

        return $"IN{Guid.NewGuid():N}"[..10];
    }
}
