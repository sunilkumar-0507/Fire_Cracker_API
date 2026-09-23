using GopiCrackers.Api.Security;
using GopiCrackers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GopiCrackers.Api.Controllers;

/// <summary>
/// Photo uploads for the product editor. See <see cref="UploadStore"/>.
/// </summary>
[ApiController]
[AdminOnly]
[Route("api/admin/uploads")]
public sealed class AdminUploadsController(UploadStore uploads) : ControllerBase
{
    public sealed record UploadResult(string Url, string FileName);

    /// <summary>
    /// Takes one image as the <c>file</c> field of a multipart form.
    ///
    /// The form is read inside the action rather than bound as a parameter, so
    /// the passcode filter runs first: a bound <c>IFormFile</c> would have MVC
    /// reject a non-multipart request with 415 before the caller had been asked
    /// who they were.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(UploadStore.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadStore.MaxBytes + 64 * 1024)]
    [ProducesResponseType<UploadResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadResult>> Upload(CancellationToken cancellation)
    {
        if (!Request.HasFormContentType)
            return Problem("Send the photo as multipart/form-data, in a field named 'file'.", statusCode: 400);

        var form = await Request.ReadFormAsync(cancellation);
        var file = form.Files.GetFile("file");

        if (file is null || file.Length == 0)
            return Problem("No photo was attached.", statusCode: 400);

        if (file.Length > UploadStore.MaxBytes)
            return Problem($"That photo is over {UploadStore.MaxBytes / (1024 * 1024)} MB.", statusCode: 400);

        await using var stream = file.OpenReadStream();
        var name = await uploads.SaveAsync(stream, cancellation);

        if (name is null)
            return Problem("That file is not a JPEG, PNG or WebP photo.", statusCode: 400);

        // Absolute, because the storefront lives on another domain: a product's
        // image has to resolve from skvpyros.in as well as from here. Behind
        // the proxy the scheme is the one the customer used, courtesy of the
        // forwarded headers.
        var url = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/uploads/{name}";
        return Created(url, new UploadResult(url, name));
    }
}
