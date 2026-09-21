using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Walks the whole API surface and reports what answered.
///
/// Kept apart from the tests that call it because it is run twice against two
/// different backends: once on the JSON files, once on MySQL. The endpoints are
/// supposed to be identical either way — that is the whole premise of the
/// persistence seam — so the check that proves it has to be the same check,
/// not two that merely look alike.
/// </summary>
internal static class EndpointAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The number every order below is placed with; tracking needs it back.</summary>
    public const string OrderPhone = "9842011994";

    /* ---------------------------------------------------------------------- */
    /* The two lists                                                           */
    /* ---------------------------------------------------------------------- */

    /// <summary>What the API says it serves, parsed out of the root index.</summary>
    public static async Task<IReadOnlyList<Documented>> DocumentedAsync(HttpClient client)
    {
        var index = await client.GetFromJsonAsync<JsonElement>("/", Json);

        return [.. index.GetProperty("endpoints").EnumerateArray()
            .Select(line => line.GetString()!)
            .Select(Documented.Parse)];
    }

    /// <summary>
    /// What it actually serves. Read from the route table rather than by
    /// probing, so an endpoint that exists but throws is still counted as
    /// present — the difference between "missing" and "broken" is worth
    /// keeping, and the request walks below draw it.
    /// </summary>
    public static IReadOnlyList<(string Method, string Template)> Routed(IServiceProvider services)
    {
        var source = services.GetRequiredService<EndpointDataSource>();

        return
        [
            .. from endpoint in source.Endpoints.OfType<RouteEndpoint>()
               let template = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/')
               where !Ignored(template)
               from method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                              ?? (IReadOnlyList<string>)["GET"]
               select (method, template)
        ];
    }

    /// <summary>
    /// Infrastructure rather than API surface: the self-describing index at the
    /// root, and the OpenAPI document that is only mapped in development.
    /// Neither belongs in a list of shop endpoints.
    /// </summary>
    private static bool Ignored(string template) =>
        template == "/" || template.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);

    /* ---------------------------------------------------------------------- */
    /* The walks                                                               */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Fetches every documented GET with real parameters substituted in, and
    /// returns the ones that did not answer 200.
    ///
    /// The whole list rather than the first failure: on a deploy you want to
    /// know everything that is broken, not to fix them one run at a time.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FailingGetsAsync(HttpClient client, string passcode)
    {
        var parameters = await ResolveParametersAsync(client);
        var failures = new List<string>();

        foreach (var endpoint in (await DocumentedAsync(client)).Where(d => d.Method == "GET"))
        {
            var url = endpoint.Concrete(parameters);

            // The admin passcode goes on every request, not just the admin
            // ones: a public endpoint ignores the header, and sending it
            // unconditionally keeps this loop from having to know which is
            // which. Whether the admin endpoints are closed to a caller without
            // it is a separate walk.
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Admin-Passcode", passcode);

            var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.OK) continue;

            failures.Add($"GET {url} -> {(int)response.StatusCode} {response.StatusCode}: " +
                         Excerpt(await response.Content.ReadAsStringAsync()));
        }

        return failures;
    }

    /// <summary>
    /// Calls every endpoint marked <c>(X-Admin-Passcode)</c> without one, and
    /// returns the ones that did not refuse.
    ///
    /// This doubles as a routing check that is safe to run against writes: a
    /// 401 can only come from the filter on a route that exists, so the whole
    /// admin surface — the DELETEs included — is proven reachable without a
    /// single row being written or deleted.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UnprotectedAdminAsync(HttpClient client)
    {
        var parameters = await ResolveParametersAsync(client);
        var failures = new List<string>();

        foreach (var endpoint in (await DocumentedAsync(client)).Where(d => d.AdminOnly))
        {
            var url = endpoint.Concrete(parameters);
            var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), url);

            // Writes are sent an empty body deliberately. The passcode filter
            // runs before model validation, so an anonymous caller must get 401
            // and never the 400 that would mean the body was looked at first.
            if (endpoint.Method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized) continue;

            failures.Add($"{endpoint.Method} {url} -> {(int)response.StatusCode} " +
                         $"{response.StatusCode} (expected 401)");
        }

        return failures;
    }

    /* ---------------------------------------------------------------------- */
    /* Real values for the route parameters                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// Builds a substitution for every <c>{placeholder}</c> in the documented
    /// list, from data the API itself hands back.
    ///
    /// The order-shaped ones cannot be read off the catalogue, so this places a
    /// real order, files a real enquiry and sends a real contact message first.
    /// That is deliberate on the MySQL run: it means the walk that follows is
    /// reading rows this process wrote through the provider, not just rows the
    /// seeder imported.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveParametersAsync(HttpClient client)
    {
        var products = await GetAsync(client, "/api/products?pageSize=1");
        var categories = await GetAsync(client, "/api/categories");
        var combos = await GetAsync(client, "/api/combos");
        var offers = await GetAsync(client, "/api/offers");
        var banners = await GetAsync(client, "/api/banners");
        var faqs = await GetAsync(client, "/api/faqs");

        var product = First(products, "id");

        var order = await PostAsync(client, "/api/orders", new
        {
            name = "Meenakshi Raghavan",
            phone = OrderPhone,
            email = "meenakshi@example.in",
            address = "12 Second Cross Street, Adyar",
            city = "Chennai",
            district = "Chennai",
            pincode = "600020",
            payment = "upi",
            items = new[] { new { id = product, qty = 1 } },
        });

        var enquiry = await PostAsync(client, "/api/bulk-enquiries", new
        {
            name = "Lakshmi Narayanan",
            organisation = "Sri Meenakshi Trust",
            phone = "9840012345",
            email = "trust@example.in",
            district = "Madurai",
            quantity = "400 boxes",
            message = "Temple festival, first week of November.",
        });

        var message = await PostAsync(client, "/api/contact-messages", new
        {
            name = "Anand Kumar",
            email = "anand@example.in",
            phone = "9840055555",
            subject = "Delivery to Kerala",
            message = "Do you deliver to Palakkad before Diwali?",
        });

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["slugOrId"] = First(products, "slug", "id"),
            ["slug"] = First(categories, "slug", "id"),
            ["code"] = First(offers, "code", "id"),
            ["id"] = First(banners, "id"),
            ["orderId"] = order.GetProperty("orderId").GetString()!,
            ["enquiryId"] = enquiry.GetProperty("enquiryId").GetString()!,
            ["messageId"] = message.GetProperty("messageId").GetString()!,
            ["email"] = "nobody@example.in",

            // Scoped entries, for the routes whose placeholder name collides
            // with another list's. {id} is a banner on /api/banners/{id} and an
            // FAQ on /api/faqs/{id}; {slugOrId} is a product on one route and a
            // combo on another.
            [Documented.Scoped("/api/faqs/{id}", "id")] = First(faqs, "id"),
            [Documented.Scoped("/api/combos/{slugOrId}", "slugOrId")] = First(combos, "slug", "id"),
        };
    }

    /* ---------------------------------------------------------------------- */

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"GET {url} returned {(int)response.StatusCode} while setting the audit up. " +
                $"Body: {Excerpt(await response.Content.ReadAsStringAsync())}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string url, object payload)
    {
        var response = await client.PostAsJsonAsync(url, payload, Json);

        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"POST {url} returned {(int)response.StatusCode} while setting the audit up. " +
                $"Body: {Excerpt(await response.Content.ReadAsStringAsync())}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>
    /// Pulls the first usable identifier out of a list response, whether the
    /// endpoint returns a bare array or a page wrapped in <c>items</c>.
    /// </summary>
    private static string First(JsonElement response, params string[] names)
    {
        var array = response.ValueKind == JsonValueKind.Array
            ? response
            : response.GetProperty("items");

        var item = array.EnumerateArray().First();

        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind is JsonValueKind.String) return value.GetString()!;
            if (value.ValueKind is JsonValueKind.Number) return value.ToString();
        }

        throw new InvalidOperationException($"No {string.Join(" or ", names)} on {item}");
    }

    private static string Excerpt(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";

    /* ---------------------------------------------------------------------- */

    /// <summary>One line of the index at <c>GET /</c>, taken apart.</summary>
    internal sealed record Documented(string Method, string Template, bool AdminOnly)
    {
        /// <summary>A parameter name qualified by the route it appears on.</summary>
        public static string Scoped(string template, string name) => $"{template}#{name}";

        public static Documented Parse(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = parts[1];

            var split = path.IndexOf('?');
            var template = split < 0 ? path : path[..split];

            return new Documented(parts[0], template,
                line.Contains("X-Admin-Passcode", StringComparison.Ordinal));
        }

        /// <summary>
        /// The template with real values in place of its placeholders.
        ///
        /// The documented query strings are shown with empty values as a hint at
        /// what is accepted ("?q=&amp;category=&amp;tag="), and an endpoint is
        /// entitled to treat "" differently from absent — so they are dropped
        /// rather than sent, except where the parameter is not optional.
        /// </summary>
        public string Concrete(IReadOnlyDictionary<string, string> parameters)
        {
            var url = Template;

            foreach (var name in Placeholders(Template))
            {
                var value = parameters.TryGetValue(Scoped(Template, name), out var scoped)
                    ? scoped
                    : parameters[name];

                url = url.Replace($"{{{name}}}", Uri.EscapeDataString(value));
            }

            var required = RequiredQuery(Template);
            return required is null ? url : $"{url}?{required}";
        }

        /// <summary>
        /// The query string a route cannot answer without, or null when every
        /// parameter it takes is a filter it is happy to do without.
        ///
        /// Tracking and payment status identify the caller by phone number
        /// rather than by session, so the number is part of the request and not
        /// an optional filter on it. Search is the same: without a term there
        /// is no search to run, and a 400 saying so is the endpoint working.
        /// </summary>
        private static string? RequiredQuery(string template) => template switch
        {
            _ when template.EndsWith("/track", StringComparison.Ordinal) => $"phone={OrderPhone}",
            _ when template.StartsWith("/api/payments/status/", StringComparison.Ordinal) => $"phone={OrderPhone}",
            "/api/search" => "q=flower",
            _ => null,
        };

        private static IEnumerable<string> Placeholders(string template) =>
            template.Split('/')
                .Where(s => s.StartsWith('{') && s.EndsWith('}'))
                .Select(s => s[1..^1]);
    }
}
