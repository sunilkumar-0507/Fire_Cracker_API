using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services.Persistence;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>
/// Records what happens on the storefront and aggregates it for the admin.
///
/// Deliberately small: a bounded in-memory list, flushed through
/// <see cref="IAnalyticsPersistence"/> to a JSON file or a table. There is no
/// third-party tag, no cookie and nothing that identifies a person — see
/// <see cref="AnalyticsEventRequest"/> for what an event is allowed to carry.
///
/// The cap matters. A shop's traffic over a season is small, but nothing here
/// expires on its own, so the list is trimmed to <see cref="Capacity"/> newest
/// events. That bounds both memory and the file, and the report only ever looks
/// back a few weeks anyway.
///
/// Writes are batched behind a timer rather than flushed per event: losing the
/// last few seconds of page views if the process is killed costs nothing, and
/// an fsync on every scroll would cost a great deal.
/// </summary>
public sealed class AnalyticsStore : IDisposable
{
    /// <summary>Roughly a season of traffic for a shop this size.</summary>
    public const int Capacity = 50_000;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    private readonly List<AnalyticsEvent> _events = [];

    /// <summary>
    /// What has arrived since the last flush.
    ///
    /// Kept apart from <see cref="_events"/> because the two backends want
    /// different things: a JSON file has to be rewritten whole, a table only
    /// wants the new rows. Tracking the delta here means neither has to
    /// rediscover it, and a flush that fails can put the batch back.
    /// </summary>
    private readonly List<AnalyticsEvent> _pending = [];

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly IAnalyticsPersistence _persistence;
    private readonly ILogger<AnalyticsStore> _logger;
    private readonly ITimer _timer;
    private readonly int _retention;

    public AnalyticsStore(
        TimeProvider clock,
        IAnalyticsPersistence persistence,
        IOptions<DatabaseOptions> database,
        ILogger<AnalyticsStore> logger)
    {
        _clock = clock;
        _persistence = persistence;
        _logger = logger;
        _retention = Math.Clamp(database.Value.AnalyticsRetention, 1_000, Capacity);

        _events.AddRange(_persistence.Load(Capacity));

        _timer = clock.CreateTimer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    /// <summary>Which backend events are kept in — <c>files</c> or <c>mysql</c>.</summary>
    public string Backend => _persistence.Backend;

    /* ---------------------------------------------------------------------- */
    /* Writes                                                                  */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Records a batch, returning how many landed and which types were refused.
    /// One bad type does not fail the batch — a storefront that shipped a typo
    /// should lose that event, not the nine good ones beside it.
    /// </summary>
    public AnalyticsAccepted Record(IReadOnlyList<AnalyticsEventRequest> batch)
    {
        var now = _clock.GetUtcNow();
        var rejected = new List<string>();
        var accepted = new List<AnalyticsEvent>(batch.Count);

        foreach (var item in batch)
        {
            var type = item.Type.Trim().ToLowerInvariant();
            if (!AnalyticsEvents.IsKnown(type))
            {
                if (!rejected.Contains(type)) rejected.Add(type);
                continue;
            }

            accepted.Add(new AnalyticsEvent(
                Type: type,
                // The server's clock, so a browser with the wrong date cannot
                // file events into next week and skew every report after it.
                At: now,
                Ref: Trim(item.Ref),
                Label: Trim(item.Label),
                Value: Math.Max(0, item.Value),
                Session: Trim(item.Session)));
        }

        if (accepted.Count > 0)
        {
            lock (_gate)
            {
                _events.AddRange(accepted);
                _pending.AddRange(accepted);
                if (_events.Count > Capacity) _events.RemoveRange(0, _events.Count - Capacity);
            }
        }

        return new AnalyticsAccepted(accepted.Count, rejected);
    }

    /// <summary>
    /// Records one event server-side — an order, an enquiry.
    ///
    /// <paramref name="session"/> is the browser's anonymous per-tab id, passed
    /// through from the request that caused this. Without it the event is still
    /// counted, but it cannot be attributed to a visit, so the funnel's last
    /// step would read zero however many orders came in.
    ///
    /// Recording these here rather than from the browser is deliberate: a
    /// checkout that navigates away, a blocked request or a closed tab must not
    /// be able to lose the one event the shop actually cares about.
    /// </summary>
    public void Record(
        string type,
        string? reference = null,
        string? label = null,
        int value = 0,
        string? session = null) =>
        Record([new AnalyticsEventRequest
        {
            Type = type,
            Ref = reference,
            Label = label,
            Value = value,
            Session = session,
        }]);

    /* ---------------------------------------------------------------------- */
    /* The report                                                              */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Aggregates the window in one pass per question. <paramref name="catalog"/>
    /// is passed in rather than held, so a product renamed since the event was
    /// recorded shows its current name in the report.
    /// </summary>
    public AnalyticsReport Report(int days, CatalogStore catalog)
    {
        days = Math.Clamp(days, 1, 365);

        var now = _clock.GetUtcNow();
        var since = now.AddDays(-days);
        var from = DateOnly.FromDateTime(since.UtcDateTime);
        var to = DateOnly.FromDateTime(now.UtcDateTime);

        List<AnalyticsEvent> window;
        lock (_gate) window = [.. _events.Where(e => e.At >= since)];

        var names = catalog.Products.ToDictionary(p => p.Slug, p => p.Name, StringComparer.OrdinalIgnoreCase);
        var categoryNames = catalog.Categories.ToDictionary(c => c.Slug, c => c.Name, StringComparer.OrdinalIgnoreCase);

        var sessions = Reach(window, null);

        // Each stage is how many *visits* got that far, which is the only unit
        // in which "of the people who looked, this many bought" is a true
        // sentence. Counting events instead would let one person reloading a
        // product page six times outvote six people who each looked once.
        var viewed = Reach(window, AnalyticsEvents.ProductView);
        var basketed = Reach(window, AnalyticsEvents.CartAdd);
        var checkouts = Reach(window, AnalyticsEvents.CheckoutStart);
        var ordered = Reach(window, AnalyticsEvents.OrderPlaced);

        var funnel = new Funnel(
            Sessions: sessions,
            ProductViews: viewed,
            CartAdds: basketed,
            CheckoutStarts: checkouts,
            Orders: ordered,
            ViewToCartRate: Rate(basketed, viewed),
            CartToCheckoutRate: Rate(checkouts, basketed),
            CheckoutToOrderRate: Rate(ordered, checkouts),
            ProductViewEvents: window.Count(e => e.Type == AnalyticsEvents.ProductView),
            CartAddEvents: window.Count(e => e.Type == AnalyticsEvents.CartAdd));

        var daily = new List<DailyPoint>(days);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var onDay = window.Where(e => DateOnly.FromDateTime(e.At.UtcDateTime) == day).ToList();
            daily.Add(new DailyPoint(
                Day: day,
                Views: onDay.Count(e => e.Type == AnalyticsEvents.ProductView),
                CartAdds: onDay.Count(e => e.Type == AnalyticsEvents.CartAdd),
                Orders: onDay.Count(e => e.Type == AnalyticsEvents.OrderPlaced),
                Revenue: onDay.Where(e => e.Type == AnalyticsEvents.OrderPlaced).Sum(e => e.Value)));
        }

        return new AnalyticsReport(
            Days: days,
            From: from,
            To: to,
            TotalEvents: window.Count,
            Funnel: funnel,
            TopProducts: Top(window, AnalyticsEvents.ProductView, names),
            TopCategories: Top(window, AnalyticsEvents.CategoryView, categoryNames),
            TopSearches: Top(window, AnalyticsEvents.Search, null),
            MostAddedToCart: Top(window, AnalyticsEvents.CartAdd, names),
            ByType:
            [
                .. AnalyticsEvents.All
                    .Select(t => new CountedRef(t, Humanise(t), window.Count(e => e.Type == t)))
                    .Where(c => c.Count > 0)
                    .OrderByDescending(c => c.Count)
            ],
            Daily: daily,
            // A search that found nothing is the most actionable line in the
            // whole report: it is a customer telling the shop what to stock.
            SearchesWithNoResults: Top(
                [.. window.Where(e => e.Type == AnalyticsEvents.Search && e.Value == 0)],
                AnalyticsEvents.Search,
                null));
    }

