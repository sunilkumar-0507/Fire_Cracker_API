using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GopiCrackers.Api.Services;

/// <summary>
/// The serializer used when writing catalogue and order JSON back to disk.
///
/// A response and a source file want different shapes. <c>Offer</c> exposes
/// <c>resolvedEndsAt</c> and <c>expired</c>, and <c>Order</c> exposes <c>ok</c> —
/// conveniences computed from "now" that belong in an API response but have no
/// business being frozen into a file. Persisting them would grow the file on
/// every save and record a point-in-time judgement as if it were source data.
///
/// Rather than annotate each one and hope the next person remembers, this drops
/// every property that is not a constructor parameter of the record. For the
/// positional records the catalogue is built from, that is exactly the line
/// between "data the file owns" and "something worked out from it".
/// </summary>
public static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The catalogue is full of ₹, ½, ″ and Tamil category names. Without
        // this the writer escapes them to \uXXXX and the files stop being
        // readable, reviewable or greppable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // An absent field stays absent, matching the hand-written source files
        // and the responses the API sends. Without this a combo line with no
        // product behind it (“GST invoice + delivery challan”) gains a
        // "slug": null on every save, so opening the admin and pressing save
        // rewrites files nobody meant to change. Reading is unaffected: an
        // omitted field deserialises back to null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { DropComputedProperties },
        },
    };

    private static void DropComputedProperties(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object) return;

        // Only applies to types built through a parameterised constructor —
        // i.e. the positional records. Anything else is left alone.
        var parameters = type.Type
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?.GetParameters();

        if (parameters is null || parameters.Length == 0) return;

        var owned = parameters
            .Select(p => p.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = type.Properties.Count - 1; i >= 0; i--)
        {
            if (!owned.Contains(type.Properties[i].Name))
                type.Properties.RemoveAt(i);
        }
    }
}
