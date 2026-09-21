using GopiCrackers.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace GopiCrackers.Api.Security;

/// <summary>
/// Requires the shared admin passcode on the <c>X-Admin-Passcode</c> header.
///
/// This is a lock on a door, not a security system. There is one passcode for
/// everyone, it is sent in plain text on every request, there are no accounts,
/// no sessions, no audit trail and no rate limit. It keeps the admin endpoints
/// out of casual reach while the shop runs on a local network — it is not
/// enough to expose this API to the internet with. Put real authentication in
/// front of it before that: this attribute is the seam to replace.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AdminOnlyAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public const string HeaderName = "X-Admin-Passcode";

    /// <summary>
    /// Below <c>ModelStateInvalidFilter</c>'s -2000, so the passcode is checked
    /// before the body is validated. Otherwise an anonymous caller posting a bad
    /// payload gets a 400 listing every field rule instead of a flat 401.
    /// </summary>
    public int Order => -3000;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<StorefrontOptions>>().Value;

        var expected = options.Admin.Passcode;
        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(expected))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Admin is not configured",
                Detail = $"No passcode is set. Add {StorefrontOptions.SectionName}:Admin:Passcode to configuration.",
            })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        // Fixed-time compare so a wrong passcode cannot be narrowed down by
        // timing the response, cheap as that attack would be here.
        if (!FixedTimeEquals(supplied, expected))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Admin passcode required",
                Detail = $"Send the passcode on the {HeaderName} header.",
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        await next();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(a);
        var right = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}
