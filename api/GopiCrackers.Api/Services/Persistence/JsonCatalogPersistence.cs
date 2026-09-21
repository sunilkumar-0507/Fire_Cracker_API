using System.Text.Json;
using GopiCrackers.Api.Models;

namespace GopiCrackers.Api.Services.Persistence;

/// <summary>
/// The catalogue as seven JSON files — how this project has always stored it,
/// and what it falls back to when no database is configured.
///
/// Lifted out of <c>CatalogStore</c> unchanged: the same reader, the same
/// temp-file-then-swap writer, the same rule about which files may be empty.
/// The only difference is that it now sits behind an interface, so the store
/// no longer knows it is talking to a disk.
/// </summary>
public sealed class JsonCatalogPersistence(DataFiles files, ILogger<JsonCatalogPersistence> logger)
    : ICatalogPersistence
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Backend => "files";

    /// <summary>The file each part is kept in.</summary>
    public static string FileFor(CatalogPart part) => part switch
    {
        CatalogPart.Products => "products.json",
        CatalogPart.Categories => "categories.json",
        CatalogPart.Combos => "combos.json",
        CatalogPart.Offers => "offers.json",
        CatalogPart.Banners => "banners.json",
        CatalogPart.Testimonials => "testimonials.json",
        CatalogPart.Faqs => "faq.json",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Not a catalogue part"),
    };

    public CatalogData Load()
    {
        logger.LogInformation("Loading catalogue from {DataPath}", files.Root);

        return new CatalogData(
            Products: Read<Product>(CatalogPart.Products, required: true),
            Categories: Read<Category>(CatalogPart.Categories, required: true),
            Combos: Read<Combo>(CatalogPart.Combos),
            Offers: Read<Offer>(CatalogPart.Offers),
            Banners: Read<Banner>(CatalogPart.Banners),
            Testimonials: Read<Testimonial>(CatalogPart.Testimonials),
            Faqs: Read<Faq>(CatalogPart.Faqs));
    }

    public void Save(CatalogPart part, CatalogSnapshot snapshot)
    {
        object payload = part switch
        {
            CatalogPart.Products => snapshot.Products,
            // ProductCount is recomputed on load, but writing the current figure
            // keeps the file honest for anything else reading it.
            CatalogPart.Categories => snapshot.Categories,
            CatalogPart.Combos => snapshot.Combos,
            CatalogPart.Offers => snapshot.Offers,
            CatalogPart.Banners => snapshot.Banners,
            CatalogPart.Testimonials => snapshot.Testimonials,
            CatalogPart.Faqs => snapshot.Faqs,
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Not a catalogue part"),
        };

        var file = FileFor(part);
        files.WriteAtomically(
            file,
            JsonSerializer.Serialize(payload, PersistenceJson.Options) + Environment.NewLine);

        logger.LogInformation("Wrote {File}", files.Path(file));
    }

    /// <summary>
    /// Reads one catalogue file.
    ///
    /// <paramref name="required"/> separates "this file cannot be empty" from
    /// "this file may legitimately have nothing in it yet". A shop with no
    /// products or no categories is a broken deployment and should fail loudly
    /// at startup. A shop with no testimonials is simply a shop that has not
    /// collected any — the same is true of banners, offers and combos before
    /// the first campaign is set up — and refusing to boot over that would
    /// leave the shopkeeper unable to add the first one.
    /// </summary>
    private List<T> Read<T>(CatalogPart part, bool required = false)
    {
        var file = FileFor(part);
        var full = files.Path(file);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Catalogue file missing: {full}", full);

        using var stream = File.OpenRead(full);
        var parsed = JsonSerializer.Deserialize<List<T>>(stream, ReadOptions)
            ?? throw new InvalidDataException($"{file} did not deserialise to a list");

        if (required && parsed.Count == 0)
            throw new InvalidDataException($"{file} is empty");

        return parsed;
    }
}
