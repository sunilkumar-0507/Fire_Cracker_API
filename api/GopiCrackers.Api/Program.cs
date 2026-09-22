using System.Text.Json.Serialization;
using GopiCrackers.Api.Data;
using GopiCrackers.Api.Options;
using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using GopiCrackers.Api.Services.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

/* -------------------------------------------------------------------------- */
/* Where Kestrel listens                                                       */
/* -------------------------------------------------------------------------- */

// Loopback unless the host says otherwise. The deployment this targets puts
// nginx in front to terminate TLS and proxy to 127.0.0.1:5000, and nginx is the
// only thing on that machine that should be reachable from outside it — so a
// unit file that forgets ASPNETCORE_URLS binds somewhere a firewall mistake
// cannot expose, rather than wherever the framework's default happens to be.
//
// Nothing that sets a URL loses out: ASPNETCORE_URLS, --urls and the
// applicationUrl in launchSettings.json all arrive as this same key, and any of
// them takes precedence over the default below.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5000");

/* -------------------------------------------------------------------------- */
/* Options                                                                     */
/* -------------------------------------------------------------------------- */

builder.Services
    .AddOptions<StorefrontOptions>()
    .Bind(builder.Configuration.GetSection(StorefrontOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<PaymentOptions>()
    .Bind(builder.Configuration.GetSection($"{StorefrontOptions.SectionName}:Payments"))
    .ValidateOnStart();

/* -------------------------------------------------------------------------- */
/* Services                                                                    */
/* -------------------------------------------------------------------------- */

builder.Services.AddSingleton(TimeProvider.System);

/* -------------------------------------------------------------------------- */
/* Where the data lives                                                        */
/* -------------------------------------------------------------------------- */

// Two backends, one seam. With a connection string every store persists to
// MySQL / MariaDB; without one they persist to the JSON files this project
// started on, and the API behaves exactly as it always has. Nothing downstream
// of this block knows which it got.
var database = new DatabaseOptions();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(database);

if (string.IsNullOrWhiteSpace(database.ConnectionString))
{
    // The conventional place, so a host that only knows how to set connection
    // strings the standard way needs no special instructions.
    database.ConnectionString =
        builder.Configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? string.Empty;
}

builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(database));

// The files are registered whatever happens: they are the fallback backend and,
// when MySQL is in use, the source the seeder fills an empty database from.
builder.Services.AddSingleton<DataFiles>();
builder.Services.AddSingleton<JsonCatalogPersistence>();
builder.Services.AddSingleton<JsonOrderPersistence>();
builder.Services.AddSingleton<JsonInventoryPersistence>();
builder.Services.AddSingleton<JsonAnalyticsPersistence>();

// A factory rather than a scoped context: the stores are singletons, so they
// open a context per operation and close it again rather than holding one open
// for the life of the process.
builder.Services.AddDbContextFactory<GopiCrackersDbContext>(db =>
{
    // With no connection string nothing ever resolves a context — every entry
    // point checks DatabaseOptions.Enabled first, and DatabaseSeeder and
    // DatabaseBootstrapper both refuse outright. The placeholder exists so the
    // registration itself does not have to be conditional, which would make
    // the admin database endpoints unresolvable rather than answerable.
    var connectionString = database.Enabled
        ? database.ConnectionString
        : "Server=127.0.0.1;Database=gopicrackers_not_configured;User ID=none;Password=none";

    db.UseMySql(connectionString, ResolveServerVersion(database, connectionString), mysql =>
    {
        mysql.CommandTimeout(database.CommandTimeoutSeconds);
        // Shared hosting drops idle MySQL connections aggressively, and a
        // dropped socket should cost a retry rather than a customer's order.
        mysql.EnableRetryOnFailure(database.RetryCount);
    });
});

if (database.Enabled)
{
    builder.Services.AddSingleton<ICatalogPersistence, MySqlCatalogPersistence>();
    builder.Services.AddSingleton<IOrderPersistence, MySqlOrderPersistence>();
    builder.Services.AddSingleton<IInventoryPersistence, MySqlInventoryPersistence>();
    builder.Services.AddSingleton<IAnalyticsPersistence, MySqlAnalyticsPersistence>();
}
else
{
    builder.Services.AddSingleton<ICatalogPersistence>(sp => sp.GetRequiredService<JsonCatalogPersistence>());
    builder.Services.AddSingleton<IOrderPersistence>(sp => sp.GetRequiredService<JsonOrderPersistence>());
    builder.Services.AddSingleton<IInventoryPersistence>(sp => sp.GetRequiredService<JsonInventoryPersistence>());
    builder.Services.AddSingleton<IAnalyticsPersistence>(sp => sp.GetRequiredService<JsonAnalyticsPersistence>());
}

builder.Services.AddSingleton<DatabaseSeeder>();
builder.Services.AddSingleton<DatabaseBootstrapper>();

/* -------------------------------------------------------------------------- */
/* Stores                                                                      */
/* -------------------------------------------------------------------------- */

// The catalogue is read into memory once and served from there, so one instance
// answers every request — no round trip and no re-parsing on the hot path.
builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddSingleton<ProductQueryService>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<AnalyticsStore>();
// Reconciles the intake ledger against the order book, so it needs both.
builder.Services.AddSingleton<InventoryStore>();

// Online payment is opt-in, the same way email is. With no merchant
// credentials the disabled gateway takes over: the checkout keeps offering cash
// on delivery and pickup, and nothing ever shows a customer a payment page that
// cannot complete.
builder.Services.AddHttpClient();

var cashfree = builder.Configuration
    .GetSection($"{StorefrontOptions.SectionName}:Payments:Cashfree");

if (string.IsNullOrWhiteSpace(cashfree["AppId"]) || string.IsNullOrWhiteSpace(cashfree["SecretKey"]))
    builder.Services.AddSingleton<IPaymentGateway, DisabledPaymentGateway>();
else
    builder.Services.AddSingleton<IPaymentGateway, CashfreePaymentGateway>();

// Email is opt-in. Without an SMTP host the logging sender takes over and says
// what it would have sent, rather than a deployment quietly promising customers
// a receipt that no relay was ever configured to deliver.
var notifications = builder.Configuration
    .GetSection($"{StorefrontOptions.SectionName}:Notifications:Smtp:Host").Value;

if (string.IsNullOrWhiteSpace(notifications))
    builder.Services.AddSingleton<INotificationSender, LoggingNotificationSender>();
else
    builder.Services.AddSingleton<INotificationSender, SmtpNotificationSender>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        // Absent fields (a banner without a subtitle) stay absent rather than
        // serialising as null, matching the shape of the source JSON.
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

/* -------------------------------------------------------------------------- */
/* Running behind a reverse proxy                                              */
/* -------------------------------------------------------------------------- */

// A VPS deployment puts nginx or Apache in front and lets it terminate TLS, so
// what reaches Kestrel is plain HTTP on localhost. Without this the pipeline
// believes that literally: UseHttpsRedirection below sees an http:// request,
// answers 307 to the https:// address, the proxy forwards it back as http
// again, and every endpoint on the site becomes a redirect loop. Honouring
// X-Forwarded-Proto is what stops that, and X-Forwarded-For is what puts the
// customer's address in the logs instead of the proxy's.
var behindProxy = builder.Configuration
    .GetValue($"{StorefrontOptions.SectionName}:Hosting:BehindReverseProxy", true);

if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
    {
        forwarded.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // The defaults only trust a proxy on a known network, which localhost
        // is not. Clearing them trusts whoever is in front — correct when that
        // is the reverse proxy and Kestrel is bound to 127.0.0.1, which is the
        // deployment this defaults to. Set Storefront:Hosting:BehindReverseProxy
        // to false if Kestrel is ever exposed to the internet directly, or a
        // caller could spoof these headers.
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
    });
}

