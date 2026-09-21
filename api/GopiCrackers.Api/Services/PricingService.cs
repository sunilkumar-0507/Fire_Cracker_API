using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>Why a requested line could not be priced.</summary>
public sealed record UnknownLine(string Id, string Reason);

public sealed record QuoteOutcome(CartQuote? Quote, IReadOnlyList<UnknownLine> Unknown)
{
    public bool Ok => Unknown.Count == 0 && Quote is not null;
}

/// <summary>
/// Cart pricing. A port of <c>selectTotals</c> and <c>applyCoupon</c> from
/// <c>src/store/cartStore.js</c>.
///
/// The client sends ids and quantities; every rupee is re-read from the
/// catalogue here. Prices that arrive in the request body are ignored.
/// </summary>
public sealed class PricingService(CatalogStore catalog, IOptions<StorefrontOptions> options)
{
    private readonly StorefrontOptions _options = options.Value;

    public ShippingOptions Shipping => _options.Shipping;

    /// <summary>A coupon that waives the delivery fee instead of cutting the subtotal.</summary>
    public const string ShippingCoupon = "shipping";

    /// <summary>
    /// The codes the checkout will accept.
    ///
    /// <c>offers.json</c> is the source, because that is the list the admin
    /// edits and the shop advertises — a discount created in the admin is
    /// redeemable the moment it is saved, and an offer can no longer be printed
    /// on the offers page while the checkout refuses it.
    ///
    /// Configuration under <c>Storefront:Coupons</c> is layered on top, for the
    /// codes that have to behave differently from the way they are advertised.
    /// <c>DIWALI75</c> is the reason it exists: the offer advertises the 75%
    /// that is already inside every catalogue price, so its rule is worth 0 and
    /// the checkout does not take the same discount off twice.
    ///
    /// Read from the catalogue on each access rather than cached, so an admin
    /// save is live without restarting the API.
    /// </summary>
    public IReadOnlyDictionary<string, CouponRule> Coupons
    {
        get
        {
            var rules = new Dictionary<string, CouponRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var offer in catalog.Offers)
            {
                if (string.IsNullOrWhiteSpace(offer.Code)) continue;

                // The subtitle is the one-line "what you get" already written
                // for the offers page, which is exactly what a checkout wants
                // to echo back when the code is accepted.
                rules[offer.Code.Trim()] = new CouponRule(
                    offer.Type, offer.Value, offer.MinOrder,
                    string.IsNullOrWhiteSpace(offer.Subtitle) ? offer.Title : offer.Subtitle);
            }

            foreach (var (code, rule) in _options.Coupons) rules[code] = rule;

            return rules;
        }
    }

    /* ---------------------------------------------------------------------- */
    /* Line resolution                                                         */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Resolves ids to priced lines. Products are looked up first, then combos,
    /// so <c>p-001</c> and <c>cmb-01</c> both work in the same basket.
    /// Quantities above available stock are capped rather than rejected — the
    /// line reports <c>capped: true</c> so the UI can explain itself.
    /// </summary>
    public QuoteOutcome BuildQuote(
        IReadOnlyList<CartLineRequest> requested,
        string? couponCode,
        string? fulfilment = null)
    {
        var unknown = new List<UnknownLine>();
        var notices = new List<string>();
        var lines = new List<CartLine>();

        // Merge duplicate ids the way addItem() would, rather than pricing twice.
        var merged = requested
            .GroupBy(i => i.Id?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, Qty: g.Sum(i => i.Qty)));

        foreach (var (id, qty) in merged)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                unknown.Add(new UnknownLine(id, "Blank id"));
                continue;
            }

            var line = ResolveLine(id, qty);
            if (line is null)
            {
                unknown.Add(new UnknownLine(id, "No product or combo with that id or slug"));
                continue;
            }

            // "Withdrawn" and "sold out" are different sentences to read on a
            // checkout that has just refused you, so they are reported apart.
            if (line.Availability == Availability.Unavailable)
            {
                unknown.Add(new UnknownLine(id, $"“{line.Name}” is temporarily unavailable"));
                continue;
            }

            if (line.Stock <= 0)
            {
                unknown.Add(new UnknownLine(id, $"“{line.Name}” is out of stock"));
                continue;
            }

            if (line.Capped)
                notices.Add($"Only {line.Stock} of “{line.Name}” left — quantity reduced to {line.Qty}.");

            lines.Add(line);
        }

        if (unknown.Count > 0) return new QuoteOutcome(null, unknown);

        var (coupon, couponNotice) = ResolveCoupon(couponCode, lines.Sum(l => l.LineTotal));
        if (couponNotice is not null) notices.Add(couponNotice);

        var totals = CalculateTotals(lines, coupon, fulfilment);
        if (Fulfilment.IsPickup(fulfilment))
            notices.Add("Collecting from the Sivakasi counter — no delivery charge.");

        return new QuoteOutcome(new CartQuote(lines, totals, coupon, notices), []);
    }

    private CartLine? ResolveLine(string id, int requestedQty)
    {
        var product = catalog.FindProduct(id);
        if (product is not null)
        {
            var qty = Math.Clamp(requestedQty, 1, Math.Max(product.Stock, 1));
            return new CartLine(
                product.Id, "product", product.Slug, product.Name, product.Unit,
                product.Price, product.Mrp, product.Images.FirstOrDefault(),
                product.Category, product.Stock, product.Tags,
                qty, product.Price * qty, qty < requestedQty, product.Availability);
        }

        var combo = catalog.FindCombo(id);
        if (combo is not null)
        {
            var qty = Math.Clamp(requestedQty, 1, Math.Max(combo.Stock, 1));
            return new CartLine(
                combo.Id, "combo", combo.Slug, combo.Name,
                $"{combo.ItemCount} items · {combo.Serves}",
                combo.Price, combo.Mrp, $"{combo.Art}/1",
                "combo-packs", combo.Stock, ["combo"],
                qty, combo.Price * qty, qty < requestedQty,
                combo.Stock > 0 ? Availability.Available : Availability.OutOfStock);
        }

        return null;
    }

    /* ---------------------------------------------------------------------- */
    /* Coupons                                                                 */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Returns a result rather than throwing — the storefront shows the reason,
    /// exactly as <c>applyCoupon</c> does.
    /// </summary>
    public CouponValidationResult ValidateCoupon(string? rawCode, int subtotal)
    {
        var code = (rawCode ?? string.Empty).Trim().ToUpperInvariant();

        // `Coupons`, not `_options.Coupons` — the catalogue's offers plus the
        // configured overrides, so an admin-created code validates here too.
        if (!Coupons.TryGetValue(code, out var rule))
            return new CouponValidationResult(false, $"“{code}” is not a valid code");

        if (subtotal < rule.MinOrder)
        {
            var gap = (rule.MinOrder - subtotal).ToString("N0",
                System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
            return new CouponValidationResult(false, $"Add ₹{gap} more to use {code}");
        }

        var applied = new AppliedCoupon(code, rule.Type, rule.Value, rule.MinOrder, rule.Note);
        return new CouponValidationResult(true, rule.Note, applied, DiscountFor(applied, subtotal));
    }

    /// <summary>Applies a coupon if it qualifies; otherwise reports why it did not.</summary>
    private (AppliedCoupon? Coupon, string? Notice) ResolveCoupon(string? rawCode, int subtotal)
    {
        if (string.IsNullOrWhiteSpace(rawCode)) return (null, null);

        var result = ValidateCoupon(rawCode, subtotal);
        return result.Ok ? (result.Coupon, result.Message) : (null, result.Message);
    }

    private static int DiscountFor(AppliedCoupon coupon, int subtotal)
    {
        if (subtotal < coupon.MinOrder) return 0;

        // A free-delivery coupon is worth nothing off the goods — it is applied
        // against the delivery fee in Totals instead.
        if (coupon.Type.Equals(ShippingCoupon, StringComparison.OrdinalIgnoreCase)) return 0;

        return coupon.Type.Equals("percentage", StringComparison.OrdinalIgnoreCase)
            // JS Math.round is half-up; banker's rounding would be a rupee out
            // on any .5 discount, so pin the mode explicitly.
            ? (int)Math.Round(subtotal * coupon.Value / 100.0, MidpointRounding.AwayFromZero)
            : Math.Min(coupon.Value, subtotal);
    }

    /* ---------------------------------------------------------------------- */
    /* Money                                                                   */
    /* ---------------------------------------------------------------------- */

    /// <summary>The full money breakdown — a port of <c>selectTotals</c>.</summary>
    public CartTotals CalculateTotals(
        IReadOnlyList<CartLine> lines,
        AppliedCoupon? coupon,
        string? fulfilment = null)
    {
        var subtotal = lines.Sum(l => l.Price * l.Qty);
        var mrpTotal = lines.Sum(l => (l.Mrp == 0 ? l.Price : l.Mrp) * l.Qty);
        var catalogueSavings = mrpTotal - subtotal;

        var couponDiscount = coupon is null ? 0 : DiscountFor(coupon, subtotal);

        var afterCoupon = subtotal - couponDiscount;

        // A coupon of type "shipping" (FREESHIP) buys free delivery rather than
        // money off the goods, so it waives the fee once its minimum is met.
        var freeDelivery = coupon is not null
            && coupon.Type.Equals(ShippingCoupon, StringComparison.OrdinalIgnoreCase)
            && subtotal >= coupon.MinOrder;

        // Nothing is being delivered on a pickup, so nothing is charged for it.
        var shipping = Fulfilment.IsPickup(fulfilment)
            || freeDelivery
            || afterCoupon == 0
            || afterCoupon >= _options.Shipping.FreeAbove
            ? 0
            : _options.Shipping.LocalFee;

        return new CartTotals(
            Subtotal: subtotal,
            MrpTotal: mrpTotal,
            CatalogueSavings: catalogueSavings,
            CouponDiscount: couponDiscount,
            Shipping: shipping,
            Total: afterCoupon + shipping,
            TotalSavings: catalogueSavings + couponDiscount,
            FreeShippingGap: Fulfilment.IsPickup(fulfilment) || freeDelivery
                ? 0
                : Math.Max(0, _options.Shipping.FreeAbove - afterCoupon),
            Count: lines.Sum(l => l.Qty));
    }
}
