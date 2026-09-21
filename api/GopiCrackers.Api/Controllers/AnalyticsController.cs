using GopiCrackers.Api.Models;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// Where the storefront files what happened — requirement 13.
///
/// Open by design: it has to be callable from an anonymous shopper's browser.
/// That makes it the one endpoint here anybody can write to, so it is kept
/// deliberately boring — a closed list of event types, a length cap on every
/// string, at most 50 events per request, and a hard cap on how many are
/// retained. Nothing it stores identifies a person, so the worst a flood can do
/// is make the shop's own numbers wrong.
/// </summary>
[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController(AnalyticsStore analytics) : ControllerBase
{
    /// <summary>
    /// Records a batch of events. Always 202 when at least one lands —
    /// analytics must never be something a shopper's page can fail on.
    /// </summary>
    [HttpPost("events")]
    [ProducesResponseType<AnalyticsAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<AnalyticsAccepted> Record([FromBody] AnalyticsBatch batch)
    {
        var result = analytics.Record(batch.Events);
        return Accepted(result);
    }

    /// <summary>The event types the storefront may send.</summary>
    [HttpGet("events")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> KnownEvents() => Ok(AnalyticsEvents.All);
}

/// <summary>The report behind the admin's analytics screen.</summary>
[ApiController]
[AdminOnly]
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController(AnalyticsStore analytics, CatalogStore catalog) : ControllerBase
{
    /// <summary>
    /// Product views, basket activity, searches and the order funnel over the
    /// last <paramref name="days"/> days. Clamped to a year.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<AnalyticsReport>(StatusCodes.Status200OK)]
    public ActionResult<AnalyticsReport> Report([FromQuery] int days = 30) =>
        Ok(analytics.Report(days, catalog));
}
