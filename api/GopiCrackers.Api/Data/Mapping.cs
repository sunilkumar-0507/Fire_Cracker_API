using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Data;

/// <summary>
/// The one seam between the storage shape and the wire shape.
///
/// Every conversion is written out by hand rather than reflected or
/// auto-mapped. It is more lines, and it is worth them: when a field is added
/// to <see cref="Product"/> the compiler stops here and says so, instead of the
/// field silently never reaching the table and being noticed in November.
/// </summary>
internal static class Mapping
{
    /* ---------------------------------------------------------------------- */
    /* Catalogue                                                               */
    /* ---------------------------------------------------------------------- */

    public static Product ToModel(this ProductRow row) => new(
        Id: row.Id,
        Code: row.Code,
        Slug: row.Slug,
        Name: row.Name,
        Category: row.Category,
        Brand: row.Brand,
        Price: row.Price,
        Mrp: row.Mrp,
        Discount: row.Discount,
        Unit: row.Unit,
        Description: row.Description,
        Highlights: row.Highlights,
        Images: row.Images,
        Stock: row.Stock,
        Tags: row.Tags,
        Specs: row.Specs,
        Featured: row.Featured,
        BestSeller: row.BestSeller,
        Combo: row.Combo,
        IsNew: row.IsNew,
        Active: row.Active);

    /// <summary>Fills an existing row, so an update is an UPDATE and not a delete plus insert.</summary>
    public static ProductRow Fill(this ProductRow row, Product model)
    {
        row.Id = model.Id;
        row.Code = model.Code;
        row.Slug = model.Slug;
        row.Name = model.Name;
        row.Category = model.Category;
        row.Brand = model.Brand;
        row.Price = model.Price;
        row.Mrp = model.Mrp;
        row.Discount = model.Discount;
        row.Unit = model.Unit;
        row.Description = model.Description;
        row.Highlights = [.. model.Highlights];
        row.Images = [.. model.Images];
        row.Stock = model.Stock;
        row.Tags = [.. model.Tags];
        row.Specs = new Dictionary<string, string>(model.Specs);
        row.Featured = model.Featured;
        row.BestSeller = model.BestSeller;
        row.Combo = model.Combo;
        row.IsNew = model.IsNew;
        row.Active = model.Active;
        return row;
    }

    public static Category ToModel(this CategoryRow row) => new(
        Id: row.Id,
        Slug: row.Slug,
        Name: row.Name,
        TamilName: row.TamilName,
        Tagline: row.Tagline,
        Description: row.Description,
        Art: row.Art,
        Accent: row.Accent,
        AccentSoft: row.AccentSoft,
        ProductCount: row.ProductCount,
        NoiseLevel: row.NoiseLevel,
        Featured: row.Featured,
        Tone: row.Tone);

    public static CategoryRow Fill(this CategoryRow row, Category model)
    {
        row.Id = model.Id;
        row.Slug = model.Slug;
        row.Name = model.Name;
        row.TamilName = model.TamilName;
        row.Tagline = model.Tagline;
        row.Description = model.Description;
        row.Art = model.Art;
        row.Accent = model.Accent;
        row.AccentSoft = model.AccentSoft;
        row.ProductCount = model.ProductCount;
        row.NoiseLevel = model.NoiseLevel;
        row.Featured = model.Featured;
        row.Tone = model.Tone;
        return row;
    }

    public static Combo ToModel(this ComboRow row) => new(
        Id: row.Id,
        Slug: row.Slug,
        Name: row.Name,
        Tagline: row.Tagline,
        Description: row.Description,
        Art: row.Art,
        Accent: row.Accent,
        Price: row.Price,
        Mrp: row.Mrp,
        Discount: row.Discount,
        Saves: row.Saves,
        ItemCount: row.ItemCount,
        Serves: row.Serves,
        Duration: row.Duration,
        Badge: row.Badge,
        Stock: row.Stock,
        Featured: row.Featured,
        Includes: row.Includes,
        Tone: row.Tone);

