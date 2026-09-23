namespace GopiCrackers.Api.Options;

/// <summary>
/// The non-catalogue constants the storefront ships in <c>src/constants/index.js</c>.
/// Defaults below match that file; anything can be overridden from the
/// <c>Storefront</c> configuration section without a rebuild.
/// </summary>
public sealed class StorefrontOptions
{
    public const string SectionName = "Storefront";

    public BrandOptions Brand { get; set; } = new();
    public ShippingOptions Shipping { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public NotificationOptions Notifications { get; set; } = new();
    public PickupOptions Pickup { get; set; } = new();

    /// <summary>Coupon codes the checkout accepts. Mirrors <c>offers.json</c>.</summary>
    public Dictionary<string, CouponRule> Coupons { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DIWALI75"] = new("percentage", 0, 0, "Already applied to every price"),
        ["EARLYBIRD"] = new("percentage", 10, 1500, "10% off before the rush"),
        ["COMBO500"] = new("flat", 500, 1899, "₹500 off combo packs"),
        ["SILENT15"] = new("percentage", 15, 999, "15% off the silent range"),
        ["BULK20"] = new("percentage", 20, 25000, "20% off bulk orders"),
    };

    public List<PaymentMethod> PaymentMethods { get; set; } =
    [
        new("upi", "UPI", "GPay, PhonePe, Paytm, BHIM"),
        new("card", "Card", "Credit or debit, all major banks"),
        new("netbanking", "Net banking", "58 banks supported"),
        new("cod", "Cash on delivery", "Available up to ₹5,000"),
    ];

    public List<string> Districts { get; set; } =
    [
        "Chennai", "Coimbatore", "Madurai", "Tiruchirappalli", "Salem", "Tirunelveli",
        "Tiruppur", "Erode", "Vellore", "Thoothukudi", "Dindigul", "Thanjavur",
        "Virudhunagar", "Kanchipuram", "Cuddalore", "Nagercoil", "Karur", "Namakkal",
        "Sivakasi", "Hosur", "Other (outside Tamil Nadu)",
    ];

    public List<string> SafetyRules { get; set; } =
    [
        "Light in open ground, one item at a time, never indoors.",
        "Keep a bucket of sand and a bucket of water within arm’s reach.",
        "Use an agarbatti to light — never a matchstick held close.",
        "Wear cotton, tie long hair back, and keep footwear on.",
        "Respect the safe distance printed on every product page.",
        "Never return to a failed cracker for 10 minutes, then soak it.",
        "Children must be supervised on every item, including sparklers.",
        "Never light anything held in your hand except a sparkler.",
    ];

    public List<string> PopularSearches { get; set; } =
    [
        "Flower pots", "Lakshmi rocket", "Sparklers", "Ground chakkar",
        "Atom bomb", "Colour smoke", "Gift box",
    ];

    public List<TrustPoint> TrustPoints { get; set; } =
    [
        new("Direct from Sivakasi", "Our own unit, no distributor margin in the price."),
        new("PESO compliant", "Every batch tested under the 125 dB legal ceiling."),
        new("Ships in 48 hours", "Licensed surface transport across Tamil Nadu & Kerala."),
        new("32 years running", "Same family, same factory floor, since 1994."),
    ];

    /// <summary>
    /// Origins allowed to call the API. The storefront and the admin are two
    /// separate Vite apps on two ports, so both dev servers and both preview
    /// servers are listed.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } =
    [
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:4173",
        "http://127.0.0.1:4173",
        "http://localhost:5174",
        "http://127.0.0.1:5174",
        "http://localhost:4174",
        "http://127.0.0.1:4174",
    ];
}

public sealed class BrandOptions
{
    public string Name { get; set; } = "SKV Pyros";
    public string Short { get; set; } = "SKV Pyros";
    public string Tagline { get; set; } = "Sivakasi · Since 1994";
    public string Phone { get; set; } = "+91 94874 79000";
    public string Whatsapp { get; set; } = "+91 94874 79000";
    public string Email { get; set; } = "skvpyros@gmail.com";
    public string Address { get; set; } =
        "14/3 Sattur Main Road, Sivakasi, Virudhunagar District, Tamil Nadu 626123";
    public string Licence { get; set; } = "PESO Licence No. E/HQ/TN/22/1994 (S)";
    public string Gstin { get; set; } = "33AABCA1994K1Z8";
    public string Hours { get; set; } = "Mon–Sat, 9:00 AM – 8:00 PM IST";
}

public sealed class ShippingOptions
{
    public int FreeAbove { get; set; } = 2000;
    public int LocalFee { get; set; } = 149;
    public int OutstationFee { get; set; } = 249;
}

/// <summary><c>type</c> is <c>percentage</c> or <c>flat</c>.</summary>
public sealed record CouponRule(string Type, int Value, int MinOrder, string Note)
{
    public CouponRule() : this("percentage", 0, 0, string.Empty) { }
}

public sealed record PaymentMethod(string Id, string Label, string Hint)
{
    public PaymentMethod() : this(string.Empty, string.Empty, string.Empty) { }
}

public sealed record TrustPoint(string Title, string Text)
{
    public TrustPoint() : this(string.Empty, string.Empty) { }
}

/// <summary>
/// The shared passcode guarding <c>/api/admin/*</c>. Empty by default so a
/// deployment that forgets to set one gets a 503 saying exactly that, rather
/// than an admin API that is quietly wide open.
/// </summary>
public sealed class AdminOptions
{
    public string Passcode { get; set; } = string.Empty;
}

/// <summary>
/// Confirmation email. Off unless <see cref="SmtpOptions.Host"/> is set, so an
/// unconfigured deployment logs what it would have sent rather than telling a
/// customer an email is coming that never will.
/// </summary>
public sealed class NotificationOptions
{
    public SmtpOptions Smtp { get; set; } = new();

    public bool Enabled => !string.IsNullOrWhiteSpace(Smtp.Host);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;

    /// <summary>Blank sends unauthenticated, which some internal relays want.</summary>
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Falls back to the brand email when blank.</summary>
    public string From { get; set; } = string.Empty;
}

/// <summary>
/// Where a pickup order is collected from, and when. Shown at checkout so a
/// customer choosing "collect" knows what they are agreeing to.
/// </summary>
public sealed class PickupOptions
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "SKV Pyros factory counter";

    public string Address { get; set; } =
        "14/3 Sattur Main Road, Sivakasi, Virudhunagar District, Tamil Nadu 626123";

    public string Hours { get; set; } = "Mon–Sat, 9:00 AM – 8:00 PM IST";

    /// <summary>What to bring, shown as a short list on the checkout step.</summary>
    public List<string> Notes { get; set; } =
    [
        "Bring your order reference and the mobile number you booked with.",
        "Orders are held at the counter for seven days.",
        "Collection is free — no delivery charge is added.",
    ];
}