/* -------------------------------------------------------------------------- */
/* CORS                                                                        */
/* -------------------------------------------------------------------------- */

const string CorsPolicy = "storefront";

// Resolved here rather than inside the policy callback, so the same list feeds
// the policy and the startup log further down. That log line matters more than
// it looks: a CORS failure cannot be seen from the server side at all — the
// request arrives, is answered 200, and the browser throws the response away —
// so on a VPS, knowing which origins the running process actually loaded is
// most of the diagnosis.
var configuredOrigins = builder.Configuration
    .GetSection($"{StorefrontOptions.SectionName}:AllowedOrigins")
    .Get<string[]>();

// Normalised rather than taken literally. The middleware compares the Origin
// header to these strings character for character, so a trailing slash or a
// stray capital is a silent whole-site outage; CorsOrigins explains the rest.
var resolvedOrigins = CorsOrigins.Resolve(
    configuredOrigins ?? [.. new StorefrontOptions().AllowedOrigins]);

// An empty or entirely unusable list would build a policy that allows nothing,
// which is a worse failure than a misconfigured one. The built-in defaults at
// least keep local development working, and startup says loudly that it
// happened rather than leaving a silent deny-everything in place.
var corsOrigins = resolvedOrigins.Allowed.Count > 0
    ? resolvedOrigins.Allowed
    : CorsOrigins.Resolve(new StorefrontOptions().AllowedOrigins).Allowed;

builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins([.. corsOrigins])

    // X-Admin-Passcode is covered by this. It is not a header a browser will
    // send without asking first, so every admin call is preceded by a
    // preflight — see the pipeline notes below for why that ordering matters.
    .AllowAnyHeader()
    .AllowAnyMethod()

    // Without this the admin pays a preflight round trip on every single
    // request. The policy is read from configuration at startup and so only
    // changes on a restart anyway, which is what makes an hour safe to cache.
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));

var app = builder.Build();

/* -------------------------------------------------------------------------- */
/* What this process actually loaded                                           */
/* -------------------------------------------------------------------------- */

// Said out loud at startup, because after this point CORS cannot be diagnosed
// from the server at all: a blocked request is answered normally and logged
// normally, and the only place the failure is visible is a browser console on
// someone else's machine. Before the database work below, so a deployment that
// cannot reach MySQL still leaves this on the record.
app.Logger.LogInformation(
    "CORS: {Count} origin(s) allowed: {Origins}", corsOrigins.Count, string.Join(", ", corsOrigins));

foreach (var ignored in resolvedOrigins.Rejected)
{
    app.Logger.LogWarning(
        "CORS: ignoring {Origin} from {Section}:AllowedOrigins. An origin is scheme://host "
        + "with an optional port and nothing else — no path, no trailing slash "
        + "(for example https://skvpyros.in).", ignored, StorefrontOptions.SectionName);
}

if (resolvedOrigins.Allowed.Count == 0)
{
    app.Logger.LogCritical(
        "CORS: {Section}:AllowedOrigins produced no usable origin, so the built-in development "
        + "defaults are in use and no deployed site can read a response from this API. Set it in "
        + "appsettings.json — and note that an environment variable only overrides the one array "
        + "index it names.", StorefrontOptions.SectionName);
}

/* -------------------------------------------------------------------------- */
/* Database                                                                    */
/* -------------------------------------------------------------------------- */

// Before the first request, and so before anything resolves a store — they read
// their whole contents on construction, and a catalogue loaded from a table
// that does not exist yet is not a recoverable situation. A configured database
// that cannot be reached stops startup here rather than letting the shop write
// today's orders into a file it will never look at again.
await app.Services.GetRequiredService<DatabaseBootstrapper>().InitialiseAsync();

/* -------------------------------------------------------------------------- */
/* Pipeline                                                                    */
/* -------------------------------------------------------------------------- */

// First, so everything downstream — the redirect, CORS, the logs — sees the
// scheme and the caller the proxy actually received rather than the hop from
// the proxy to Kestrel.
if (behindProxy) app.UseForwardedHeaders();

// Routing before CORS, so the policy is evaluated against the endpoint that
// was actually matched — and so the ordering below is explicit rather than
// resting on the UseRouting that WebApplication would otherwise slip in.
app.UseRouting();

// Ahead of the exception handler, the status-code pages and the HTTPS
// redirect. All three produce responses of their own, and a response without
// Access-Control-Allow-Origin is one the browser will not let the site read.
// Three concrete failures this position prevents:
//
//   * A preflight is answered here, 204, instead of being redirected by
//     UseHttpsRedirection. Browsers do not follow redirects on a preflight, so
//     a 307 there fails the request outright rather than being retried — and
//     the admin preflights every single call, because X-Admin-Passcode is not
//     a header a browser sends without asking permission first. That is the
//     difference between the admin panel working and it not working at all.
//
//   * A 404 or a 500 carries the allow header too, so the storefront's fetch()
//     reports the error it actually got. Otherwise every failure in this API,
//     whatever its cause, reaches whoever is debugging it disguised as a CORS
//     error, and they go looking in the wrong place.
//
//   * The header is applied from a Response.OnStarting callback registered on
//     the way in, which is what lets it survive UseExceptionHandler clearing
//     the response before writing its problem details.
app.UseCors(CorsPolicy);

