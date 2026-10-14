using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GopiCrackers.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// The message encryption, checked against the worked example in RFC 8291
/// Appendix A — if this passes, a real browser can read what the API sends.
/// </summary>
public sealed class WebPushEncryptionTests
{
    [Fact]
    public void Encrypting_the_RFC_8291_example_gives_the_RFC_s_bytes()
    {
        var serverPrivate = Base64Url.DecodeFromChars("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        var serverPublic = Base64Url.DecodeFromChars(
            "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        var userAgentPublic = Base64Url.DecodeFromChars(
            "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var authSecret = Base64Url.DecodeFromChars("BTBZMqHH6r4Tts7J_aSIgg");
        var salt = Base64Url.DecodeFromChars("DGv6ra1nlYgDCS1FRnbzlw");

        using var serverKey = ECDiffieHellman.Create(VapidKeys.Parameters(serverPublic, serverPrivate));

        var body = WebPushEncryption.Encrypt(
            "When I grow up, I want to be a watermelon"u8.ToArray(), userAgentPublic, authSecret, serverKey, salt);

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.EncodeToString(body));
    }
}

/// <summary>
/// The web admin's browser notifications: subscribing, and the push an order
/// or an enquiry sends. The browsers' push services are <see cref="FakeWebPushService"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WebPushTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private readonly ApiFixture _fixture = fixture;

    private static object Order() => new
    {
        name = "Meenakshi Raghavan",
        phone = "9842011994",
        address = "12 Second Cross Street, Adyar",
        city = "Chennai",
        district = "Chennai",
        pincode = "600020",
        payment = "upi",
        items = new[] { new { id = "p-002", qty = 2 } },
    };

    private async Task<List<string>> SubscribedEndpointsAsync() =>
        [.. (await GetAdminJsonAsync("/api/admin/web-push/subscriptions")).EnumerateArray()
            .Select(s => s.GetProperty("endpoint").GetString()!)];

    private async Task SubscribeAsync(FakeBrowser browser, string? name = null)
    {
        var (status, _) = await PostAdminAsync("/api/admin/web-push/subscriptions", browser.Subscription(name));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private Task<HttpStatusCode> UnsubscribeAsync(FakeBrowser browser) =>
        DeleteAdminAsync($"/api/admin/web-push/subscriptions?endpoint={Uri.EscapeDataString(browser.Endpoint)}");

    [Fact]
    public async Task The_key_is_the_public_half_of_a_P256_pair()
    {
        var key = (await GetAdminJsonAsync("/api/admin/web-push/key")).GetProperty("publicKey").GetString()!;

        var raw = Base64Url.DecodeFromChars(key);
        Assert.Equal(65, raw.Length);
        Assert.Equal(0x04, raw[0]);
    }

    [Fact]
    public async Task A_browser_subscribes_once_however_often_it_asks_and_can_unsubscribe()
    {
        using var browser = new FakeBrowser();

        var (first, body) = await PostAdminAsync("/api/admin/web-push/subscriptions", browser.Subscription("Counter laptop"));
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal("Counter laptop", body.GetProperty("name").GetString());

        // The admin site subscribes every time it is opened.
        await SubscribeAsync(browser);
        Assert.Single(await SubscribedEndpointsAsync(), e => e == browser.Endpoint);

        Assert.Equal(HttpStatusCode.NoContent, await UnsubscribeAsync(browser));
        Assert.DoesNotContain(browser.Endpoint, await SubscribedEndpointsAsync());
        Assert.Equal(HttpStatusCode.NotFound, await UnsubscribeAsync(browser));
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://localhost/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com.example.in/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc")]
    [InlineData("not a url")]
    public async Task An_endpoint_that_is_not_a_browser_push_service_is_refused(string endpoint)
    {
        using var browser = new FakeBrowser();
        var (status, _) = await PostAdminAsync("/api/admin/web-push/subscriptions", new
        {
            endpoint,
            keys = new { p256dh = browser.P256dh, auth = browser.Auth },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Keys_that_are_not_what_a_browser_sends_are_refused()
    {
        using var browser = new FakeBrowser();
        var (status, _) = await PostAdminAsync("/api/admin/web-push/subscriptions", new
        {
            endpoint = browser.Endpoint,
            keys = new { p256dh = browser.P256dh, auth = "short" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Subscribing_needs_the_passcode()
    {
        using var browser = new FakeBrowser();
        var response = await Client.PostAsync("/api/admin/web-push/subscriptions",
            new StringContent(JsonSerializer.Serialize(browser.Subscription()), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Placing_an_order_pops_up_in_every_subscribed_browser()
    {
        using var browser = new FakeBrowser();
        await SubscribeAsync(browser);

        try
        {
            var (status, order) = await PostAsync("/api/orders", Order());
            Assert.Equal(HttpStatusCode.Created, status);
            var orderId = order.GetProperty("orderId").GetString()!;

            var sent = _fixture.WebPush.SentTo(browser.Endpoint).Single();
            Assert.Equal("aes128gcm", sent.ContentEncoding);
            Assert.Equal("high", sent.Urgency);
            Assert.True(int.Parse(sent.Ttl!) > 0);

            var message = browser.Read(sent.Body);
            Assert.StartsWith($"New order {orderId}", message.GetProperty("title").GetString());
            Assert.Contains("Meenakshi Raghavan", message.GetProperty("body").GetString());
            Assert.Equal("/orders", message.GetProperty("url").GetString());
            Assert.Equal($"order-{orderId}", message.GetProperty("tag").GetString());
            Assert.Equal(orderId, message.GetProperty("data").GetProperty("orderId").GetString());
        }
        finally
        {
            await UnsubscribeAsync(browser);
        }
    }

    [Fact]
    public async Task The_push_is_signed_with_the_VAPID_key_for_that_push_service()
    {
        using var browser = new FakeBrowser();
        await SubscribeAsync(browser);

        try
        {
            await PostAsync("/api/orders", Order());
            var sent = _fixture.WebPush.SentTo(browser.Endpoint).Single();

            // "vapid t=<jwt>, k=<public key>"
            var parts = sent.Authorization!["vapid ".Length..].Split(", ")
                .ToDictionary(p => p[..p.IndexOf('=')], p => p[(p.IndexOf('=') + 1)..]);
            var publicKey = (await GetAdminJsonAsync("/api/admin/web-push/key")).GetProperty("publicKey").GetString()!;
            Assert.Equal(publicKey, parts["k"]);

            var jwt = parts["t"].Split('.');
            using var key = ECDsa.Create(VapidKeys.Parameters(Base64Url.DecodeFromChars(publicKey)));
            Assert.True(key.VerifyData(
                Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64Url.DecodeFromChars(jwt[2]), HashAlgorithmName.SHA256));

            var claims = JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(jwt[1]));
            Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
            Assert.StartsWith("mailto:", claims.GetProperty("sub").GetString());
            Assert.True(claims.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        finally
        {
            await UnsubscribeAsync(browser);
        }
    }

    [Fact]
    public async Task A_bulk_enquiry_pops_up_in_every_subscribed_browser()
    {
        using var browser = new FakeBrowser();
        await SubscribeAsync(browser);

        try
        {
            var (status, enquiry) = await PostAsync("/api/bulk-enquiries", new
            {
                name = "Senthil Kumar",
                organisation = "Tiruppur Knitwear Ltd",
                phone = "9842011997",
                district = "Tiruppur",
                quantity = "180 boxes",
                message = "Gift boxes for factory staff, GST invoice needed.",
            });
            Assert.Equal(HttpStatusCode.Created, status);
            var enquiryId = enquiry.GetProperty("enquiryId").GetString()!;

            var message = browser.Read(_fixture.WebPush.SentTo(browser.Endpoint).Single().Body);
            Assert.Equal($"Bulk enquiry {enquiryId}", message.GetProperty("title").GetString());
            Assert.Equal(
                "Senthil Kumar (Tiruppur Knitwear Ltd) — Tiruppur — 180 boxes",
                message.GetProperty("body").GetString());
            Assert.Equal("/enquiries", message.GetProperty("url").GetString());
            Assert.Equal(enquiryId, message.GetProperty("data").GetProperty("enquiryId").GetString());
        }
        finally
        {
            await UnsubscribeAsync(browser);
        }
    }

    [Fact]
    public async Task A_subscription_the_push_service_has_dropped_is_removed_after_the_next_order()
    {
        using var browser = new FakeBrowser(FakeWebPushService.GoneMarker);
        await SubscribeAsync(browser);

        var (status, _) = await PostAsync("/api/orders", Order());
        Assert.Equal(HttpStatusCode.Created, status);

        Assert.Single(_fixture.WebPush.SentTo(browser.Endpoint));
        Assert.DoesNotContain(browser.Endpoint, await SubscribedEndpointsAsync());
    }

    [Fact]
    public async Task The_test_button_sends_to_every_subscribed_browser()
    {
        using var browser = new FakeBrowser();
        await SubscribeAsync(browser);

        try
        {
            var (status, _) = await PostAdminAsync("/api/admin/web-push/test", new { });
            Assert.Equal(HttpStatusCode.Accepted, status);

            var message = browser.Read(_fixture.WebPush.SentTo(browser.Endpoint).Single().Body);
            Assert.Equal("Notifications are on", message.GetProperty("title").GetString());
        }
        finally
        {
            await UnsubscribeAsync(browser);
        }
    }
}

/// <summary>
/// A browser's side of a subscription: its own key pair and auth secret, and the
/// decryption a browser does on arrival — written from the receiving side of
/// RFC 8291, so it checks the API's encryption rather than repeating it.
/// </summary>
public sealed class FakeBrowser : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _publicKey;
    private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);

    public string Endpoint { get; }
    public string P256dh => Base64Url.EncodeToString(_publicKey);
    public string Auth => Base64Url.EncodeToString(_auth);

    public FakeBrowser(string marker = "")
    {
        var q = _key.ExportParameters(false).Q;
        _publicKey = [0x04, .. q.X!, .. q.Y!];
        Endpoint = $"https://fcm.googleapis.com/fcm/send/{marker}{Guid.NewGuid():n}";
    }

    public object Subscription(string? name = null) => new
    {
        endpoint = Endpoint,
        expirationTime = (object?)null,
        keys = new { p256dh = P256dh, auth = Auth },
        name,
    };

    public JsonElement Read(byte[] body)
    {
        var salt = body[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16));
        var idLength = body[20];
        var serverPublic = body[21..(21 + idLength)];
        var ciphertext = body[(21 + idLength)..];
        Assert.True(ciphertext.Length <= recordSize);

        using var server = ECDiffieHellman.Create(VapidKeys.Parameters(serverPublic));
        var shared = _key.DeriveRawSecretAgreement(server.PublicKey);

        byte[] info = [.. "WebPush: info\0"u8, .. _publicKey, .. serverPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, _auth, info);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var plaintext = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plaintext);

        // Strip the padding back to the last-record delimiter.
        var end = Array.LastIndexOf(plaintext, (byte)0x02);
        Assert.True(end >= 0);
        Assert.All(plaintext[(end + 1)..], b => Assert.Equal(0, b));

        return JsonSerializer.Deserialize<JsonElement>(plaintext.AsSpan(0, end));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Answers like a browser push service: 201 for a delivered message, 410 for an
/// endpoint containing <see cref="GoneMarker"/>, as for a browser that unsubscribed.
/// </summary>
public sealed class FakeWebPushService : HttpMessageHandler
{
    public const string GoneMarker = "Gone";

    public sealed record Sent(
        string Endpoint, byte[] Body, string? ContentEncoding, string? Authorization, string? Ttl, string? Urgency);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyList<Sent> SentTo(string endpoint) => [.. _sent.Where(s => s.Endpoint == endpoint)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = request.RequestUri!.ToString();

        _sent.Enqueue(new Sent(
            endpoint,
            await request.Content!.ReadAsByteArrayAsync(cancellationToken),
            request.Content.Headers.ContentEncoding.FirstOrDefault(),
            request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null,
            request.Headers.TryGetValues("TTL", out var ttl) ? ttl.Single() : null,
            request.Headers.TryGetValues("Urgency", out var urgency) ? urgency.Single() : null));

        return new HttpResponseMessage(endpoint.Contains(GoneMarker) ? HttpStatusCode.Gone : HttpStatusCode.Created);
    }
}
