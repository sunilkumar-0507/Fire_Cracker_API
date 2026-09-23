namespace GopiCrackers.Api.Services;

/// <summary>
/// Product photos uploaded from the admin.
///
/// The catalogue's own photography ships inside the two front ends' builds, so
/// adding one used to mean a commit and a redeploy. A photo taken on the shop's
/// phone now lands here instead: a folder on the API's disk, served back under
/// <c>/api/uploads</c> by the static-file handler in <c>Program.cs</c>, and
/// referenced from a product's <c>images</c> by its absolute URL — which both
/// front ends already pass straight through to an <c>&lt;img&gt;</c>.
///
/// The folder is <c>Storefront:Uploads:Path</c>, relative to the content root.
/// A publish replaces the content root, so a deployment points this somewhere
/// that outlives it — see DEPLOYMENT.md.
/// </summary>
public sealed class UploadStore
{
    /// <summary>Well above a phone photo the admin has already shrunk, well below abuse.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    public string Root { get; }

    public UploadStore(IWebHostEnvironment env, IConfiguration config)
    {
        var configured = config["Storefront:Uploads:Path"];
        Root = Path.GetFullPath(Path.Combine(
            env.ContentRootPath,
            string.IsNullOrWhiteSpace(configured) ? "uploads" : configured));

        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Writes the image and returns the name it was stored under, or null when
    /// the bytes are not a JPEG, PNG or WebP.
    ///
    /// The type is read from the file's own first bytes, never from its name or
    /// the Content-Type the browser sent — both are the caller's to choose. The
    /// stored name is generated, so nothing the caller sent reaches the path.
    /// </summary>
    public async Task<string?> SaveAsync(Stream content, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellation);

        var extension = ExtensionFor(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (extension is null) return null;

        var name = $"{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():n}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(Root, name), buffer.ToArray(), cancellation);
        return name;
    }

    private static string? ExtensionFor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return ".jpg";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ".png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ".webp";
        return null;
    }
}
