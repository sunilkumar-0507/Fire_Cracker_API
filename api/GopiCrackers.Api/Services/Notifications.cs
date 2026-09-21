using System.Net;
using System.Net.Mail;
using System.Text;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>
/// Confirmation notifications — requirement 12.
///
/// The customer's own confirmation is the screen they are already looking at
/// plus the WhatsApp message the storefront offers to send; neither needs a
/// server. This is the shop's copy, and the customer's email receipt when one
/// can actually be delivered.
///
/// <see cref="LoggingNotificationSender"/> is the default and sends nothing.
/// That is the point: an unconfigured deployment logs what it *would* have sent
/// rather than pretending to a customer that an email is on its way. Configure
/// <c>Storefront:Notifications:Smtp:Host</c> and the SMTP sender takes over with
/// no other change.
/// </summary>
public interface INotificationSender
{
    Task OrderPlacedAsync(Order order, CancellationToken cancellationToken = default);

    Task EnquiryReceivedAsync(BulkEnquiry enquiry, CancellationToken cancellationToken = default);
}

/// <summary>Shared message bodies, so both senders say exactly the same thing.</summary>
public static class NotificationCopy
{
    public static string OrderSubject(Order order) =>
        $"Order {order.OrderId} received — ₹{order.Totals.Total:N0}";

    public static string OrderBody(Order order, BrandOptions brand)
    {
        var body = new StringBuilder();
        body.AppendLine($"Hello {order.Name.Split(' ')[0]},");
        body.AppendLine();
        body.AppendLine($"We have your order. Your reference is {order.OrderId} — quote it whenever you contact us.");
        body.AppendLine();

        foreach (var line in order.Items)
            body.AppendLine($"  {line.Qty} x {line.Name} — ₹{line.LineTotal:N0}");

        body.AppendLine();
        body.AppendLine($"  Subtotal      ₹{order.Totals.Subtotal:N0}");
        if (order.Totals.CouponDiscount > 0)
            body.AppendLine($"  Coupon        -₹{order.Totals.CouponDiscount:N0}");
        body.AppendLine(order.IsPickup
            ? "  Collection    Free"
            : $"  Delivery      ₹{order.Totals.Shipping:N0}");
        body.AppendLine($"  Total         ₹{order.Totals.Total:N0}");
        body.AppendLine();

        body.AppendLine(order.IsPickup
            ? $"Collect from {brand.Address} from {order.DeliveryFrom:ddd d MMM}. Bring this reference."
            : $"Delivering to {order.Address}, {order.City} {order.Pincode} between " +
              $"{order.DeliveryFrom:ddd d MMM} and {order.DeliveryTo:ddd d MMM}.");

        body.AppendLine();
        body.AppendLine("Store the carton somewhere cool and dry, off the floor, away from the kitchen");
        body.AppendLine("and any electrical point until the night itself.");
        body.AppendLine();
        body.AppendLine($"{brand.Name} · {brand.Phone} · {brand.Hours}");

        return body.ToString();
    }

    public static string EnquirySubject(BulkEnquiry enquiry) =>
        $"Bulk enquiry {enquiry.EnquiryId} received";

    public static string EnquiryBody(BulkEnquiry enquiry, BrandOptions brand)
    {
        var body = new StringBuilder();
        body.AppendLine($"Hello {enquiry.Name.Split(' ')[0]},");
        body.AppendLine();
        body.AppendLine($"We have your bulk enquiry. Your reference is {enquiry.EnquiryId}.");
        body.AppendLine();
        body.AppendLine($"  District   {enquiry.District}");
        if (enquiry.Quantity is not null) body.AppendLine($"  Quantity   {enquiry.Quantity}");
        if (enquiry.Budget is not null) body.AppendLine($"  Budget     {enquiry.Budget}");
        body.AppendLine();
        body.AppendLine("Someone will call you within one working day with a quote.");
        body.AppendLine();
        body.AppendLine($"{brand.Name} · {brand.Phone} · {brand.Hours}");

        return body.ToString();
    }
}

/// <summary>
/// The default. Writes what would have been sent to the log and returns.
///
/// A shop running without SMTP still has the confirmation screen, the reference
/// number, the WhatsApp message and the admin order book — so nothing is lost
/// here except an email nobody configured.
/// </summary>
public sealed class LoggingNotificationSender(
    ILogger<LoggingNotificationSender> logger,
    IOptions<StorefrontOptions> options) : INotificationSender
{
    private readonly StorefrontOptions _options = options.Value;

    public Task OrderPlacedAsync(Order order, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Order {OrderId} for {Name} ({Phone}) — {Total} via {Fulfilment}. " +
            "No email sent: Storefront:Notifications:Smtp:Host is not configured.",
            order.OrderId, order.Name, order.Phone, order.Totals.Total, order.Fulfilment);

        _ = _options;
        return Task.CompletedTask;
    }

    public Task EnquiryReceivedAsync(BulkEnquiry enquiry, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Bulk enquiry {EnquiryId} from {Name} ({Phone}) in {District}. No email sent.",
            enquiry.EnquiryId, enquiry.Name, enquiry.Phone, enquiry.District);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends through a configured SMTP relay: a copy to the shop always, and a
/// receipt to the customer when they gave an address.
///
/// Failures are logged and swallowed. The order is already accepted and the
/// customer already has their reference number on screen — turning a refused
/// SMTP connection into a failed checkout would lose the sale to fix nothing.
/// </summary>
public sealed class SmtpNotificationSender(
    ILogger<SmtpNotificationSender> logger,
    IOptions<StorefrontOptions> options) : INotificationSender
{
    private readonly StorefrontOptions _options = options.Value;

    public async Task OrderPlacedAsync(Order order, CancellationToken cancellationToken = default)
    {
        var brand = _options.Brand;
        var subject = NotificationCopy.OrderSubject(order);
        var body = NotificationCopy.OrderBody(order, brand);

        await SendAsync(brand.Email, $"[{order.OrderId}] {order.Name} — {subject}", body, cancellationToken);

        if (!string.IsNullOrWhiteSpace(order.Email))
            await SendAsync(order.Email, subject, body, cancellationToken);
    }

    public async Task EnquiryReceivedAsync(BulkEnquiry enquiry, CancellationToken cancellationToken = default)
    {
        var brand = _options.Brand;
        var subject = NotificationCopy.EnquirySubject(enquiry);
        var body = NotificationCopy.EnquiryBody(enquiry, brand);

        await SendAsync(brand.Email, $"[{enquiry.EnquiryId}] {enquiry.Name} — {subject}", body, cancellationToken);

        if (!string.IsNullOrWhiteSpace(enquiry.Email))
            await SendAsync(enquiry.Email, subject, body, cancellationToken);
    }

    private async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var smtp = _options.Notifications.Smtp;

        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl };

            if (!string.IsNullOrWhiteSpace(smtp.Username))
                client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);

            var from = string.IsNullOrWhiteSpace(smtp.From) ? _options.Brand.Email : smtp.From;

            using var message = new MailMessage(from, to, subject, body)
            {
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };

            await client.SendMailAsync(message, cancellationToken);
            logger.LogInformation("Sent \"{Subject}\" to {To}", subject, to);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not email \"{Subject}\" to {To}", subject, to);
        }
    }
}
