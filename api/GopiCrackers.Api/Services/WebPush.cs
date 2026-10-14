using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GopiCrackers.Api.Models;
using GopiCrackers.Api.Options;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Services;

/// <summary>
/// The browsers that pop up a notification when an order is placed — the web
/// admin, opened on a phone or a desktop, with notifications allowed. No app.
///
/// Kept the same way as <see cref="PushDeviceStore"/> and for the same reason:
/// the admin site re-subscribes every time it is opened, so a list lost to a
/// redeploy refills itself, and an endpoint the browser's push service answers
/// 404 or 410 for is dropped on the next send.
///
/// Keyed on the endpoint URL, which the browser's push service makes unique per
/// browser install.
/// </summary>
public sealed class WebPushSubscriptionStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WebPushSubscription> _subscriptions;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WebPushSubscriptionStore(IWebHostEnvironment env, IOptions<StorefrontOptions> options)
    {
        var configured = options.Value.Notifications.WebPush.SubscriptionsPath;
        _path = Path.GetFullPath(Path.Combine(
            env.ContentRootPath,
            string.IsNullOrWhiteSpace(configured) ? "web-push-subscriptions.json" : configured));

        _subscriptions = Load(_path).ToDictionary(s => s.Endpoint, StringComparer.Ordinal);
    }

    public IReadOnlyList<WebPushSubscription> All()
    {
        lock (_gate) return [.. _subscriptions.Values.OrderByDescending(s => s.LastSeen)];
    }

    /// <summary>Adds the subscription, or refreshes its keys, name and last-seen time.</summary>
    public WebPushSubscription Register(string endpoint, string p256dh, string auth, string? name, DateTimeOffset now)
    {
        lock (_gate)
        {
            var subscription = _subscriptions.TryGetValue(endpoint, out var existing)
                ? existing with { P256dh = p256dh, Auth = auth, Name = name ?? existing.Name, LastSeen = now }
                : new WebPushSubscription(endpoint, p256dh, auth, name, now, now);

            _subscriptions[endpoint] = subscription;
            Save();
            return subscription;
        }
    }

    public bool Remove(string endpoint)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(endpoint)) return false;
            Save();
            return true;
        }
    }

    public void RemoveMany(IEnumerable<string> endpoints)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var endpoint in endpoints) changed |= _subscriptions.Remove(endpoint);
            if (changed) Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_subscriptions.Values, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static List<WebPushSubscription> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<WebPushSubscription>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt list costs nothing but a re-subscribe next time the admin is opened.
            return [];
        }
    }
}

/// <summary>
/// The server's VAPID key pair: what identifies this API to the browsers' push
/// services, and what the admin site passes to <c>pushManager.subscribe</c> as
/// the <c>applicationServerKey</c>.
///
/// Taken from <c>Storefront:Notifications:WebPush:PublicKey</c> and
/// <c>PrivateKey</c> when both are set. Otherwise a pair is generated on first
/// start and kept in <c>KeysPath</c>, so it survives a restart. Changing the
/// pair invalidates every browser's subscription — the admin site notices the
/// new public key and subscribes again.
/// </summary>
public sealed class VapidKeys
{
    /// <summary>The uncompressed P-256 point, base64url — the form browsers take.</summary>
    public string PublicKey { get; }

    /// <summary><c>mailto:</c> or <c>https:</c>, so a push service can reach whoever runs the API.</summary>
    public string Subject { get; }

    private readonly byte[] _publicKey;
    private readonly byte[] _privateKey;

    public VapidKeys(IWebHostEnvironment env, IOptions<StorefrontOptions> options, ILogger<VapidKeys> logger)
    {
        var settings = options.Value.Notifications.WebPush;

        if (!string.IsNullOrWhiteSpace(settings.PublicKey) && !string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            (_publicKey, _privateKey) = (Base64Url.DecodeFromChars(settings.PublicKey), Base64Url.DecodeFromChars(settings.PrivateKey));
        }
        else
        {
            var path = Path.GetFullPath(Path.Combine(
                env.ContentRootPath,
                string.IsNullOrWhiteSpace(settings.KeysPath) ? "web-push-keys.json" : settings.KeysPath));
            (_publicKey, _privateKey) = LoadOrCreate(path, logger);
        }

        if (_publicKey.Length != 65 || _publicKey[0] != 0x04 || _privateKey.Length != 32)
            throw new InvalidOperationException(
                "Storefront:Notifications:WebPush keys are not a P-256 pair: the public key must be 65 bytes " +
                "(uncompressed) and the private key 32, both base64url.");

        PublicKey = Base64Url.EncodeToString(_publicKey);
        Subject = string.IsNullOrWhiteSpace(settings.Subject)
            ? $"mailto:{options.Value.Brand.Email}"
            : settings.Subject;
    }

    /// <summary>
    /// The <c>Authorization</c> header value for one push service: a short-lived
    /// ES256 JWT with that service's origin as its audience, and the public key.
    /// </summary>
    public string Authorization(Uri endpoint, DateTimeOffset now)
    {
        var header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            // The spec allows up to 24 hours. Twelve leaves room for a server clock that runs fast.
            exp = now.AddHours(12).ToUnixTimeSeconds(),
            sub = Subject,
        }));

        using var key = ECDsa.Create(Parameters(_publicKey, _privateKey));
        // .NET signs in IEEE P1363 form (r ‖ s) by default, which is what JWS wants.
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256);

        return $"vapid t={header}.{claims}.{Base64Url.EncodeToString(signature)}, k={PublicKey}";
    }

    internal static ECParameters Parameters(byte[] publicKey, byte[]? privateKey = null) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        D = privateKey,
    };

    private static (byte[] Public, byte[] Private) LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<SavedKeys>(File.ReadAllText(path), SavedJson);
            if (saved is not null)
                return (Base64Url.DecodeFromChars(saved.PublicKey), Base64Url.DecodeFromChars(saved.PrivateKey));
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new SavedKeys(Base64Url.EncodeToString(publicKey), Base64Url.EncodeToString(parameters.D!)), SavedJson));

        logger.LogWarning(
            "Generated a web push key pair and saved it to {Path}. Keep that file (or copy both keys into " +
            "Storefront:Notifications:WebPush) — a new pair means every browser has to allow notifications again.",
            path);

        return (publicKey, parameters.D!);
    }

    private static readonly JsonSerializerOptions SavedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record SavedKeys(string PublicKey, string PrivateKey);
}