    public static ComboRow Fill(this ComboRow row, Combo model)
    {
        row.Id = model.Id;
        row.Slug = model.Slug;
        row.Name = model.Name;
        row.Tagline = model.Tagline;
        row.Description = model.Description;
        row.Art = model.Art;
        row.Accent = model.Accent;
        row.Price = model.Price;
        row.Mrp = model.Mrp;
        row.Discount = model.Discount;
        row.Saves = model.Saves;
        row.ItemCount = model.ItemCount;
        row.Serves = model.Serves;
        row.Duration = model.Duration;
        row.Badge = model.Badge;
        row.Stock = model.Stock;
        row.Featured = model.Featured;
        row.Includes = [.. model.Includes];
        row.Tone = model.Tone;
        return row;
    }

    public static Offer ToModel(this OfferRow row) => new(
        Id: row.Id,
        Code: row.Code,
        Title: row.Title,
        Subtitle: row.Subtitle,
        Description: row.Description,
        Type: row.Type,
        Value: row.Value,
        MinOrder: row.MinOrder,
        Art: row.Art,
        Accent: row.Accent,
        AccentTo: row.AccentTo,
        EndsAt: row.EndsAt,
        FallbackHours: row.FallbackHours,
        Badge: row.Badge,
        Featured: row.Featured,
        Terms: row.Terms,
        Tone: row.Tone);

    public static OfferRow Fill(this OfferRow row, Offer model)
    {
        row.Id = model.Id;
        row.Code = model.Code;
        row.Title = model.Title;
        row.Subtitle = model.Subtitle;
        row.Description = model.Description;
        row.Type = model.Type;
        row.Value = model.Value;
        row.MinOrder = model.MinOrder;
        row.Art = model.Art;
        row.Accent = model.Accent;
        row.AccentTo = model.AccentTo;
        row.EndsAt = model.EndsAt;
        row.FallbackHours = model.FallbackHours;
        row.Badge = model.Badge;
        row.Featured = model.Featured;
        row.Terms = [.. model.Terms];
        row.Tone = model.Tone;
        return row;
    }

    public static Banner ToModel(this BannerRow row) => new(
        Id: row.Id,
        Placement: row.Placement,
        Title: row.Title,
        Eyebrow: row.Eyebrow,
        TitleAccent: row.TitleAccent,
        Subtitle: row.Subtitle,
        CtaPrimary: row.CtaPrimaryLabel is null || row.CtaPrimaryTo is null
            ? null
            : new BannerCta(row.CtaPrimaryLabel, row.CtaPrimaryTo),
        CtaSecondary: row.CtaSecondaryLabel is null || row.CtaSecondaryTo is null
            ? null
            : new BannerCta(row.CtaSecondaryLabel, row.CtaSecondaryTo),
        Art: row.Art,
        Accent: row.Accent,
        AccentTo: row.AccentTo);

    public static BannerRow Fill(this BannerRow row, Banner model)
    {
        row.Id = model.Id;
        row.Placement = model.Placement;
        row.Title = model.Title;
        row.Eyebrow = model.Eyebrow;
        row.TitleAccent = model.TitleAccent;
        row.Subtitle = model.Subtitle;
        row.CtaPrimaryLabel = model.CtaPrimary?.Label;
        row.CtaPrimaryTo = model.CtaPrimary?.To;
        row.CtaSecondaryLabel = model.CtaSecondary?.Label;
        row.CtaSecondaryTo = model.CtaSecondary?.To;
        row.Art = model.Art;
        row.Accent = model.Accent;
        row.AccentTo = model.AccentTo;
        return row;
    }

    public static Testimonial ToModel(this TestimonialRow row) => new(
        Id: row.Id,
        Name: row.Name,
        Role: row.Role,
        Location: row.Location,
        Initials: row.Initials,
        Accent: row.Accent,
        Rating: row.Rating,
        Quote: row.Quote);

    public static TestimonialRow Fill(this TestimonialRow row, Testimonial model)
    {
        row.Id = model.Id;
        row.Name = model.Name;
        row.Role = model.Role;
        row.Location = model.Location;
        row.Initials = model.Initials;
        row.Accent = model.Accent;
        row.Rating = model.Rating;
        row.Quote = model.Quote;
        return row;
    }

    public static Faq ToModel(this FaqRow row) => new(
        Id: row.Id,
        Category: row.Category,
        Question: row.Question,
        Answer: row.Answer);

    public static FaqRow Fill(this FaqRow row, Faq model)
    {
        row.Id = model.Id;
        row.Category = model.Category;
        row.Question = model.Question;
        row.Answer = model.Answer;
        return row;
    }