// Unhandled failures become ProblemDetails rather than an HTML error page, so a
// fetch() on the storefront can always parse the body it gets back.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

app.MapControllers();

// A machine-readable index, so the API describes itself at its root.
app.MapGet("/", (IWebHostEnvironment env) => Results.Ok(new
{
    name = "Gopi Crackers REST API",
    description = "Catalogue, cart and checkout endpoints for the Fire Cracker storefront.",
    openapi = env.IsDevelopment() ? "/openapi/v1.json" : null,
    endpoints = new[]
    {
        "GET    /api/health",
        "GET    /api/meta/config",
        "GET    /api/meta/brand",
        "GET    /api/meta/districts",
        "GET    /api/meta/payment-methods",
        "GET    /api/meta/safety-rules",
        "GET    /api/meta/popular-searches",
        "GET    /api/meta/trust-points",
        "GET    /api/meta/fulfilment",
        "GET    /api/meta/availability",
        "GET    /api/meta/order-statuses",
        "GET    /api/products?q=&category=&tag=&min=&max=&rating=&stock=1&sort=&page=&pageSize=",
        "GET    /api/products/sort-options",
        "GET    /api/products/tags",
        "GET    /api/products/price-bounds",
        "GET    /api/products/featured",
        "GET    /api/products/best-sellers",
        "GET    /api/products/new-arrivals",
        "GET    /api/products/{slugOrId}",
        "GET    /api/products/{slugOrId}/related?limit=4",
        "GET    /api/categories?featured=",
        "GET    /api/categories/{slug}",
        "GET    /api/categories/{slug}/products",
        "GET    /api/combos?featured=&sort=",
        "GET    /api/combos/featured",
        "GET    /api/combos/{slugOrId}",
        "GET    /api/offers?featured=&active=",
        "GET    /api/offers/featured",
        "GET    /api/offers/{code}",
        "GET    /api/banners?placement=",
        "GET    /api/banners/{id}",
        "GET    /api/testimonials?limit=&minRating=",
        "GET    /api/faqs?category=",
        "GET    /api/faqs/categories",
        "GET    /api/faqs/{id}",
        "GET    /api/search?q=&limit=&categoryLimit=",
        "GET    /api/shipping",
        "GET    /api/coupons",
        "POST   /api/coupons/validate",
        "POST   /api/cart/quote",
        "GET    /api/bootstrap",
        "GET    /api/admin/summary                      (X-Admin-Passcode)",
        "GET    /api/admin/orders?status=&q=&page=&pageSize=  (X-Admin-Passcode)",
        "GET    /api/admin/orders/statuses              (X-Admin-Passcode)",
        "GET    /api/admin/orders/{orderId}             (X-Admin-Passcode)",
        "PATCH  /api/admin/orders/{orderId}/status      (X-Admin-Passcode)",
        "POST   /api/admin/products                     (X-Admin-Passcode)",
        "PUT    /api/admin/products/{id}                (X-Admin-Passcode)",
        "DELETE /api/admin/products/{id}                (X-Admin-Passcode)",
        "PATCH  /api/admin/products/{id}/active         (X-Admin-Passcode)",
        "PATCH  /api/admin/stock                        (X-Admin-Passcode)",
        "GET    /api/admin/analytics?days=30            (X-Admin-Passcode)",
        "GET    /api/admin/inventory?lowStockAt=20      (X-Admin-Passcode)",
        "GET    /api/admin/inventory/intake             (X-Admin-Passcode)",
        "POST   /api/admin/inventory/intake             (X-Admin-Passcode)",
        "GET    /api/admin/enquiries?status=&q=&page=   (X-Admin-Passcode)",
        "GET    /api/admin/enquiries/statuses           (X-Admin-Passcode)",
        "GET    /api/admin/enquiries/{enquiryId}        (X-Admin-Passcode)",
        "PATCH  /api/admin/enquiries/{enquiryId}/status (X-Admin-Passcode)",
        "GET    /api/admin/messages?page=&pageSize=     (X-Admin-Passcode)",
        "GET    /api/admin/subscribers?q=&page=&pageSize= (X-Admin-Passcode)",
        "DELETE /api/admin/subscribers/{email}          (X-Admin-Passcode)",
        "POST   /api/admin/categories                   (X-Admin-Passcode)",
        "PUT    /api/admin/categories/{id}              (X-Admin-Passcode)",
        "DELETE /api/admin/categories/{id}              (X-Admin-Passcode)",
        "POST   /api/admin/combos                       (X-Admin-Passcode)",
        "PUT    /api/admin/combos/{id}                  (X-Admin-Passcode)",
        "DELETE /api/admin/combos/{id}                  (X-Admin-Passcode)",
        "POST   /api/admin/offers                       (X-Admin-Passcode)",
        "PUT    /api/admin/offers/{id}                  (X-Admin-Passcode)",
        "DELETE /api/admin/offers/{id}                  (X-Admin-Passcode)",
        "POST   /api/orders",
        "GET    /api/orders/{orderId}/track?phone=",
        "POST   /api/analytics/events",
        "GET    /api/analytics/events",
        "POST   /api/bulk-enquiries",
        "GET    /api/bulk-enquiries/{enquiryId}",
        "POST   /api/contact-messages",
        "GET    /api/contact-messages/{messageId}",
        "POST   /api/newsletter/subscribe",
        "POST   /api/newsletter/unsubscribe",
        "GET    /api/payments/config",
        "POST   /api/payments/cashfree/session",
        "POST   /api/payments/cashfree/webhook",
        "GET    /api/payments/status/{orderId}?phone=",
        "GET    /api/admin/database                     (X-Admin-Passcode)",
        "GET    /api/admin/database/backends            (X-Admin-Passcode)",
        "POST   /api/admin/database/migrate             (X-Admin-Passcode)",
        "POST   /api/admin/database/seed                (X-Admin-Passcode)",
        "POST   /api/admin/database/reload              (X-Admin-Passcode)",
        "GET    /api/admin/database/export              (X-Admin-Passcode)",
    },
})).ExcludeFromDescription();

