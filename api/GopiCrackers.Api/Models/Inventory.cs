using System.ComponentModel.DataAnnotations;

namespace GopiCrackers.Api.Models;

/// <summary>
/// One delivery of stock into the shop.
///
/// The ledger is append-only on purpose. A stock record whose history can be
/// edited is a stock record nobody can reconcile against, so a mistake is
/// corrected by recording a negative entry — the same way a paper book is
/// corrected, and with the same audit trail.
/// </summary>
public sealed record StockIntake(
    string Id,
    string ProductId,
    int Quantity,
    DateTimeOffset ReceivedAt,
    string? Supplier,
    string? Note);

public sealed record StockIntakeWrite
{
    [Required(ErrorMessage = "Which product?")]
    public string ProductId { get; init; } = string.Empty;

    /// <summary>
    /// Negative is allowed, and is how a miscount or a breakage is written off.
    /// Zero is not: it would be a ledger line that changes nothing.
    /// </summary>
    [Range(-100_000, 100_000)]
    public int Quantity { get; init; }

    [StringLength(120)]
    public string? Supplier { get; init; }

    [StringLength(400)]
    public string? Note { get; init; }
}

/// <summary>What happened to one product's stock, and where that leaves it.</summary>
public sealed record InventoryRow(
    string ProductId,
    string Code,
    string Name,
    string Slug,
    string Category,
    string? Image,
    int Price,
    /// <summary>Units received, from the intake ledger.</summary>
    int Intake,
    /// <summary>Units sold, from every order that was not cancelled.</summary>
    int Sold,
    /// <summary>Units still on the shelf — the catalogue's own figure.</summary>
    int Holding,
    /// <summary>What those units sold for.</summary>
    int Revenue,
    /// <summary>
    /// <c>Intake - Sold - Holding</c>. Zero means the book balances. Anything
    /// else is stock that arrived before the ledger existed, or a count that has
    /// drifted — worth showing rather than hiding, because it is the number a
    /// shopkeeper actually wants to chase.
    /// </summary>
    int Unaccounted,
    string Availability,
    DateTimeOffset? LastIntakeAt);

/// <summary>The stock report behind the admin's inventory screen.</summary>
public sealed record InventoryReport(
    int Products,
    int TotalIntake,
    int TotalSold,
    int TotalHolding,
    int TotalRevenue,
    int OutOfStock,
    int LowStock,
    /// <summary>Holding valued at what it would sell for.</summary>
    int HoldingValue,
    IReadOnlyList<InventoryRow> Rows,
    /// <summary>The same rows ordered by units sold, best first.</summary>
    IReadOnlyList<InventoryRow> BestSellers,
    IReadOnlyList<StockIntake> RecentIntake);
