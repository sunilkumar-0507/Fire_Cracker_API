using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// Stock monitoring: what came in, what sold, what is left.
///
/// The Stock screen answers "what should I reorder"; this one answers "where
/// did it all go". They are deliberately separate — one is a bulk edit of a
/// single number, the other is a ledger nobody should be able to overwrite.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/inventory")]
public sealed class AdminInventoryController(InventoryStore inventory) : ControllerBase
{
    /// <summary>
    /// The whole report: per-product intake, sales and holding, the totals, the
    /// best sellers by units, and the most recent ledger entries.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<InventoryReport>(StatusCodes.Status200OK)]
    public ActionResult<InventoryReport> Get([FromQuery] int lowStockAt = 20, [FromQuery] int bestSellers = 10)
        => Ok(inventory.Report(Math.Clamp(lowStockAt, 0, 10_000), Math.Clamp(bestSellers, 1, 100)));

    /// <summary>Every ledger entry, newest first.</summary>
    [HttpGet("intake")]
    [ProducesResponseType<IReadOnlyList<StockIntake>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<StockIntake>> Intake() => Ok(inventory.Entries);

    /// <summary>
    /// Records a delivery, raising the product's stock by the same amount. A
    /// negative quantity is how breakage or a miscount is written off — the
    /// ledger is append-only, so a mistake is corrected, never erased.
    /// </summary>
    [HttpPost("intake")]
    [ProducesResponseType<StockIntake>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<StockIntake> RecordIntake([FromBody] StockIntakeWrite body)
    {
        var (entry, error) = inventory.Record(body);

        if (entry is null)
        {
            ModelState.AddModelError(nameof(body.ProductId), error ?? "Could not record that intake.");
            return ValidationProblem(new ValidationProblemDetails(ModelState)
            {
                Title = "Stock intake was not recorded",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        return CreatedAtAction(nameof(Intake), new { }, entry);
    }
}