    /* ---------------------------------------------------------------------- */
    /* Orders                                                                  */
    /* ---------------------------------------------------------------------- */

    public static Order ToModel(this OrderRow row) => new(
        OrderId: row.OrderId,
        PlacedAt: row.PlacedAt,
        Status: row.Status,
        Name: row.Name,
        Phone: row.Phone,
        Email: row.Email,
        Address: row.Address,
        City: row.City,
        District: row.District,
        Pincode: row.Pincode,
        Payment: row.Payment,
        Notes: row.Notes,
        Items: [.. row.Items.OrderBy(i => i.LineNo).Select(ToModel)],
        Totals: new CartTotals(
            Subtotal: row.Subtotal,
            MrpTotal: row.MrpTotal,
            CatalogueSavings: row.CatalogueSavings,
            CouponDiscount: row.CouponDiscount,
            Shipping: row.Shipping,
            Total: row.Total,
            TotalSavings: row.TotalSavings,
            FreeShippingGap: row.FreeShippingGap,
            Count: row.ItemCount),
        Coupon: row.CouponCode is null
            ? null
            : new AppliedCoupon(
                Code: row.CouponCode,
                Type: row.CouponType ?? "percentage",
                Value: row.CouponValue ?? 0,
                MinOrder: row.CouponMinOrder ?? 0,
                Note: row.CouponNote ?? string.Empty),
        DeliveryFrom: row.DeliveryFrom,
        DeliveryTo: row.DeliveryTo,
        Fulfilment: row.Fulfilment,
        History: [.. row.History.OrderBy(h => h.Seq).Select(h => new OrderEvent(h.Status, h.At, h.Note))],
        PaymentStatus: row.PaymentStatus,
        PaymentReference: row.PaymentReference);

    /// <summary>
    /// Fills an order's own columns, leaving its lines and timeline alone.
    ///
    /// Scalars-only because that is exactly what changes after checkout: an
    /// order is confirmed, packed, paid, cancelled. Rewriting the basket on
    /// every one of those would delete and re-insert a dozen rows to record a
    /// single word. <see cref="ToRow(Order)"/> builds the children, once, when
    /// the order is first written.
    /// </summary>
    public static OrderRow Fill(this OrderRow row, Order model)
    {
        row.OrderId = model.OrderId;
        row.PlacedAt = model.PlacedAt;
        row.Status = model.Status;
        row.Name = model.Name;
        row.Phone = model.Phone;
        row.Email = model.Email;
        row.Address = model.Address;
        row.City = model.City;
        row.District = model.District;
        row.Pincode = model.Pincode;
        row.Payment = model.Payment;
        row.PaymentStatus = model.PaymentStatus;
        row.PaymentReference = model.PaymentReference;
        row.Notes = model.Notes;

        row.Subtotal = model.Totals.Subtotal;
        row.MrpTotal = model.Totals.MrpTotal;
        row.CatalogueSavings = model.Totals.CatalogueSavings;
        row.CouponDiscount = model.Totals.CouponDiscount;
        row.Shipping = model.Totals.Shipping;
        row.Total = model.Totals.Total;
        row.TotalSavings = model.Totals.TotalSavings;
        row.FreeShippingGap = model.Totals.FreeShippingGap;
        row.ItemCount = model.Totals.Count;

        row.CouponCode = model.Coupon?.Code;
        row.CouponType = model.Coupon?.Type;
        row.CouponValue = model.Coupon?.Value;
        row.CouponMinOrder = model.Coupon?.MinOrder;
        row.CouponNote = model.Coupon?.Note;

        row.DeliveryFrom = model.DeliveryFrom;
        row.DeliveryTo = model.DeliveryTo;
        row.Fulfilment = model.Fulfilment;

        return row;
    }

    /// <summary>A whole new order row, lines and timeline included.</summary>
    public static OrderRow ToRow(this Order model)
    {
        var row = new OrderRow().Fill(model);

        for (var i = 0; i < model.Items.Count; i++)
            row.Items.Add(model.Items[i].ToRow(model.OrderId, i));

        var timeline = model.Timeline;
        for (var i = 0; i < timeline.Count; i++)
            row.History.Add(timeline[i].ToRow(model.OrderId, i));

        return row;
    }

