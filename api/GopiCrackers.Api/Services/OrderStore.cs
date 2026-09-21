using System.Collections.Concurrent;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Services.Persistence;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>
/// Store for everything the storefront writes: orders, bulk enquiries, contact
/// messages and newsletter subscribers.
///
/// Records are held in memory and mirrored through <see cref="IOrderPersistence"/>,
/// so the admin order list survives a restart. This replaces the mock
/// <c>api.placeOrder</c> promise, not a payment system — no card, UPI or bank
/// detail is accepted by any endpoint or written anywhere.
///
/// The four collections do not all outlive the process on every backend.
/// Orders always do. Enquiries, contact messages and subscribers are kept when
/// the API runs on a database and are deliberately dropped when it runs on
/// files — see <see cref="JsonOrderPersistence"/> for why.
/// </summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<string, Order> _orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BulkEnquiry> _enquiries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ContactMessage> _messages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _subscribers = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _clock;
    private readonly IOrderPersistence _persistence;
    private readonly CatalogStore _catalog;
    private readonly StorefrontOptions _options;

    /// <summary>
    /// The statuses an order moves through, in the order they happen. Named for
    /// what a shopkeeper would say out loud rather than for a courier's
    /// vocabulary, so the same five words describe a delivery and a pickup.
    /// <c>cancelled</c> sits outside the sequence — it can happen from anywhere.
    /// </summary>
    public static readonly IReadOnlyList<string> Statuses =
        ["pending", "confirmed", "processing", "ready", "completed", "cancelled"];

    /// <summary>The steps a progress bar draws, i.e. everything but cancellation.</summary>
    public static readonly IReadOnlyList<string> ProgressSteps =
        ["pending", "confirmed", "processing", "ready", "completed"];

    /// <summary>Statuses that still need something doing — the dashboard's queue.</summary>
    public static readonly IReadOnlyList<string> OpenStatuses =
        ["pending", "confirmed", "processing", "ready"];

    /// <summary>
    /// Labels differ by fulfilment: "Ready" means "on the lorry" for a delivery
    /// and "waiting at the counter" for a pickup, and a customer reading one
    /// should not see the other's wording.
    /// </summary>
    public static string Label(string status, bool pickup) => status.ToLowerInvariant() switch
    {
        "pending" => "Order received",
        "confirmed" => "Confirmed by the shop",
        "processing" => "Being packed",
        "ready" => pickup ? "Ready to collect" : "Out for delivery",
        "completed" => pickup ? "Collected" : "Delivered",
        "cancelled" => "Cancelled",
        _ => status,
    };

    /// <summary>
    /// Statuses written before this vocabulary existed, mapped onto it. Kept so
    /// a journal from an earlier run still loads instead of being dropped.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["packed"] = "processing",
        ["shipped"] = "ready",
        ["delivered"] = "completed",
    };

    public OrderStore(
        TimeProvider clock,
        IOrderPersistence persistence,
        CatalogStore catalog,
        IOptions<StorefrontOptions> options)
    {
        _clock = clock;
        _persistence = persistence;
        _catalog = catalog;
        _options = options.Value;

        foreach (var order in _persistence.LoadOrders())
        {
            var migrated = Migrate(order);
            _orders[migrated.OrderId] = migrated;
        }

        foreach (var enquiry in _persistence.LoadEnquiries())
            _enquiries[enquiry.EnquiryId] = enquiry;

        foreach (var message in _persistence.LoadMessages())
            _messages[message.MessageId] = message;

        foreach (var (email, since) in _persistence.LoadSubscribers())
            _subscribers[email] = since;
    }

    /// <summary>Which backend the order book is stored in — <c>files</c> or <c>mysql</c>.</summary>
    public string Backend => _persistence.Backend;

    /// <summary>
    /// Brings a journalled order up to the current shape: a legacy status is
    /// renamed, and an order that predates history gets a single-entry one so
    /// the tracking page has something to draw.
    /// </summary>
    private static Order Migrate(Order order)
    {
        var status = LegacyStatuses.GetValueOrDefault(order.Status, order.Status);

        List<OrderEvent> history = order.History is { Count: > 0 } existing
            ? [.. existing.Select(e => e with
                {
                    Status = LegacyStatuses.GetValueOrDefault(e.Status, e.Status),
                })]
            : [new OrderEvent(status, order.PlacedAt)];

        return order with { Status = status, History = history };
    }

    /* ---------------------------------------------------------------------- */
    /* Orders                                                                  */
    /* ---------------------------------------------------------------------- */

    /// <summary>Statuses in which the goods are considered off the shelf.</summary>
    private static bool HoldsStock(string status) =>
        !status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);

    public Order PlaceOrder(OrderRequest request, CartQuote quote)
    {
        var now = _clock.GetUtcNow();
        var placedOn = DateOnly.FromDateTime(now.UtcDateTime);
        var fulfilment = Fulfilment.Normalise(request.Fulfilment);
        var pickup = fulfilment == Fulfilment.Pickup;

        // A pickup has no delivery address, so the order records the counter it
        // will be collected from. That keeps every order self-describing: the
        // packing slip says where it is going either way.
        var address = pickup ? _options.Brand.Address : request.Address!.Trim();
        var city = pickup ? "Sivakasi" : request.City!.Trim();
        var district = pickup ? "Virudhunagar" : request.District!.Trim();
        var pincode = pickup ? "626123" : request.Pincode!.Trim();

        var order = new Order(
            OrderId: NextId("AC", _orders.ContainsKey),
            PlacedAt: now,
            Status: "pending",
            Name: request.Name.Trim(),
            Phone: request.Phone.Trim(),
            Email: string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Address: address,
            City: city,
            District: district,
            Pincode: pincode,
            Payment: request.Payment.Trim().ToLowerInvariant(),
            Notes: string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Items: quote.Items,
            Totals: quote.Totals,
            Coupon: quote.Coupon,
            // A pickup is ready the next working day; a delivery takes two to four.
            DeliveryFrom: AddWorkingDays(placedOn, pickup ? 1 : 2),
            DeliveryTo: AddWorkingDays(placedOn, pickup ? 1 : 4),
            Fulfilment: fulfilment,
            History: [new OrderEvent("pending", now, "Order received")]);

        _orders[order.OrderId] = order;

        // The goods are spoken for the moment the order is accepted. Without
        // this the stock figure only ever moved when somebody edited it by
        // hand, so the shop would happily sell the same box until a person
        // noticed — and "holding stock" on the inventory screen would be a
        // number that meant nothing.
        _catalog.AdjustStock(order.Items, -1);

        _persistence.SaveOrder(order, AllOrders());
        return order;
    }

    public Order? FindOrder(string orderId) => _orders.GetValueOrDefault(orderId);

    public IReadOnlyList<Order> RecentOrders(int limit = 20) => [.. AllOrders().Take(limit)];

    /// <summary>Every order, newest first — the admin list.</summary>
    public IReadOnlyList<Order> AllOrders() => [.. _orders.Values.OrderByDescending(o => o.PlacedAt)];

    /// <summary>
    /// Moves an order to a new status and records when. Returns null when the id
    /// is unknown, so the caller can tell "no such order" from "saved".
    /// </summary>
    /// <summary>
    /// Records where an order stands with the money.
    ///
    /// Separate from <see cref="SetStatus"/> because paying for something and
    /// receiving it are different events: a cash-on-delivery order is completed
    /// long before it is paid, and a prepaid one is paid long before it ships.
    /// The timeline gets a line either way, so the customer can see it.
    /// </summary>
    public Order? SetPaymentStatus(string orderId, string status, string? reference = null)
    {
        if (!_orders.TryGetValue(orderId, out var order)) return null;

        var next = status.Trim().ToLowerInvariant();
        if (!Models.PaymentStatus.IsKnown(next)) return null;

        // Nothing new to say, and a repeated webhook is the normal case rather
        // than the exception — gateways retry until they get a 200.
        if (order.PaymentStatus.Equals(next, StringComparison.OrdinalIgnoreCase)
            && (reference is null || reference == order.PaymentReference))
            return order;

        var history = new List<OrderEvent>(order.Timeline)
        {
            new(order.Status, _clock.GetUtcNow(), next switch
            {
                Models.PaymentStatus.Paid => "Payment received",
                Models.PaymentStatus.Failed => "Payment failed",
                Models.PaymentStatus.Refunded => "Payment refunded",
                Models.PaymentStatus.Processing => "Payment in progress",
                _ => "Payment pending",
            }),
        };

        var updated = order with
        {
            PaymentStatus = next,
            PaymentReference = reference ?? order.PaymentReference,
            History = history,
        };

        _orders[orderId] = updated;
        _persistence.SaveOrder(updated, AllOrders());
        return updated;
    }

    public Order? SetStatus(string orderId, string status, string? note = null)
    {
        if (!_orders.TryGetValue(orderId, out var order)) return null;

        var next = status.Trim().ToLowerInvariant();

        // Re-setting the status an order already holds should not stutter the
        // timeline with a second identical entry.
        if (order.Status.Equals(next, StringComparison.OrdinalIgnoreCase) && note is null)
            return order;

        var history = new List<OrderEvent>(order.Timeline)
        {
            new(next, _clock.GetUtcNow(), note),
        };

        var updated = order with { Status = next, History = history };
        _orders[orderId] = updated;

        // Cancelling returns the goods to the shelf; reopening a cancelled order
        // takes them off again. Any other transition leaves stock alone.
        var held = HoldsStock(order.Status);
        var holdsNow = HoldsStock(next);
        if (held != holdsNow) _catalog.AdjustStock(order.Items, holdsNow ? -1 : 1);

        _persistence.SaveOrder(updated, AllOrders());
        return updated;
    }

    /// <summary>
    /// The customer-facing view of an order, gated on the phone number it was
    /// placed with. Returns null for both "no such order" and "wrong number", so
    /// a caller cannot use the difference to confirm an id exists.
    /// </summary>
    public OrderTracking? Track(string orderId, string phone)
    {
        var order = FindOrder(orderId.Trim());
        if (order is null) return null;

        var supplied = Digits(phone);
        if (supplied.Length == 0 || !Digits(order.Phone).EndsWith(supplied, StringComparison.Ordinal))
            return null;

        return new OrderTracking(
            OrderId: order.OrderId,
            PlacedAt: order.PlacedAt,
            Status: order.Status,
            StatusLabel: Label(order.Status, order.IsPickup),
            Fulfilment: order.Fulfilment,
            Name: order.Name,
            MaskedPhone: Mask(order.Phone),
            Items: order.Items,
            Totals: order.Totals,
            DeliveryFrom: order.DeliveryFrom,
            DeliveryTo: order.DeliveryTo,
            History: order.Timeline,
            Steps: ProgressSteps);
    }

    public static bool IsKnownStatus(string? status) =>
        status is not null && Statuses.Contains(status.Trim().ToLowerInvariant());

    private static string Digits(string value) => new([.. value.Where(char.IsDigit)]);

    /// <summary>98420 11994 → •••••11994. Enough to recognise, not enough to reuse.</summary>
    private static string Mask(string phone)
    {
        var digits = Digits(phone);
        return digits.Length <= 4 ? digits : new string('•', digits.Length - 4) + digits[^4..];
    }

    /* ---------------------------------------------------------------------- */
    /* Enquiries, messages, subscribers                                        */
    /* ---------------------------------------------------------------------- */

    public BulkEnquiry RecordEnquiry(BulkEnquiryRequest request)
    {
        var enquiry = new BulkEnquiry(
            EnquiryId: NextId("BQ", _enquiries.ContainsKey),
            ReceivedAt: _clock.GetUtcNow(),
            Status: "received",
            Name: request.Name.Trim(),
            Organisation: Clean(request.Organisation),
            Phone: request.Phone.Trim(),
            Email: Clean(request.Email),
            District: request.District.Trim(),
            Budget: Clean(request.Budget),
            Quantity: Clean(request.Quantity),
            Message: Clean(request.Message));

        _enquiries[enquiry.EnquiryId] = enquiry;
        _persistence.SaveEnquiry(enquiry);
        return enquiry;
    }

    public BulkEnquiry? FindEnquiry(string id) => _enquiries.GetValueOrDefault(id);

    /// <summary>Every bulk enquiry, newest first — the admin enquiry list.</summary>
    public IReadOnlyList<BulkEnquiry> AllEnquiries() =>
        [.. _enquiries.Values.OrderByDescending(e => e.ReceivedAt)];

    /// <summary>Statuses a bulk enquiry moves through.</summary>
    public static readonly IReadOnlyList<string> EnquiryStatuses =
        ["received", "quoted", "won", "closed"];

    public static bool IsKnownEnquiryStatus(string? status) =>
        status is not null && EnquiryStatuses.Contains(status.Trim().ToLowerInvariant());

    public BulkEnquiry? SetEnquiryStatus(string id, string status)
    {
        if (!_enquiries.TryGetValue(id, out var enquiry)) return null;

        var updated = enquiry with { Status = status.Trim().ToLowerInvariant() };
        _enquiries[id] = updated;
        _persistence.SaveEnquiry(updated);
        return updated;
    }

    public ContactMessage RecordMessage(ContactMessageRequest request)
    {
        var message = new ContactMessage(
            MessageId: NextId("MSG", _messages.ContainsKey),
            ReceivedAt: _clock.GetUtcNow(),
            Name: request.Name.Trim(),
            Phone: request.Phone.Trim(),
            Email: Clean(request.Email),
            Subject: Clean(request.Subject),
            Message: request.Message.Trim());

        _messages[message.MessageId] = message;
        _persistence.SaveMessage(message);
        return message;
    }

    public ContactMessage? FindMessage(string id) => _messages.GetValueOrDefault(id);

    /// <summary>Every contact message, newest first.</summary>
    public IReadOnlyList<ContactMessage> AllMessages() =>
        [.. _messages.Values.OrderByDescending(m => m.ReceivedAt)];

    /// <summary>Idempotent — subscribing twice reports the original timestamp.</summary>
    public Subscription Subscribe(string email)
    {
        var normalised = email.Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();

        var existed = !_subscribers.TryAdd(normalised, now);
        var since = _subscribers.GetValueOrDefault(normalised, now);

        if (!existed) _persistence.SaveSubscriber(normalised, since);

        return new Subscription(normalised, since, existed);
    }

    public bool IsSubscribed(string email) => _subscribers.ContainsKey(email.Trim());

    public int SubscriberCount => _subscribers.Count;

    /// <summary>The whole list, newest first — the admin's newsletter screen.</summary>
    public IReadOnlyList<Subscription> AllSubscribers() =>
    [
        .. _subscribers
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new Subscription(pair.Key, pair.Value, AlreadySubscribed: true))
    ];

    /// <summary>
    /// Takes an address off the list. False when it was never on it, so the
    /// endpoint can answer 404 rather than pretending it removed something.
    /// </summary>
    public bool Unsubscribe(string email)
    {
        var normalised = email.Trim().ToLowerInvariant();
        if (!_subscribers.TryRemove(normalised, out _)) return false;

        _persistence.DeleteSubscriber(normalised);
        return true;
    }

    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Prefix + 8 digits, in the storefront's own <c>AC########</c> shape. The
    /// collision retry matters because two orders placed in the same millisecond
    /// would otherwise overwrite each other.
    /// </summary>
    private string NextId(string prefix, Func<string, bool> exists)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var seed = _clock.GetUtcNow().ToUnixTimeMilliseconds() + attempt;
            var candidate = $"{prefix}{seed % 100_000_000:D8}";
            if (!exists(candidate)) return candidate;
        }

        return $"{prefix}{Random.Shared.Next(0, 100_000_000):D8}";
    }

    /// <summary>Adds working days, skipping Sundays — a port of <c>addWorkingDays</c>.</summary>
    internal static DateOnly AddWorkingDays(DateOnly from, int days)
    {
        var date = from;
        var added = 0;
        while (added < days)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek != DayOfWeek.Sunday) added++;
        }
        return date;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
