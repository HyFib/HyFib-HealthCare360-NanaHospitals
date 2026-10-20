using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;

/// <summary>
/// Assembles an ABDM health record: a FHIR document (<c>Bundle</c> of type <c>document</c>, NRCeS DocumentBundle) whose
/// first entry is the Composition. Each resource is an entry whose <c>fullUrl</c> is <c>urn:uuid:{id}</c>, as in the
/// implementation guide's examples, and claims its NRCeS profile in <c>meta.profile</c>. A document must stand on its
/// own, so <see cref="Build"/> checks that every reference points at an entry, or names its target by display or
/// identifier only.
/// </summary>
internal sealed class DocumentBundleBuilder
{
    private readonly List<(string FullUrl, R4.Resource Resource)> _entries = [];

    /// <summary>The <c>fullUrl</c> of the entry holding the resource with this id.</summary>
    public static string FullUrl(Guid id) => "urn:uuid:" + FhirIds.Of(id);

    /// <summary>
    /// A reference to the entry holding the resource with this id; <paramref name="type"/> names the resource type where a
    /// profile tells entries apart by it (a prescription's section entries).
    /// </summary>
    public static R4.ResourceReference Reference(Guid id, string? display = null, string? type = null) =>
        new(FullUrl(id), FhirElements.Clean(display)) { Type = type };

    /// <summary>
    /// A stable id for a resource HC360 derives from a record rather than storing (the coded diagnoses of a discharge
    /// summary, its care plan): the same record and name always give the same id, and different names do not collide.
    /// An RFC 9562 version 8 UUID made from the SHA-256 of the record's id and the name.
    /// </summary>
    public static Guid DerivedId(Guid source, string name)
    {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        source.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));
        var hash = SHA256.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>Adds the resource as an entry: its id becomes <paramref name="id"/> and it claims <paramref name="profile"/>.</summary>
    public DocumentBundleBuilder Add(Guid id, R4.Resource resource, string profile)
    {
        resource.Id = FhirIds.Of(id);
        resource.Meta ??= new R4.Meta();
        resource.Meta.Profile = [profile];
        _entries.Add((FullUrl(id), resource));
        return this;
    }

    /// <summary>
    /// The document. <paramref name="documentId"/> identifies this copy of it: each export is a new document, with a new
    /// identifier, assembled at <paramref name="now"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The first entry is not the Composition, two entries have the same id, or a reference leaves the document.
    /// </exception>
    public R4.Bundle Build(Guid documentId, DateTimeOffset now)
    {
        if (_entries.Count == 0 || _entries[0].Resource is not R4.Composition)
        {
            throw new InvalidOperationException("A document starts with its Composition.");
        }

        var fullUrls = _entries.Select(e => e.FullUrl).ToHashSet(StringComparer.Ordinal);
        if (fullUrls.Count != _entries.Count)
        {
            throw new InvalidOperationException("Two entries of the document have the same id.");
        }

        foreach (var (_, resource) in _entries)
        {
            foreach (var reference in References(resource))
            {
                var target = reference.Reference;
                if (target is null ? reference.Display is null && reference.Identifier is null : !fullUrls.Contains(target))
                {
                    throw new InvalidOperationException($"{resource.TypeName} refers to {target ?? "nothing"}, which is not in the document.");
                }
            }
        }

        var timestamp = FhirTime.ToMilliseconds(now);
        return new R4.Bundle
        {
            Id = FhirIds.Of(documentId),
            Meta = new R4.Meta
            {
                // The NRCeS DocumentBundle requires a version; each export is a new document, so this is its first.
                VersionId = "1",
                LastUpdated = timestamp,
                Profile = [AbdmProfiles.DocumentBundle],
            },
            Identifier = new R4.Identifier(FhirSystems.Uri, FullUrl(documentId)),
            Type = R4.Bundle.BundleType.Document,
            Timestamp = timestamp,
            Entry = [.. _entries.Select(e => new R4.Bundle.EntryComponent { FullUrl = e.FullUrl, Resource = e.Resource })],
        };
    }

    /// <summary>Every reference in a resource, however deeply nested.</summary>
    internal static IEnumerable<R4.ResourceReference> References(R4.Base element)
    {
        foreach (var child in element.EnumerateElements())
        {
            IEnumerable<R4.Base> values = child.Value switch
            {
                R4.Base single => [single],
                IEnumerable<R4.Base> many => many,
                _ => [],
            };
            foreach (var value in values)
            {
                if (value is R4.ResourceReference reference)
                {
                    yield return reference;
                }

                foreach (var nested in References(value))
                {
                    yield return nested;
                }
            }
        }
    }
}
