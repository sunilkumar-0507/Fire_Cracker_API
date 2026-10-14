using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>
/// The phones that ring when an order is placed.
///
/// The shop admin app registers its Expo push token here on every launch, so
/// the list heals itself: a file lost to a redeploy refills the next time each
/// phone opens the app, and a token Expo reports as dead is dropped on the next
/// send. That is why this is a small JSON file rather than a table — nothing in
/// it is worth a migration.
///
/// The file is <c>Storefront:Notifications:Push:DevicesPath</c>, relative to the
/// content root. Like the uploads folder, point it outside the publish folder
/// on a server — see DEPLOYMENT.md.
/// </summary>
public sealed class PushDeviceStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PushDevice> _devices;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PushDeviceStore(IWebHostEnvironment env, IOptions<StorefrontOptions> options)
    {
        var configured = options.Value.Notifications.Push.DevicesPath;
        _path = Path.GetFullPath(Path.Combine(
            env.ContentRootPath,
            string.IsNullOrWhiteSpace(configured) ? "push-devices.json" : configured));

        _devices = Load(_path).ToDictionary(d => d.Token, StringComparer.Ordinal);
    }

    public IReadOnlyList<PushDevice> All()
    {
        lock (_gate) return [.. _devices.Values.OrderByDescending(d => d.LastSeen)];
    }

    /// <summary>Adds the token, or refreshes its name and last-seen time.</summary>
    public PushDevice Register(string token, string? platform, string? name, DateTimeOffset now)
    {
        lock (_gate)
        {
            var device = _devices.TryGetValue(token, out var existing)
                ? existing with { Platform = platform ?? existing.Platform, Name = name ?? existing.Name, LastSeen = now }
                : new PushDevice(token, platform, name, now, now);

            _devices[token] = device;
            Save();
            return device;
        }
    }

    public bool Remove(string token)
    {
        lock (_gate)
        {
            if (!_devices.Remove(token)) return false;
            Save();
            return true;
        }
    }

    public void RemoveMany(IEnumerable<string> tokens)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var token in tokens) changed |= _devices.Remove(token);
            if (changed) Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_devices.Values, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static List<PushDevice> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PushDevice>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt list costs nothing but a re-registration on next launch.
            return [];
        }
    }
}

/// <summary>
/// Sends through Expo's push service, which hands the message on to Firebase
/// for Android and to APNs for iPhones — so the API needs no Google or Apple
/// credentials of its own; those live with the app's EAS project.
///
/// Expo accepts up to 100 messages a request. Tokens it answers
/// <c>DeviceNotRegistered</c> for — an uninstalled app, a reset phone — are
/// removed so they are not tried again.
/// </summary>
public sealed class ExpoPushClient(
    IHttpClientFactory http,
    PushDeviceStore devices,
    IOptions<StorefrontOptions> options,
    ILogger<ExpoPushClient> logger)
{
    public const string Endpoint = "https://exp.host/--/api/v2/push/send";

    /// <summary>The Android channel the app creates. Must match ORDER_CHANNEL there.</summary>
    public const string OrderChannel = "orders";

    public async Task SendAsync(string title, string body, object data, CancellationToken cancellationToken)
    {
        var targets = devices.All();
        if (targets.Count == 0) return;

        var client = http.CreateClient(nameof(ExpoPushClient));
        var accessToken = options.Value.Notifications.Push.AccessToken;

        foreach (var chunk in targets.Chunk(100))
        {
            var messages = chunk.Select(d => new ExpoMessage(
                To: d.Token,
                Title: title,
                Body: body,
                Data: data,
                Sound: "default",
                Priority: "high",
                ChannelId: OrderChannel)).ToList();

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(messages, options: ExpoJson),
            };
            if (!string.IsNullOrWhiteSpace(accessToken))
                request.Headers.Authorization = new("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Expo push answered {Status}: {Body}",
                    (int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
                continue;
            }

            var result = await response.Content.ReadFromJsonAsync<ExpoResponse>(ExpoJson, cancellationToken);
            var tickets = result?.Data ?? [];

            // Tickets come back in the order the messages were sent.
            var dead = tickets
                .Select((t, i) => (Ticket: t, Token: i < chunk.Length ? chunk[i].Token : null))
                .Where(x => x.Ticket.Status == "error" && x.Ticket.Details?.Error == "DeviceNotRegistered" && x.Token is not null)
                .Select(x => x.Token!)
                .ToList();

            if (dead.Count > 0)
            {
                devices.RemoveMany(dead);
                logger.LogInformation("Dropped {Count} push token(s) Expo no longer recognises", dead.Count);
            }

            foreach (var ticket in tickets.Where(t => t.Status == "error" && t.Details?.Error != "DeviceNotRegistered"))
                logger.LogWarning("Expo push error: {Message}", ticket.Message);
        }
    }

    private static readonly JsonSerializerOptions ExpoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ExpoMessage(
        string To, string Title, string Body, object Data, string Sound, string Priority, string ChannelId);

    private sealed record ExpoResponse(List<ExpoTicket>? Data);

    private sealed record ExpoTicket(string Status, string? Id, string? Message, ExpoTicketDetails? Details);

    private sealed record ExpoTicketDetails(string? Error);
}

/// <summary>
/// Adds a push to the shop's phones — for new orders and new bulk enquiries —
/// in front of whichever email sender is configured.
///
/// Push never delays or fails a checkout (or an enquiry form): it gets a few seconds of its own —
/// not the customer's request token, so a shopper closing the tab does not
/// cancel the shop's alert — and any failure is logged and swallowed, the same
/// rule the SMTP sender follows.
/// </summary>
public sealed class PushNotificationSender(
    INotificationSender inner,
    ExpoPushClient push,
    ILogger<PushNotificationSender> logger) : INotificationSender
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public async Task OrderPlacedAsync(Order order, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = new CancellationTokenSource(Budget);
            await push.SendAsync(
                NotificationCopy.OrderPushTitle(order),
                NotificationCopy.OrderPushBody(order),
                new { orderId = order.OrderId },
                timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not push order {OrderId} to the shop's phones", order.OrderId);
        }

        await inner.OrderPlacedAsync(order, cancellationToken);
    }

    public async Task EnquiryReceivedAsync(BulkEnquiry enquiry, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = new CancellationTokenSource(Budget);
            await push.SendAsync(
                NotificationCopy.EnquiryPushTitle(enquiry),
                NotificationCopy.EnquiryPushBody(enquiry),
                new { enquiryId = enquiry.EnquiryId },
                timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not push enquiry {EnquiryId} to the shop's phones", enquiry.EnquiryId);
        }

        await inner.EnquiryReceivedAsync(enquiry, cancellationToken);
    }
}
