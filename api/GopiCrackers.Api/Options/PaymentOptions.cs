namespace GopiCrackers.Api.Options;

/// <summary>
/// Online payment configuration.
///
/// Everything here is off until credentials are supplied. That is the point:
/// a deployment with no keys keeps taking cash-on-delivery orders exactly as
/// before, rather than offering a customer a payment page that cannot work.
/// </summary>
public sealed class PaymentOptions
{
    public CashfreeOptions Cashfree { get; set; } = new();
}

/// <summary>
/// Cashfree Payments (PG v2023-08-01).
///
/// <see cref="AppId"/> and <see cref="SecretKey"/> come from the Cashfree
/// merchant dashboard. Keep them in user-secrets or the environment, never in
/// appsettings.json — the secret key can move money.
/// </summary>
public sealed class CashfreeOptions
{
    /// <summary><c>sandbox</c> or <c>production</c>.</summary>
    public string Mode { get; set; } = "sandbox";

    public string AppId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Where Cashfree sends the customer back to. The storefront route that
    /// reads the order reference out of the query string and shows the receipt.
    /// </summary>
    public string ReturnUrl { get; set; } = "http://localhost:5173/checkout/payment-return";

    /// <summary>
    /// The API version header Cashfree requires on every call. Pinned rather
    /// than tracking "latest", so their next release cannot change the shape of
    /// a response this code is parsing.
    /// </summary>
    public string ApiVersion { get; set; } = "2023-08-01";

    /// <summary>
    /// True once both credentials are present. Everything payment-related is
    /// gated on this, so the absence of keys is a configuration state rather
    /// than a runtime error.
    /// </summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(SecretKey);

    public bool IsProduction =>
        Mode.Equals("production", StringComparison.OrdinalIgnoreCase);

    /// <summary>The API host for the configured mode.</summary>
    public string BaseUrl => IsProduction
        ? "https://api.cashfree.com/pg"
        : "https://sandbox.cashfree.com/pg";
}
