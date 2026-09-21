using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The seam between "what the shop knows" and "where it is kept".
///
/// Every store in this API holds its data in memory and writes it somewhere on
/// change. These four interfaces are that somewhere. There are two
/// implementations of each: the JSON files the project started on, and MySQL /
/// MariaDB. Which one is wired up is decided once, in <c>Program.cs</c>, by
/// whether a connection string is configured — and no controller, no store and
/// no test knows the difference.
///
/// One rule shapes all four: <b>reads never touch the backend</b>. The stores
/// load once at startup and serve every request from memory, exactly as they
/// did when this was a folder of JSON. A catalogue of 177 products and a season
/// of orders fit in a few megabytes, so paying a round trip to answer "what is
/// on the shelf" would buy nothing. The backend is written to, not read from.
/// </summary>
public enum CatalogPart
{
    Products,
    Categories,
    Combos,
    Offers,
    Banners,
    Testimonials,
    Faqs,
}

/// <summary>Everything the catalogue is built from, as it came out of storage.</summary>
public sealed record CatalogData(
    IReadOnlyList<Product> Products,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<Combo> Combos,
    IReadOnlyList<Offer> Offers,
    IReadOnlyList<Banner> Banners,
    IReadOnlyList<Testimonial> Testimonials,
    IReadOnlyList<Faq> Faqs)
{
    public static CatalogData Empty { get; } = new([], [], [], [], [], [], []);

    public int Count =>
        Products.Count + Categories.Count + Combos.Count + Offers.Count +
        Banners.Count + Testimonials.Count + Faqs.Count;
}

public interface ICatalogPersistence
{
    /// <summary>A one-word name for the backend, for logs and the status endpoint.</summary>
    string Backend { get; }

    /// <summary>Reads the whole catalogue. Called once, at startup.</summary>
    CatalogData Load();

    /// <summary>
    /// Persists one part of the catalogue from the snapshot that is about to be
    /// published.
    ///
    /// Whole-part rather than per-row because that is what a JSON file can do —
    /// and because the database side is an admin save of at most a couple of
    /// hundred rows, which is cheaper than the round trip that started it.
    /// Crucially it is called <em>before</em> the snapshot is published, so a
    /// failed write leaves memory and storage agreeing on the old value rather
    /// than drifting apart.
    /// </summary>
    void Save(CatalogPart part, CatalogSnapshot snapshot);
}

/// <summary>
/// Orders, enquiries, contact messages and the newsletter list.
///
/// Orders are the one thing here a shopkeeper cannot re-derive, so both
/// backends keep them. The other three differ on purpose, and the difference is
/// documented on <see cref="JsonOrderPersistence"/>: a database is somewhere a
/// shop can be expected to look after personal data, a JSON file sitting in the
/// repository's own <c>src/data</c> is not.
/// </summary>
public interface IOrderPersistence
{
    string Backend { get; }

    IReadOnlyList<Order> LoadOrders();

    /// <summary>
    /// Records one order. <paramref name="all"/> is the whole book as it now
    /// stands — the file backend rewrites it, the database backend upserts the
    /// single row and ignores it. Passing both keeps the caller from having to
    /// know which it is talking to.
    /// </summary>
    void SaveOrder(Order order, IReadOnlyList<Order> all);

    IReadOnlyList<BulkEnquiry> LoadEnquiries();
    void SaveEnquiry(BulkEnquiry enquiry);

    IReadOnlyList<ContactMessage> LoadMessages();
    void SaveMessage(ContactMessage message);

    IReadOnlyDictionary<string, DateTimeOffset> LoadSubscribers();
    void SaveSubscriber(string email, DateTimeOffset since);
    bool DeleteSubscriber(string email);
}

public interface IInventoryPersistence
{
    string Backend { get; }

    IReadOnlyList<StockIntake> Load();

    /// <summary>Appends one ledger entry. <paramref name="all"/> as above.</summary>
    void Append(StockIntake entry, IReadOnlyList<StockIntake> all);
}

public interface IAnalyticsPersistence
{
    string Backend { get; }

    /// <summary>The newest <paramref name="capacity"/> events, oldest first.</summary>
    IReadOnlyList<AnalyticsEvent> Load(int capacity);

    /// <summary>
    /// Flushes the batch that has built up since the last call.
    /// <paramref name="all"/> is the in-memory window the file backend rewrites;
    /// <paramref name="appended"/> is what the database backend inserts.
    /// </summary>
    void Flush(IReadOnlyList<AnalyticsEvent> all, IReadOnlyList<AnalyticsEvent> appended);

    /// <summary>Drops everything past the newest <paramref name="capacity"/> events.</summary>
    void Trim(int capacity);
}