/// <summary>
/// Message encryption for Web Push (RFC 8291), in the <c>aes128gcm</c> content
/// coding (RFC 8188). Every push service requires it: the payload is sealed to
/// the browser's own key, so the push service carries it without reading it.
/// </summary>
public static class WebPushEncryption
{
    private const int RecordSize = 4096;
    private const int HeaderSize = 16 + 4 + 1 + 65;
    private const int TagSize = 16;

    /// <summary>The largest payload one record carries, after the header, padding delimiter and tag.</summary>
    public const int MaxPayload = RecordSize - HeaderSize - 1 - TagSize;

    public static byte[] Encrypt(byte[] payload, byte[] userAgentPublicKey, byte[] authSecret)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(payload, userAgentPublicKey, authSecret, serverKey, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>With the ephemeral key and salt supplied — for the RFC's worked example.</summary>
    internal static byte[] Encrypt(
        byte[] payload, byte[] userAgentPublicKey, byte[] authSecret, ECDiffieHellman serverKey, byte[] salt)
    {
        if (userAgentPublicKey.Length != 65 || userAgentPublicKey[0] != 0x04)
            throw new ArgumentException("The subscription's p256dh key is not an uncompressed P-256 point.", nameof(userAgentPublicKey));
        if (authSecret.Length != 16)
            throw new ArgumentException("The subscription's auth secret is not 16 bytes.", nameof(authSecret));
        if (payload.Length > MaxPayload)
            throw new ArgumentException($"A web push payload carries at most {MaxPayload} bytes.", nameof(payload));

        var q = serverKey.ExportParameters(includePrivateParameters: false).Q;
        byte[] serverPublicKey = [0x04, .. q.X!, .. q.Y!];

        using var userAgent = ECDiffieHellman.Create(VapidKeys.Parameters(userAgentPublicKey));
        var sharedSecret = serverKey.DeriveRawSecretAgreement(userAgent.PublicKey);

        // RFC 8291 §3.4: mix the auth secret in, then derive the RFC 8188 key and nonce.
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. userAgentPublicKey, .. serverPublicKey];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        // One record, so it is the last: the 0x02 delimiter, no further padding.
        byte[] plaintext = [.. payload, 0x02];
        var body = new byte[HeaderSize + plaintext.Length + TagSize];

        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), RecordSize);
        body[20] = (byte)serverPublicKey.Length;
        serverPublicKey.CopyTo(body, 21);

        using var aes = new AesGcm(contentKey, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            body.AsSpan(HeaderSize, plaintext.Length),
            body.AsSpan(HeaderSize + plaintext.Length, TagSize));

        return body;
    }
}

/// <summary>
/// Sends to every subscribed browser through its own push service — Google's
/// for Chrome and Edge on Android, Mozilla's for Firefox, Apple's for Safari
/// and iPhone home-screen web apps. Each subscription's endpoint names its
/// service; the VAPID key is the only credential, and it is ours.
/// </summary>
public sealed class WebPushClient(
    IHttpClientFactory http,
    WebPushSubscriptionStore subscriptions,
    VapidKeys vapid,
    TimeProvider clock,
    ILogger<WebPushClient> logger)
{
    /// <summary>How long a push service holds a message for a browser that is offline.</summary>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <param name="url">The admin page to open, relative to the admin site — the service worker resolves it.</param>
    /// <param name="tag">One notification per order: a re-send replaces rather than stacks.</param>
    public async Task SendAsync(string title, string body, string url, string tag, object data, CancellationToken cancellationToken)
    {
        var targets = subscriptions.All();
        if (targets.Count == 0) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title, body, url, tag, data }, PayloadJson);
        var client = http.CreateClient(nameof(WebPushClient));
        var now = clock.GetUtcNow();

        var results = await Task.WhenAll(targets.Select(t => SendOneAsync(client, t, payload, now, cancellationToken)));

        var gone = results.OfType<string>().ToList();
        if (gone.Count > 0)
        {
            subscriptions.RemoveMany(gone);
            logger.LogInformation("Dropped {Count} web push subscription(s) the browser has given up", gone.Count);
        }
    }

    /// <returns>The endpoint when the push service says it no longer exists; otherwise null.</returns>
    private async Task<string?> SendOneAsync(
        HttpClient client, WebPushSubscription target, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(target.Endpoint);
            var sealedPayload = WebPushEncryption.Encrypt(payload, Base64Url.DecodeFromChars(target.P256dh), Base64Url.DecodeFromChars(target.Auth));

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(sealedPayload),
            };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("Authorization", vapid.Authorization(endpoint, now));
            request.Headers.Add("TTL", ((int)TimeToLive.TotalSeconds).ToString());
            request.Headers.Add("Urgency", "high");

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return null;

            // 404 and 410 are the push service saying the browser unsubscribed or the subscription expired.
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                return target.Endpoint;

            logger.LogWarning("Web push to {Host} answered {Status}: {Body}",
                endpoint.Host, (int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Web push to {Endpoint} failed", target.Endpoint);
        }

        return null;
    }
}