    private static IReadOnlyList<CountedRef> Top(
        IReadOnlyList<AnalyticsEvent> window,
        string type,
        IReadOnlyDictionary<string, string>? names,
        int limit = 10) =>
    [
        .. window
            .Where(e => e.Type == type && !string.IsNullOrWhiteSpace(e.Ref))
            .GroupBy(e => e.Ref!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CountedRef(
                Ref: g.Key,
                Label: names?.GetValueOrDefault(g.Key) ?? g.First().Label ?? g.Key,
                Count: g.Count(),
                Value: g.Sum(e => e.Value)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
    ];

    /// <summary>A percentage to one decimal place; zero out of zero is zero, not NaN.</summary>
    private static double Rate(int part, int whole) =>
        whole <= 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    /// <summary>
    /// Distinct sessions that produced <paramref name="type"/> — or that
    /// produced anything at all, when it is null.
    ///
    /// Events without a session (storage disabled, or recorded server-side by
    /// the checkout) cannot be attributed to a visit and are left out rather
    /// than counted as one visit each, which would inflate every stage.
    /// </summary>
    private static int Reach(IReadOnlyList<AnalyticsEvent> window, string? type) =>
        window
            .Where(e => e.Session is not null && (type is null || e.Type == type))
            .Select(e => e.Session!)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static string Humanise(string type) =>
        string.Join(' ', type.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /* ---------------------------------------------------------------------- */
    /* Persistence                                                             */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Hands the batch that has built up since the last call to the backend.
    ///
    /// On failure the batch goes back on the queue rather than being dropped —
    /// a database that is briefly unreachable should cost a delay, not the
    /// evening's figures. The window itself is never rolled back, so the report
    /// stays correct either way.
    /// </summary>
    public void Flush()
    {
        List<AnalyticsEvent> window;
        List<AnalyticsEvent> batch;

        lock (_gate)
        {
            if (_pending.Count == 0) return;
            window = [.. _events];
            batch = [.. _pending];
            _pending.Clear();
        }

        try
        {
            _persistence.Flush(window, batch);
            Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not flush {Count} analytics events; retrying on the next tick", batch.Count);
            lock (_gate) _pending.InsertRange(0, batch);
        }
    }

    /// <summary>
    /// Keeps the stored window to the same cap the in-memory one holds.
    ///
    /// Only worth doing occasionally — trimming on every flush would run a
    /// COUNT every fifteen seconds to discover that nothing needs removing.
    /// </summary>
    private void Trim()
    {
        if (Interlocked.Increment(ref _flushes) % TrimEvery != 0) return;

        try
        {
            _persistence.Trim(_retention);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not trim the analytics log to {Retention}", _retention);
        }
    }

    private int _flushes;

    /// <summary>Once an hour, at a fifteen-second flush interval.</summary>
    private const int TrimEvery = 240;

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