    public static CartLine ToModel(this OrderItemRow row) => new(
        Id: row.ItemId,
        Kind: row.Kind,
        Slug: row.Slug,
        Name: row.Name,
        Unit: row.Unit,
        Price: row.Price,
        Mrp: row.Mrp,
        Image: row.Image,
        Category: row.Category,
        Stock: row.Stock,
        Tags: row.Tags,
        Qty: row.Qty,
        LineTotal: row.LineTotal,
        Capped: row.Capped,
        Availability: row.Availability);

    public static OrderItemRow ToRow(this CartLine line, string orderId, int lineNo) => new()
    {
        OrderId = orderId,
        LineNo = lineNo,
        ItemId = line.Id,
        Kind = line.Kind,
        Slug = line.Slug,
        Name = line.Name,
        Unit = line.Unit,
        Price = line.Price,
        Mrp = line.Mrp,
        Image = line.Image,
        Category = line.Category,
        Stock = line.Stock,
        Tags = [.. line.Tags],
        Qty = line.Qty,
        LineTotal = line.LineTotal,
        Capped = line.Capped,
        Availability = line.Availability,
    };

    public static OrderEventRow ToRow(this OrderEvent step, string orderId, int seq) => new()
    {
        OrderId = orderId,
        Seq = seq,
        Status = step.Status,
        At = step.At,
        Note = step.Note,
    };

    /* ---------------------------------------------------------------------- */
    /* Enquiries, messages, stock, analytics                                   */
    /* ---------------------------------------------------------------------- */

    public static BulkEnquiry ToModel(this EnquiryRow row) => new(
        EnquiryId: row.EnquiryId,
        ReceivedAt: row.ReceivedAt,
        Status: row.Status,
        Name: row.Name,
        Organisation: row.Organisation,
        Phone: row.Phone,
        Email: row.Email,
        District: row.District,
        Budget: row.Budget,
        Quantity: row.Quantity,
        Message: row.Message);

    public static EnquiryRow Fill(this EnquiryRow row, BulkEnquiry model)
    {
        row.EnquiryId = model.EnquiryId;
        row.ReceivedAt = model.ReceivedAt;
        row.Status = model.Status;
        row.Name = model.Name;
        row.Organisation = model.Organisation;
        row.Phone = model.Phone;
        row.Email = model.Email;
        row.District = model.District;
        row.Budget = model.Budget;
        row.Quantity = model.Quantity;
        row.Message = model.Message;
        return row;
    }

    public static ContactMessage ToModel(this ContactMessageRow row) => new(
        MessageId: row.MessageId,
        ReceivedAt: row.ReceivedAt,
        Name: row.Name,
        Phone: row.Phone,
        Email: row.Email,
        Subject: row.Subject,
        Message: row.Message);

    public static ContactMessageRow Fill(this ContactMessageRow row, ContactMessage model)
    {
        row.MessageId = model.MessageId;
        row.ReceivedAt = model.ReceivedAt;
        row.Name = model.Name;
        row.Phone = model.Phone;
        row.Email = model.Email;
        row.Subject = model.Subject;
        row.Message = model.Message;
        return row;
    }

    public static StockIntake ToModel(this StockIntakeRow row) => new(
        Id: row.Id,
        ProductId: row.ProductId,
        Quantity: row.Quantity,
        ReceivedAt: row.ReceivedAt,
        Supplier: row.Supplier,
        Note: row.Note);

    public static StockIntakeRow ToRow(this StockIntake model) => new()
    {
        Id = model.Id,
        ProductId = model.ProductId,
        Quantity = model.Quantity,
        ReceivedAt = model.ReceivedAt,
        Supplier = model.Supplier,
        Note = model.Note,
    };

    public static AnalyticsEvent ToModel(this AnalyticsEventRow row) => new(
        Type: row.Type,
        At: row.At,
        Ref: row.Ref,
        Label: row.Label,
        Value: row.Value,
        Session: row.Session);

    public static AnalyticsEventRow ToRow(this AnalyticsEvent model) => new()
    {
        Type = model.Type,
        At = model.At,
        Ref = model.Ref,
        Label = model.Label,
        Value = model.Value,
        Session = model.Session,
    };
}
