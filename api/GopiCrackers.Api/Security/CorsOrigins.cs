namespace GopiCrackers.Api.Security;

/// <summary>
/// Turns the strings in <c>Storefront:AllowedOrigins</c> into the exact forms
/// the CORS middleware compares against.
///
/// CORS matching is a string comparison, not a URL comparison: ASP.NET Core
/// keeps the configured origins in a list and checks whether the browser's
/// <c>Origin</c> header is in it, character for character. A browser always
/// sends scheme and host lowercased, with no path and no trailing slash, and
/// with the port left off when it is the default for the scheme. So every one
/// of these is a value that looks right in a config file and never matches:
///
/// <list type="bullet">
///   <item><c>https://skvpyros.in/</c> — trailing slash</item>
///   <item><c>https://skvpyros.in/api</c> — the API's URL rather than the site's origin</item>
///   <item><c>https://SKVPyros.in</c> — capitals</item>
///   <item><c>https://skvpyros.in:443</c> — the default port, spelled out</item>
///   <item><c>" https://skvpyros.in"</c> — a space carried in from a copy-paste</item>
/// </list>
///
/// None of them produce an error anywhere. The API answers 200 with the right
/// body, the browser discards it unread, and the only symptom is an empty page
/// on a site whose server logs look perfect. Normalising here is what turns
/// "I typed the domain in and it still does not work" into it working.
///
/// What this does not do is widen anything. An entry that cannot be read as an
/// http(s) origin is dropped and reported by name rather than guessed at, and
/// <c>*</c> is refused outright — this API takes orders and holds an admin
/// surface, and a wildcard is never the answer on it.
/// </summary>
internal static class CorsOrigins
{
    /// <summary>
    /// The usable origins, in the order they were configured, alongside the
    /// entries that had to be dropped so startup can name them in the log.
    /// </summary>
    internal sealed record Result(IReadOnlyList<string> Allowed, IReadOnlyList<string> Rejected);

    internal static Result Resolve(IEnumerable<string?>? configured)
    {
        var allowed = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in configured ?? [])
        {
            // A blank line is padding, not a mistake worth reporting.
            if (string.IsNullOrWhiteSpace(entry)) continue;

            if (!TryNormalise(entry, out var origin))
            {
                rejected.Add(entry.Trim());
                continue;
            }

            // Two spellings of one origin — "https://skvpyros.in" and
            // "https://skvpyros.in/" — are one entry once normalised.
            if (seen.Add(origin)) allowed.Add(origin);
        }

        return new Result(allowed, rejected);
    }

    /// <summary>
    /// The origin a browser would send for <paramref name="value"/>, or false
    /// if it does not describe an http(s) origin at all.
    /// </summary>
    internal static bool TryNormalise(string? value, out string origin)
    {
        origin = string.Empty;

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        // Deliberately unsupported. WithOrigins("*") is not the same thing as
        // AllowAnyOrigin() and behaves in ways nobody expects; more to the
        // point, nothing about this API wants either of them.
        if (trimmed == "*") return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;

        // Rebuilt from the parsed parts rather than trimmed as text: a path, a
        // query and a fragment fall away for free, Uri has already lowercased
        // the scheme and the host, and IsDefaultPort is what stops :443 being
        // spelled out on an https origin the browser will send without it.
        var host = uri.Host;
        if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('['))
            host = $"[{host}]";

        origin = uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port}";

        return true;
    }
}