app.Run();

/// <summary>
/// Which dialect Pomelo generates SQL for.
///
/// Auto-detection opens a connection, which is friendly in development and
/// wrong in production: it makes the API wait on a database that may still be
/// coming up, and turns a database being down into a stack trace from deep
/// inside the options builder. Setting Storefront:Database:ServerVersion skips
/// it entirely — and when it is left to auto-detect, the failure is rewritten
/// into a sentence that names the setting to fix.
/// </summary>
static MySqlServerVersion ResolveServerVersion(DatabaseOptions database, string connectionString)
{
    if (!string.IsNullOrWhiteSpace(database.ServerVersion))
        return (MySqlServerVersion)ServerVersion.Parse(database.ServerVersion);

    // Nothing ever opens a context in this state; the pinned version only has
    // to be a version, not the right one.
    if (!database.Enabled) return new MySqlServerVersion(new Version(8, 0, 36));

    try
    {
        // Detected against the server rather than against the schema. The two
        // differ on exactly the deploy this has to survive: a first upload,
        // where the database named in the connection string does not exist yet.
        // MySQL refuses the connection outright in that case, so auto-detecting
        // with the name still attached would fail here — before
        // DatabaseBootstrapper ever gets the chance to create it. A server's
        // version does not depend on which of its schemas you ask.
        return (MySqlServerVersion)ServerVersion.AutoDetect(WithoutDatabase(connectionString));
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            "Could not reach MySQL to find out what version it is. Check " +
            $"Storefront:Database:ConnectionString ({database.Redacted}), or set " +
            "Storefront:Database:ServerVersion (e.g. \"8.0.36-mysql\" or \"10.11.6-mariadb\") " +
            "so startup does not need the database to be awake.", ex);
    }
}

/// <summary>
/// The same connection string with the database name removed, so it addresses
/// the server itself. Returned unchanged if it cannot be parsed — the caller is
/// already inside a try, and a malformed string should surface as the
/// connection error it is rather than as a parse error from here.
/// </summary>
static string WithoutDatabase(string connectionString)
{
    try
    {
        return new MySqlConnector.MySqlConnectionStringBuilder(connectionString)
        {
            Database = string.Empty,
        }.ConnectionString;
    }
    catch (ArgumentException)
    {
        return connectionString;
    }
}

/// <summary>Exposed so an integration test project can boot the API in-process.</summary>
public partial class Program;
