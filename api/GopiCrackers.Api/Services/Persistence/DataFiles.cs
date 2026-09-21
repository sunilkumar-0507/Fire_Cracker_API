namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// Where the JSON lives.
///
/// Pulled out of <see cref="CatalogStore"/> so the order journal, the stock
/// ledger and the analytics log stop resolving their paths through the
/// catalogue — which used to mean the catalogue had to be constructed first,
/// for no reason other than that it happened to know the folder.
///
/// The folder is still needed when the API runs on MySQL: it is where the
/// seeder reads the shop's opening catalogue from. So a missing folder is only
/// fatal when the files are the live backend, and <see cref="Available"/> is
/// how the caller asks which it is.
/// </summary>
public sealed class DataFiles
{
    private readonly string? _root;
    private readonly string[] _looked;

    public DataFiles(IWebHostEnvironment env, IConfiguration config)
    {
        _looked = CandidatesFor(env, config);
        _root = _looked.FirstOrDefault(c => File.Exists(System.IO.Path.Combine(c, "products.json")));
    }

    /// <summary>True when a catalogue folder was found.</summary>
    public bool Available => _root is not null;

    /// <summary>The folder, or a throw naming everywhere it was looked for.</summary>
    public string Root => _root ?? throw new DirectoryNotFoundException(
        "Could not locate the catalogue JSON. Looked in: " + string.Join(", ", _looked) +
        ". Set Catalog:DataPath to override.");

    /// <summary>The folder, or null. For callers that can do without it.</summary>
    public string? RootOrNull => _root;

    public string Path(string file) => System.IO.Path.Combine(Root, file);

    /// <summary>
    /// Everywhere the catalogue could be, in the order it is looked for.
    ///
    /// A <c>Catalog:DataPath</c> override first — development points it at the
    /// repo's own <c>src/data</c> so an admin edit lands in the files the
    /// storefront is built from. Otherwise build output, then the project
    /// folder, then the repository two levels up.
    /// </summary>
    private static string[] CandidatesFor(IWebHostEnvironment env, IConfiguration config)
    {
        var candidates = new List<string>(4);

        var configured = config["Catalog:DataPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(System.IO.Path.GetFullPath(
                System.IO.Path.Combine(env.ContentRootPath, configured)));
        }

        candidates.Add(System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Data")));
        candidates.Add(System.IO.Path.GetFullPath(
            System.IO.Path.Combine(env.ContentRootPath, "Data")));
        candidates.Add(System.IO.Path.GetFullPath(
            System.IO.Path.Combine(env.ContentRootPath, "..", "..", "src", "data")));

        return [.. candidates];
    }

    /// <summary>
    /// Writes through a temp file and swaps it into place, so an interrupted
    /// save leaves the previous contents intact rather than a truncated file.
    /// </summary>
    public void WriteAtomically(string file, string contents)
    {
        var target = Path(file);
        var temp = target + ".tmp";

        File.WriteAllText(temp, contents);
        File.Move(temp, target, overwrite: true);
    }
}
