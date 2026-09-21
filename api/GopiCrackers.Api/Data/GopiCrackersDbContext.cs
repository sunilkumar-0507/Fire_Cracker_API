using System.Text.Json;
using GopiCrackers.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GopiCrackers.Api.Data;

/// <summary>
/// The shop's database.
///
/// One context over thirteen tables: the seven catalogue ones, the order book
/// and its two child tables, the enquiry and contact books, the newsletter
/// list, the stock ledger and the analytics log.
///
/// Two conventions run through the whole model:
///
/// <list type="bullet">
/// <item>
/// Every timestamp is stored as UTC. MySQL has no offset-aware type, so rather
/// than let the provider quietly drop the offset, a converter turns every
/// <see cref="DateTimeOffset"/> into a UTC <see cref="DateTime"/> on the way in
/// and rebuilds it with a zero offset on the way out. Everything in the API
/// already works in UTC, so nothing is lost — but now that is written down in
/// one place rather than assumed in thirty.
/// </item>
/// <item>
/// Ids are <c>varchar</c>, not integers. They are the shop's own references —
/// <c>p-042</c>, <c>AC17263841</c>, <c>flower-pots</c> — printed on packing
/// slips and pasted into WhatsApp. An auto-increment key underneath them would
/// add a second identity for every row and answer no question anybody asks.
/// </item>
/// </list>
/// </summary>
public sealed class GopiCrackersDbContext(DbContextOptions<GopiCrackersDbContext> options)
    : DbContext(options)
{
    public DbSet<ProductRow> Products => Set<ProductRow>();
    public DbSet<CategoryRow> Categories => Set<CategoryRow>();
    public DbSet<ComboRow> Combos => Set<ComboRow>();
    public DbSet<OfferRow> Offers => Set<OfferRow>();
    public DbSet<BannerRow> Banners => Set<BannerRow>();
    public DbSet<TestimonialRow> Testimonials => Set<TestimonialRow>();
    public DbSet<FaqRow> Faqs => Set<FaqRow>();

    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<OrderItemRow> OrderItems => Set<OrderItemRow>();
    public DbSet<OrderEventRow> OrderEvents => Set<OrderEventRow>();

    public DbSet<EnquiryRow> Enquiries => Set<EnquiryRow>();
    public DbSet<ContactMessageRow> ContactMessages => Set<ContactMessageRow>();
    public DbSet<SubscriberRow> Subscribers => Set<SubscriberRow>();
    public DbSet<StockIntakeRow> StockIntake => Set<StockIntakeRow>();
    public DbSet<AnalyticsEventRow> AnalyticsEvents => Set<AnalyticsEventRow>();

    /// <summary>
    /// The same options the API serialises responses with, so a JSON column
    /// holds <c>₹</c> and Tamil category names as themselves rather than as
    /// <c>\uXXXX</c> escapes — readable to anyone who opens the table.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override void OnModelCreating(ModelBuilder model)
    {
        /* ------------------------------------------------------------------ */
        /* Catalogue                                                           */
        /* ------------------------------------------------------------------ */

        model.Entity<ProductRow>(e =>
        {
            e.ToTable("products");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(64);
            e.Property(p => p.Code).HasMaxLength(32);
            e.Property(p => p.Slug).HasMaxLength(180);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Category).HasMaxLength(120);
            e.Property(p => p.Brand).HasMaxLength(120);
            e.Property(p => p.Unit).HasMaxLength(80);
            e.Property(p => p.Description).HasColumnType("text");

            // A slug is a URL. Two products sharing one is a page that cannot be
            // addressed, so the database refuses it rather than letting the
            // snapshot quietly drop the loser (see CatalogSnapshot.FirstWins).
            e.HasIndex(p => p.Slug).IsUnique();
            e.HasIndex(p => p.Category);
            e.HasIndex(p => p.Active);

            e.Property(p => p.Highlights).HasJsonConversion(Json);
            e.Property(p => p.Images).HasJsonConversion(Json);
            e.Property(p => p.Tags).HasJsonConversion(Json);
            e.Property(p => p.Specs).HasJsonConversion(Json);
        });

        model.Entity<CategoryRow>(e =>
        {
            e.ToTable("categories");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(64);
            e.Property(c => c.Slug).HasMaxLength(120);
            e.Property(c => c.Name).HasMaxLength(160);
            e.Property(c => c.TamilName).HasMaxLength(160);
            e.Property(c => c.Tagline).HasMaxLength(240);
            e.Property(c => c.Description).HasColumnType("text");
            e.Property(c => c.Art).HasMaxLength(80);
            e.Property(c => c.Accent).HasMaxLength(32);
            e.Property(c => c.AccentSoft).HasMaxLength(32);
            e.Property(c => c.NoiseLevel).HasMaxLength(80);
            e.Property(c => c.Tone).HasMaxLength(48);
            e.HasIndex(c => c.Slug).IsUnique();
        });

        model.Entity<ComboRow>(e =>
        {
            e.ToTable("combos");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(64);
            e.Property(c => c.Slug).HasMaxLength(180);
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Tagline).HasMaxLength(240);
            e.Property(c => c.Description).HasColumnType("text");
            e.Property(c => c.Art).HasMaxLength(80);
            e.Property(c => c.Accent).HasMaxLength(32);
            e.Property(c => c.Serves).HasMaxLength(120);
            e.Property(c => c.Duration).HasMaxLength(120);
            e.Property(c => c.Badge).HasMaxLength(120);
            e.Property(c => c.Tone).HasMaxLength(48);
            e.HasIndex(c => c.Slug).IsUnique();
            e.Property(c => c.Includes).HasJsonConversion(Json);
        });

        model.Entity<OfferRow>(e =>
        {
            e.ToTable("offers");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasMaxLength(64);
            e.Property(o => o.Code).HasMaxLength(48);
            e.Property(o => o.Title).HasMaxLength(200);
            e.Property(o => o.Subtitle).HasMaxLength(240);
            e.Property(o => o.Description).HasColumnType("text");
            e.Property(o => o.Type).HasMaxLength(32);
            e.Property(o => o.Art).HasMaxLength(80);
            e.Property(o => o.Accent).HasMaxLength(32);
            e.Property(o => o.AccentTo).HasMaxLength(32);
            e.Property(o => o.Badge).HasMaxLength(120);
            e.Property(o => o.Tone).HasMaxLength(48);
            e.HasIndex(o => o.Code).IsUnique();
            e.Property(o => o.Terms).HasJsonConversion(Json);
        });

        model.Entity<BannerRow>(e =>
        {
            e.ToTable("banners");
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasMaxLength(64);
            e.Property(b => b.Placement).HasMaxLength(64);
            e.Property(b => b.Title).HasMaxLength(240);
            e.Property(b => b.Eyebrow).HasMaxLength(160);
            e.Property(b => b.TitleAccent).HasMaxLength(160);
            e.Property(b => b.Subtitle).HasMaxLength(400);
            e.Property(b => b.CtaPrimaryLabel).HasMaxLength(120);
            e.Property(b => b.CtaPrimaryTo).HasMaxLength(240);
            e.Property(b => b.CtaSecondaryLabel).HasMaxLength(120);
            e.Property(b => b.CtaSecondaryTo).HasMaxLength(240);
            e.Property(b => b.Art).HasMaxLength(80);
            e.Property(b => b.Accent).HasMaxLength(32);
            e.Property(b => b.AccentTo).HasMaxLength(32);
            e.HasIndex(b => b.Placement);
        });

        model.Entity<TestimonialRow>(e =>
        {
            e.ToTable("testimonials");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(64);
            e.Property(t => t.Name).HasMaxLength(160);
            e.Property(t => t.Role).HasMaxLength(160);
            e.Property(t => t.Location).HasMaxLength(160);
            e.Property(t => t.Initials).HasMaxLength(8);
            e.Property(t => t.Accent).HasMaxLength(32);
            e.Property(t => t.Quote).HasColumnType("text");
        });

        model.Entity<FaqRow>(e =>
        {
            e.ToTable("faqs");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasMaxLength(64);
            e.Property(f => f.Category).HasMaxLength(120);
            e.Property(f => f.Question).HasMaxLength(400);
            e.Property(f => f.Answer).HasColumnType("text");
            e.HasIndex(f => f.Category);
        });

        /* ------------------------------------------------------------------ */
        /* Orders                                                              */
        /* ------------------------------------------------------------------ */

        model.Entity<OrderRow>(e =>
        {
            e.ToTable("orders");
            e.HasKey(o => o.OrderId);
            e.Property(o => o.OrderId).HasMaxLength(32);
            e.Property(o => o.Status).HasMaxLength(32);
            e.Property(o => o.Name).HasMaxLength(120);
            e.Property(o => o.Phone).HasMaxLength(24);
            e.Property(o => o.Email).HasMaxLength(200);
            e.Property(o => o.Address).HasMaxLength(400);
            e.Property(o => o.City).HasMaxLength(120);
            e.Property(o => o.District).HasMaxLength(120);
            e.Property(o => o.Pincode).HasMaxLength(12);
            e.Property(o => o.Payment).HasMaxLength(32);
            e.Property(o => o.PaymentStatus).HasMaxLength(32);
            e.Property(o => o.PaymentReference).HasMaxLength(120);
            e.Property(o => o.Notes).HasColumnType("text");
            e.Property(o => o.Fulfilment).HasMaxLength(16);
            e.Property(o => o.CouponCode).HasMaxLength(48);
            e.Property(o => o.CouponType).HasMaxLength(32);
            e.Property(o => o.CouponNote).HasMaxLength(240);

            // The order list is always "newest first", and the admin filters it
            // by status. Both are the whole index.
            e.HasIndex(o => o.PlacedAt);
            e.HasIndex(o => o.Status);
            e.HasIndex(o => o.Phone);

            // An order without its lines is not an order, so the lines go when
            // it goes. Nothing deletes orders today — but a cascade written now
            // is a cascade nobody has to remember later.
            e.HasMany(o => o.Items)
                .WithOne(i => i.Order!)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(o => o.History)
                .WithOne(h => h.Order!)
                .HasForeignKey(h => h.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<OrderItemRow>(e =>
        {
            e.ToTable("order_items");
            e.HasKey(i => i.Key);
            e.Property(i => i.OrderId).HasMaxLength(32);
            e.Property(i => i.ItemId).HasMaxLength(64);
            e.Property(i => i.Kind).HasMaxLength(16);
            e.Property(i => i.Slug).HasMaxLength(180);
            e.Property(i => i.Name).HasMaxLength(200);
            e.Property(i => i.Unit).HasMaxLength(80);
            e.Property(i => i.Image).HasMaxLength(400);
            e.Property(i => i.Category).HasMaxLength(120);
            e.Property(i => i.Availability).HasMaxLength(24);
            e.Property(i => i.Tags).HasJsonConversion(Json);

            // "What sold, and for how much" groups by this column.
            e.HasIndex(i => i.ItemId);
            e.HasIndex(i => new { i.OrderId, i.LineNo });
        });

        model.Entity<OrderEventRow>(e =>
        {
            e.ToTable("order_events");
            e.HasKey(h => h.Key);
            e.Property(h => h.OrderId).HasMaxLength(32);
            e.Property(h => h.Status).HasMaxLength(32);
            e.Property(h => h.Note).HasMaxLength(400);
            e.HasIndex(h => new { h.OrderId, h.Seq });
        });

        /* ------------------------------------------------------------------ */
        /* Enquiries, messages, subscribers                                    */
        /* ------------------------------------------------------------------ */

        model.Entity<EnquiryRow>(e =>
        {
            e.ToTable("bulk_enquiries");
            e.HasKey(x => x.EnquiryId);
            e.Property(x => x.EnquiryId).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Organisation).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(24);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.District).HasMaxLength(120);
            e.Property(x => x.Budget).HasMaxLength(120);
            e.Property(x => x.Quantity).HasMaxLength(120);
            e.Property(x => x.Message).HasColumnType("text");
            e.HasIndex(x => x.ReceivedAt);
            e.HasIndex(x => x.Status);
        });

        model.Entity<ContactMessageRow>(e =>
        {
            e.ToTable("contact_messages");
            e.HasKey(x => x.MessageId);
            e.Property(x => x.MessageId).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Phone).HasMaxLength(24);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(240);
            e.Property(x => x.Message).HasColumnType("text");
            e.HasIndex(x => x.ReceivedAt);
        });

        model.Entity<SubscriberRow>(e =>
        {
            e.ToTable("newsletter_subscribers");
            e.HasKey(x => x.Email);
            // Long enough for a real address, short enough to be a MySQL key:
            // utf8mb4 costs four bytes a character and the 3072-byte index
            // limit lands at 768.
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasIndex(x => x.SubscribedAt);
        });

        /* ------------------------------------------------------------------ */
        /* Stock and analytics                                                 */
        /* ------------------------------------------------------------------ */

        model.Entity<StockIntakeRow>(e =>
        {
            e.ToTable("stock_intake");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.ProductId).HasMaxLength(64);
            e.Property(x => x.Supplier).HasMaxLength(160);
            e.Property(x => x.Note).HasMaxLength(400);
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.ReceivedAt);
        });

        model.Entity<AnalyticsEventRow>(e =>
        {
            e.ToTable("analytics_events");
            e.HasKey(x => x.Key);
            e.Property(x => x.Type).HasMaxLength(48);
            e.Property(x => x.Ref).HasMaxLength(200);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.Session).HasMaxLength(64);

            // Every report is "events of this type in this window", and the
            // trim is "oldest first" — one composite index serves both.
            e.HasIndex(x => new { x.At, x.Type });
            e.HasIndex(x => x.Session);
        });

        ApplyUtcTimestamps(model);
        base.OnModelCreating(model);
    }

    /// <summary>
    /// MySQL has no offset-aware timestamp. Rather than let that be discovered
    /// later by a report that is five and a half hours out, every
    /// <see cref="DateTimeOffset"/> in the model is converted explicitly: UTC on
    /// the way in, a zero offset on the way out.
    /// </summary>
    private static void ApplyUtcTimestamps(ModelBuilder model)
    {
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) ||
                    property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(converter);
                    // Microseconds, so two events in the same second still sort.
                    property.SetColumnType("datetime(6)");
                }
            }
        }
    }
}

