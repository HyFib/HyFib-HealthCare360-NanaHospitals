using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// Assembles an <see cref="AbdmDocument"/> into the IG's DocumentBundle: a FHIR <c>document</c> Bundle whose first entry
/// is the Composition, followed by every resource it refers to, each with a <c>urn:uuid:</c> full URL. The document is
/// self-contained, so every reference is resolved inside it: a reference to a resource that is not in the document keeps
/// only its display (an encounter of the same stay or visit points to the document's encounter instead).
/// <para>
/// Ids are derived from HC360's ids (a SHA-256 based UUID, RFC 9562 version 8), so exporting the same record again gives
/// the same ids, and a patient or doctor keeps one id across documents. The Bundle's identifier changes with the
/// document's version (the latest change to anything in it).
/// </para>
/// </summary>
internal static class AbdmDocumentBuilder
{
    public static R4.Bundle Build(AbdmDocument document, DateTimeOffset now)
    {
        var resources = Resources(document);
        var compositionKey = $"Composition/{document.Type}/{FhirIds.Of(document.SourceId)}";
        var ids = resources.ToDictionary(Key, r => Uuid(Key(r)), StringComparer.Ordinal);
        ids[compositionKey] = Uuid(compositionKey);

        var composition = Composition(document, ids[compositionKey]);
        var encounterKey = document.Encounter is { } encounter ? Key(encounter) : null;
        var encounterUrn = encounterKey is null ? null : Urn(ids[encounterKey]);
        foreach (var resource in resources.Prepend(composition))
        {
            // The encounter's own links (the visit a consultation is part of) are not redirected to itself.
            Resolve(resource, ids, Key(resource) == encounterKey ? null : encounterUrn);
        }

        resources = Reachable(composition, resources, ids);
        foreach (var resource in resources)
        {
            resource.Id = ids[Key(resource)];
            if (resource is R4.DomainResource domain && domain.Text is null && AbdmNarrative.Summary(domain) is { } summary)
            {
                domain.Text = AbdmNarrative.Generated([summary]);
            }
        }

        var bundleId = Uuid($"Bundle/{document.Type}/{FhirIds.Of(document.SourceId)}/{document.VersionId}");
        var bundle = new R4.Bundle
        {
            Id = bundleId,
            Meta = new R4.Meta
            {
                VersionId = document.VersionId,
                LastUpdated = FhirTime.ToMilliseconds(document.LastUpdated),
                Profile = [AbdmProfiles.DocumentBundle],
                Security = [FhirElements.Coding(AbdmSystems.Confidentiality, "V", "very restricted")],
            },
            Identifier = new R4.Identifier(AbdmSystems.Uri, Urn(bundleId)),
            Type = R4.Bundle.BundleType.Document,
            Timestamp = FhirTime.ToMilliseconds(now),
        };

        bundle.Entry.Add(new R4.Bundle.EntryComponent { FullUrl = Urn(composition.Id!), Resource = composition });
        bundle.Entry.AddRange(resources.Select(r => new R4.Bundle.EntryComponent { FullUrl = Urn(r.Id!), Resource = r }));
        return bundle;
    }

    /// <summary>
    /// A UUID derived from a name: the first 16 bytes of its SHA-256 hash with the RFC 9562 version (8, custom) and
    /// variant bits set. The same name always gives the same UUID.
    /// </summary>
    public static string Uuid(string name)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes("hc360:abdm:" + name), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    public static string Urn(string uuid) => "urn:uuid:" + uuid;

    /// <summary>The resources of the document in order, each once: patient, encounter, people, sections, supporting.</summary>
    private static List<R4.Resource> Resources(AbdmDocument document)
    {
        var resources = new List<R4.Resource>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(R4.Resource? resource)
        {
            if (resource is not null && keys.Add(Key(resource)))
            {
                resources.Add(resource);
            }
        }

        Add(document.Patient);
        Add(document.Encounter);
        Add(document.Author);
        Add(document.Attester);
        Add(document.Custodian);
        foreach (var entry in document.Sections.SelectMany(s => s.Entries))
        {
            Add(entry);
        }

        foreach (var resource in document.Supporting)
        {
            Add(resource);
        }

        return resources;
    }

