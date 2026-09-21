using GopiCrackers.Api.Data;
using GopiCrackers.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// Orders, enquiries, contact messages and the newsletter list, in MySQL /
/// MariaDB.
///
/// Unlike the catalogue this does not sync whole tables. The order book grows
/// all season and only ever changes one row at a time — an order is placed,
/// then confirmed, then packed — so each write is an upsert of exactly the row
/// that moved.
///
/// Enquiries, messages and subscribers <em>are</em> kept here, where the file
/// backend deliberately drops them. The objection was never to storing them; it
/// was to a plain-text file of names and phone numbers living inside the
/// repository the storefront is built from. A database the shop already runs,
/// backs up and controls access to is where that record belongs.
/// </summary>
public sealed class MySqlOrderPersistence(
    IDbContextFactory<GopiCrackersDbContext> factory,
    ILogger<MySqlOrderPersistence> logger) : IOrderPersistence
{
    public string Backend => "mysql";

    /* ---------------------------------------------------------------------- */
    /* Orders                                                                  */
    /* ---------------------------------------------------------------------- */

    public IReadOnlyList<Order> LoadOrders()
    {
        using var db = factory.CreateDbContext();

        var orders = db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.History)
            .OrderByDescending(o => o.PlacedAt)
            .AsSplitQuery()
            .AsEnumerable()
            .Select(Mapping.ToModel)
            .ToList();

        logger.LogInformation("Restored {Count} orders from the database", orders.Count);
        return orders;
    }

    public void SaveOrder(Order order, IReadOnlyList<Order> all)
    {
        using var db = factory.CreateDbContext();

        var row = db.Orders
            .Include(o => o.History)
            .FirstOrDefault(o => o.OrderId == order.OrderId);

        if (row is null)
        {
            db.Orders.Add(order.ToRow());
        }
        else
        {
            row.Fill(order);
            AppendNewSteps(row, order.Timeline);
        }

        db.SaveChanges();
    }

    /// <summary>
    /// The timeline only ever grows, so a status change appends one row rather
    /// than rewriting the whole history. Loading the lines is skipped entirely
    /// on an update: an order's basket is fixed at checkout, and reading a
    /// dozen rows to leave them untouched is a query nobody needed.
    /// </summary>
    private static void AppendNewSteps(OrderRow row, IReadOnlyList<OrderEvent> timeline)
    {
        for (var seq = row.History.Count; seq < timeline.Count; seq++)
            row.History.Add(timeline[seq].ToRow(row.OrderId, seq));
    }

    /* ---------------------------------------------------------------------- */
    /* Enquiries and messages                                                  */
    /* ---------------------------------------------------------------------- */

    public IReadOnlyList<BulkEnquiry> LoadEnquiries()
    {
        using var db = factory.CreateDbContext();
        return [.. db.Enquiries.AsNoTracking()
            .OrderByDescending(e => e.ReceivedAt)
            .AsEnumerable()
            .Select(Mapping.ToModel)];
    }

    public void SaveEnquiry(BulkEnquiry enquiry)
    {
        using var db = factory.CreateDbContext();

        var row = db.Enquiries.FirstOrDefault(e => e.EnquiryId == enquiry.EnquiryId);
        if (row is null) db.Enquiries.Add(new EnquiryRow().Fill(enquiry));
        else row.Fill(enquiry);

        db.SaveChanges();
    }

    public IReadOnlyList<ContactMessage> LoadMessages()
    {
        using var db = factory.CreateDbContext();
        return [.. db.ContactMessages.AsNoTracking()
            .OrderByDescending(m => m.ReceivedAt)
            .AsEnumerable()
            .Select(Mapping.ToModel)];
    }

    public void SaveMessage(ContactMessage message)
    {
        using var db = factory.CreateDbContext();

        var row = db.ContactMessages.FirstOrDefault(m => m.MessageId == message.MessageId);
        if (row is null) db.ContactMessages.Add(new ContactMessageRow().Fill(message));
        else row.Fill(message);

        db.SaveChanges();
    }

    /* ---------------------------------------------------------------------- */
    /* Newsletter                                                              */
    /* ---------------------------------------------------------------------- */

    public IReadOnlyDictionary<string, DateTimeOffset> LoadSubscribers()
    {
        using var db = factory.CreateDbContext();

        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in db.Subscribers.AsNoTracking()) map[row.Email] = row.SubscribedAt;
        return map;
    }

    /// <summary>
    /// Idempotent, like the endpoint in front of it: an address already on the
    /// list keeps the date it first joined rather than being bumped to today.
    /// </summary>
    public void SaveSubscriber(string email, DateTimeOffset since)
    {
        using var db = factory.CreateDbContext();

        if (db.Subscribers.Any(s => s.Email == email)) return;

        db.Subscribers.Add(new SubscriberRow { Email = email, SubscribedAt = since });
        db.SaveChanges();
    }

    public bool DeleteSubscriber(string email)
    {
        using var db = factory.CreateDbContext();

        var row = db.Subscribers.FirstOrDefault(s => s.Email == email);
        if (row is null) return false;

        db.Subscribers.Remove(row);
        db.SaveChanges();
        return true;
    }
}