/// <summary>
/// Stores a collection in one column as JSON.
///
/// The comparer is the part worth knowing about: without it EF compares
/// collection properties by reference, so mutating a list in place looks like
/// no change at all and the save silently does nothing. Everything here is
/// written by replacing the whole row, but a comparer that only works because
/// of how the caller happens to behave is a trap for the next caller.
/// </summary>
internal static class JsonColumn
{
    public static PropertyBuilder<List<T>> HasJsonConversion<T>(
        this PropertyBuilder<List<T>> property,
        JsonSerializerOptions options) =>
        property
            .HasConversion(
                value => JsonSerializer.Serialize(value, options),
                text => JsonSerializer.Deserialize<List<T>>(text, options) ?? new List<T>(),
                new ValueComparer<List<T>>(
                    (a, b) => JsonSerializer.Serialize(a, options) == JsonSerializer.Serialize(b, options),
                    v => JsonSerializer.Serialize(v, options).GetHashCode(),
                    v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, options), options)!))
            .HasColumnType("text");

    public static PropertyBuilder<Dictionary<string, string>> HasJsonConversion(
        this PropertyBuilder<Dictionary<string, string>> property,
        JsonSerializerOptions options) =>
        property
            .HasConversion(
                value => JsonSerializer.Serialize(value, options),
                text => JsonSerializer.Deserialize<Dictionary<string, string>>(text, options)
                        ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => JsonSerializer.Serialize(a, options) == JsonSerializer.Serialize(b, options),
                    v => JsonSerializer.Serialize(v, options).GetHashCode(),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(
                        JsonSerializer.Serialize(v, options), options)!))
            .HasColumnType("text");
}