    private static R4.Composition Composition(AbdmDocument document, string id)
    {
        var composition = new R4.Composition
        {
            Id = id,
            Meta = new R4.Meta
            {
                VersionId = document.VersionId,
                LastUpdated = FhirTime.ToMilliseconds(document.LastUpdated),
                Profile = [document.Profile],
            },
            Identifier = new R4.Identifier(AbdmSystems.Uri, Urn(id)),
            Status = document.Status,
            Type = document.DocumentType.Concept(document.Title),
            Subject = Reference(document.Patient),
            Encounter = document.Encounter is { } encounter ? Reference(encounter) : null,
            Date = FhirTime.DateTime(document.Date),
            Author = [Reference(document.Author)],
            Title = document.Title,
            Custodian = Reference(document.Custodian),
        };

        if (document.Attester is { } attester)
        {
            composition.Attester.Add(new R4.Composition.AttesterComponent
            {
                Mode = R4.Composition.CompositionAttestationMode.Legal,
                Time = document.AttestedAt is { } at ? FhirTime.DateTime(at) : null,
                Party = Reference(attester),
            });
        }

        foreach (var section in document.Sections.Where(s => !s.IsEmpty))
        {
            var lines = section.Text.Count > 0 ? section.Text : section.Entries.Select(e => AbdmNarrative.Summary(e)).OfType<string>().ToList();
            composition.Section.Add(new R4.Composition.SectionComponent
            {
                Title = section.Title,
                Code = section.Code.Concept(),
                Text = AbdmNarrative.Generated(lines.Count > 0 ? lines : [section.Title]),
                Entry = section.Entries.Select(Reference).ToList(),
            });
        }

        composition.Text = AbdmNarrative.Generated(
        [
            document.Title,
            $"Date: {FhirTime.DateTime(document.Date)}",
            .. composition.Section.Select(s => s.Title).OfType<string>(),
        ]);
        return composition;
    }

    /// <summary>A typed reference written as in the read API; <see cref="Resolve"/> turns it into the entry's full URL.</summary>
    private static R4.ResourceReference Reference(R4.Resource resource) =>
        new(Key(resource)) { Type = resource.TypeName, Display = AbdmNarrative.Label(resource) };

    /// <summary>
    /// Points every reference in the resource at the entry it names. A reference to an encounter outside the document
    /// becomes a reference to the document's encounter (it is the same visit or stay); any other reference outside the
    /// document keeps only its display or identifier, as a reference may.
    /// </summary>
    private static void Resolve(R4.Resource resource, Dictionary<string, string> ids, string? encounterUrn)
    {
        foreach (var reference in References(resource).ToList())
        {
            if (reference.Reference is not { } target)
            {
                continue;
            }

            if (ids.TryGetValue(target, out var uuid))
            {
                reference.Reference = Urn(uuid);
            }
            else if (target.StartsWith("Encounter/", StringComparison.Ordinal) && encounterUrn is not null)
            {
                reference.Reference = encounterUrn;
            }
            else
            {
                reference.Reference = null;
                if (reference.Display is null && reference.Identifier is null)
                {
                    reference.Display = target[..target.IndexOf('/', StringComparison.Ordinal)];
                }
            }
        }
    }

    /// <summary>
    /// The resources the Composition reaches through references, in document order: a supporting resource nothing
    /// refers to (a doctor named only in a dropped link) is left out, as a document holds only what it uses.
    /// </summary>
    private static List<R4.Resource> Reachable(R4.Composition composition, List<R4.Resource> resources, Dictionary<string, string> ids)
    {
        var byUrn = resources.ToDictionary(r => Urn(ids[Key(r)]), StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<R4.Resource>([composition]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var reference in References(current))
            {
                if (reference.Reference is { } urn && byUrn.TryGetValue(urn, out var target) && reached.Add(urn))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return [.. resources.Where(r => reached.Contains(Urn(ids[Key(r)])))];
    }

    private static IEnumerable<R4.ResourceReference> References(R4.Base element)
    {
        foreach (var (_, value) in element.EnumerateElements())
        {
            IEnumerable<R4.Base> children = value switch
            {
                R4.Base single => [single],
                IEnumerable<R4.Base> list => list,
                _ => [],
            };
            foreach (var child in children)
            {
                if (child is R4.ResourceReference reference)
                {
                    yield return reference;
                }

                foreach (var nested in References(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string Key(R4.Resource resource) => $"{resource.TypeName}/{resource.Id}";
}

/// <summary>
/// The human-readable part of the document: plain XHTML paragraphs (generated), escaped, for the Composition, each
/// section and each resource, so a receiving PHR app can show the record as written.
/// </summary>
internal static class AbdmNarrative
{
    public static R4.Narrative Generated(IEnumerable<string> lines)
    {
        var html = new StringBuilder("<div xmlns=\"http://www.w3.org/1999/xhtml\">");
        foreach (var line in lines.SelectMany(l => l.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            html.Append("<p>").Append(SecurityElement.Escape(line)).Append("</p>");
        }

        return new R4.Narrative { Status = R4.Narrative.NarrativeStatus.Generated, Div = html.Append("</div>").ToString() };
    }

    /// <summary>A short name for a reference's display.</summary>
    public static string? Label(R4.Resource resource) => resource switch
    {
        R4.Patient p => p.Name.FirstOrDefault()?.Text,
        R4.Practitioner p => p.Name.FirstOrDefault()?.Text,
        R4.Organization o => o.Name,
        _ => Summary(resource),
    };

    /// <summary>One line saying what the resource holds ("Paracetamol 500 mg Tablet: 1 tablet, three times a day …").</summary>
    public static string? Summary(R4.Resource resource) => resource switch
    {
        R4.Patient p => Join(p.Name.FirstOrDefault()?.Text, Join("UHID", p.Identifier.FirstOrDefault(i => i.System == FhirSystems.Uhid)?.Value)),
        R4.Practitioner p => p.Name.FirstOrDefault()?.Text,
        R4.Organization o => o.Name,
        R4.Encounter e => Join(e.Type.FirstOrDefault()?.Text ?? e.Class?.Display, e.Period?.Start),
        R4.Condition c => Text(c.Code),
        R4.AllergyIntolerance a => Join("Allergy", Text(a.Code)),
        R4.MedicationRequest m => Join(Text(m.Medication as R4.CodeableConcept) + ":", m.DosageInstruction.FirstOrDefault()?.Text),
        R4.Observation o => Join(Text(o.Code) + ":", Value(o)),
        R4.DiagnosticReport r => Join(Text(r.Code), r.Conclusion is { } conclusion ? $"({conclusion})" : null),
        R4.DocumentReference d => d.Description ?? Text(d.Type),
        _ => null,
    };

    private static string? Text(R4.CodeableConcept? concept) =>
        FhirElements.Clean(concept?.Text) ?? concept?.Coding.Select(c => c.Display).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

    private static string? Value(R4.Observation observation) => observation.Value switch
    {
        R4.Quantity q => Join(q.Comparator is { } comparator ? Comparator(comparator) + q.Value?.ToString(CultureInfo.InvariantCulture) : q.Value?.ToString(CultureInfo.InvariantCulture), q.Unit),
        R4.FhirString s => s.Value,
        null when observation.Component.Count > 0 => string.Join(" / ", observation.Component.Select(c => (c.Value as R4.Quantity)?.Value?.ToString(CultureInfo.InvariantCulture) ?? "-"))
                                                     + (observation.Component.Select(c => (c.Value as R4.Quantity)?.Unit).FirstOrDefault(u => u is not null) is { } unit ? " " + unit : string.Empty),
        _ => null,
    };

    private static string Comparator(R4.Quantity.QuantityComparator comparator) => comparator switch
    {
        R4.Quantity.QuantityComparator.LessThan => "<",
        R4.Quantity.QuantityComparator.LessOrEqual => "<=",
        R4.Quantity.QuantityComparator.GreaterOrEqual => ">=",
        _ => ">",
    };

    private static string? Join(string? first, string? second) =>
        (FhirElements.Clean(first), FhirElements.Clean(second)) switch
        {
            (null, null) => null,
            ({ } a, null) => a.TrimEnd(':'),
            (null, { } b) => b,
            ({ } a, { } b) => $"{a} {b}",
        };
}
