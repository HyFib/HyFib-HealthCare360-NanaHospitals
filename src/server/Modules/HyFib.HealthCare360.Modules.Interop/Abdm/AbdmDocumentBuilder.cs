using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// Assembles an <see cref="AbdmDocument"/> into the IG's DocumentBundle: a FHIR <c>document</c> Bundle whose first entry
/// is the Composition, followed by every resource it refers to, each with a <c>urn:uuid:</c> full URL. The document is
/// self-contained, so every reference is resolved inside it: a reference to a resource that is not in the document keeps
/// only its display (an encounter of the same stay or visit points to the document's encounter instead, and a patient to
/// the document's patient: a record kept under a duplicate UHID that was merged into it).
/// <para>
/// Ids are derived from HC360's ids (a SHA-256 based UUID, RFC 9562 version 8), so exporting the same record again gives
/// the same ids, and a patient or doctor keeps one id across documents. The document's version, and with it the Bundle's
/// id, identifier and timestamp, is the latest change to what it holds and a hash of its content, the printed PDF
/// included (<see cref="Version"/>): it changes whenever the document does, and only then, so a document exported twice
/// is either the same, byte for byte, or a new version.
/// </para>
/// </summary>
internal static partial class AbdmDocumentBuilder
{
    public static R4.Bundle Build(AbdmDocument document)
    {
        var resources = Resources(document);
        var compositionKey = $"Composition/{document.Type}/{FhirIds.Of(document.SourceId)}";
        var ids = resources.ToDictionary(Key, r => Uuid(Key(r)), StringComparer.Ordinal);
        ids[compositionKey] = Uuid(compositionKey);

        var composition = Composition(document, ids[compositionKey]);
        var encounterKey = document.Encounter is { } encounter ? Key(encounter) : null;
        var encounterUrn = encounterKey is null ? null : Urn(ids[encounterKey]);
        var patientUrn = Urn(ids[Key(document.Patient)]);
        foreach (var resource in resources.Prepend(composition))
        {
            // The encounter's own links (the visit a consultation is part of) are not redirected to itself.
            Resolve(resource, ids, Key(resource) == encounterKey ? null : encounterUrn, patientUrn);
        }

        resources = Reachable(composition, resources, ids);
        foreach (var resource in resources)
        {
            resource.Id = ids[Key(resource)];
            if (resource is R4.DomainResource domain && domain.Text is null && AbdmNarrative.Summary(domain) is { } summary)
            {
                domain.Text = AbdmNarrative.Generated([summary]);
            }

            PrintedAsOf(resource, document.Date);
        }

        // The latest change to what the document holds: a doctor the record names but the document leaves out (the
        // referral doctor, a summary's writer when another doctor signed it) does not count.
        var lastUpdated = resources.Select(r => r.Meta?.LastUpdated).OfType<DateTimeOffset>().Append(document.RecordUpdated).Max();
        composition.Meta!.LastUpdated = FhirTime.ToMilliseconds(lastUpdated);
        var version = Version(lastUpdated, resources.Prepend(composition));
        composition.Meta.VersionId = version;
        var bundleId = Uuid($"Bundle/{document.Type}/{FhirIds.Of(document.SourceId)}/{version}");
        var bundle = new R4.Bundle
        {
            Id = bundleId,
            Meta = new R4.Meta
            {
                VersionId = version,
                LastUpdated = FhirTime.ToMilliseconds(lastUpdated),
                Profile = [AbdmProfiles.DocumentBundle],
                Security = [FhirElements.Coding(AbdmSystems.Confidentiality, "V", "very restricted")],
            },
            Identifier = new R4.Identifier(AbdmSystems.Uri, Urn(bundleId)),
            Type = R4.Bundle.BundleType.Document,

            // A document is assembled once per version: its timestamp is the version's, not the export's, as its
            // identifier is.
            Timestamp = FhirTime.ToMilliseconds(lastUpdated),
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

    /// <summary>
    /// The document's version: its latest change in Unix milliseconds, and the first 8 bytes of a SHA-256 hash of its
    /// resources as JSON, the printed PDF included. The hash catches what moves no change time forward: an entry that
    /// drops out (an allergy recorded in error, a diagnosis deleted), or a PDF that prints something else (the patient's
    /// age on a later day, the DSC signature as it checks out now).
    /// </summary>
    internal static string Version(DateTimeOffset lastUpdated, IEnumerable<R4.Resource> resources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var resource in resources)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(FhirJson.Serialize(resource)));
        }

        return $"{lastUpdated.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(hash.GetHashAndReset(), 0, 8)}";
    }

    /// <summary>
    /// A printed PDF as the same page always prints. The PDF is made again at every export, and two PDFs of the same page
    /// differ only in what the browser numbers as it goes: the creation and modification dates, the time it was made,
    /// which become <paramref name="date"/> (in UTC); and in a tagged PDF the ids of table header cells, named after their
    /// DOM nodes (<c>(node00000070)</c>) by a counter the browser keeps from one page to the next, which are numbered
    /// again 0, 1, 2… in the same order. The rest of the file is left byte for byte as it is: each date and id keeps its
    /// length, so the file's cross-reference offsets still hold, and the ids their order, so the structure tree's sorted
    /// IDTree still holds.
    /// </summary>
    internal static byte[] PdfAsOf(byte[] pdf, DateTimeOffset date)
    {
        // Latin-1 maps every byte to one character and back, so the binary parts of the file come through unchanged.
        var text = Encoding.Latin1.GetString(pdf);
        var utc = date.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var printed = PdfDate().Replace(text, m => m.Groups["key"].Value + utc + m.Groups["zone"].Value switch
        {
            "" => string.Empty,
            "Z" => "Z",
            var zone => "+00" + zone[3..4] + "00" + zone[6..],
        });

        var nodes = PdfNodeId().Matches(printed);
        if (nodes.Count > 0)
        {
            // Each id becomes the order it first comes in the file: the gaps between them are other nodes the browser
            // numbered meanwhile, in this page or in another it was printing at the same time, and even their order
            // differs from one print to the next (Windows CI numbered a prescription's two headers the other way round).
            // Where each one comes in the file, which follows the page, does not.
            var rank = nodes.Select(Node).Distinct().Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
            printed = PdfNodeId().Replace(printed, m => $"(node{rank[Node(m)].ToString("D8", CultureInfo.InvariantCulture)})");
        }

        return string.Equals(printed, text, StringComparison.Ordinal) ? pdf : Encoding.Latin1.GetBytes(printed);

        static int Node(Match id) => int.Parse(id.Groups["n"].ValueSpan, CultureInfo.InvariantCulture);
    }

    /// <summary>A date of the PDF's document information: <c>/CreationDate (D:20261021005544+00'00')</c>.</summary>
    [GeneratedRegex(@"(?<key>/(?:CreationDate|ModDate)\s*\(D:)\d{14}(?<zone>Z|[+-]\d\d'\d\d'?)?(?=\))", RegexOptions.CultureInvariant)]
    private static partial Regex PdfDate();

    /// <summary>The id a tagged PDF gives a table header cell, in its structure element and the cells' headers: <c>(node00000017)</c>.</summary>
    [GeneratedRegex(@"\(node(?<n>\d{8})\)", RegexOptions.CultureInvariant)]
    private static partial Regex PdfNodeId();

    /// <summary>The printed PDFs the resource carries (a Binary's, or an attachment's), as of the document's date.</summary>
    private static void PrintedAsOf(R4.Resource resource, DateTimeOffset date)
    {
        if (resource is R4.Binary { ContentType: "application/pdf", Data: { } data } binary)
        {
            binary.Data = PdfAsOf(data, date);
        }

        foreach (var attachment in Descendants<R4.Attachment>(resource))
        {
            if (attachment is { ContentType: "application/pdf", Data: { } bytes })
            {
                attachment.Data = PdfAsOf(bytes, date);
            }
        }
    }

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
        Add(document.AmendedBy);
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
            Meta = new R4.Meta { Profile = [document.Profile] },
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

        if (document.AmendedBy is { } amender && Key(amender) != Key(document.Author))
        {
            composition.Author.Add(Reference(amender));
        }

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
            composition.Section.Add(new R4.Composition.SectionComponent
            {
                Title = section.Title,
                Code = section.Code.Concept(),
                Text = SectionText(section),
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

    /// <summary>
    /// A section's narrative, which must read whole on its own (it is what a doctor attests): its free text, then a line
    /// for each entry, under the section's heading for them when there is free text. With free text it is <c>additional</c>
    /// (it says more than the entries), else <c>generated</c> from them.
    /// </summary>
    private static R4.Narrative SectionText(AbdmSection section)
    {
        var entries = section.Entries.Select(e => AbdmNarrative.Summary(e)).OfType<string>().ToList();
        if (section.Text.Count == 0)
        {
            return AbdmNarrative.Generated(entries.Count > 0 ? entries : [section.Title]);
        }

        List<string> lines = [.. section.Text];
        if (entries.Count > 0 && section.EntriesHeading is { } heading)
        {
            lines.Add(heading);
        }

        lines.AddRange(entries);
        return AbdmNarrative.Generated(lines, R4.Narrative.NarrativeStatus.Additional);
    }

    /// <summary>A typed reference written as in the read API; <see cref="Resolve"/> turns it into the entry's full URL.</summary>
    private static R4.ResourceReference Reference(R4.Resource resource) =>
        new(Key(resource)) { Type = resource.TypeName, Display = AbdmNarrative.Label(resource) };

    /// <summary>
    /// Points every reference in the resource at the entry it names. A reference to an encounter outside the document
    /// becomes a reference to the document's encounter (it is the same visit or stay), and one to another patient a
    /// reference to the document's patient (a document is one patient's: a record still under a duplicate UHID merged into
    /// the patient's names it); any other reference outside the document keeps only its display or identifier, as a
    /// reference may.
    /// </summary>
    private static void Resolve(R4.Resource resource, Dictionary<string, string> ids, string? encounterUrn, string patientUrn)
    {
        foreach (var reference in References(resource).ToList())
        {
            // A reference object shared by two elements (a summary's signer, its author and authenticator) is met twice:
            // the second time it already points into the document.
            if (reference.Reference is not { } target || target.StartsWith("urn:uuid:", StringComparison.Ordinal))
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
            else if (target.StartsWith("Patient/", StringComparison.Ordinal))
            {
                reference.Reference = patientUrn;
            }
            else
            {
                reference.Reference = null;
                if (reference.Display is null && reference.Identifier is null)
                {
                    var slash = target.IndexOf('/', StringComparison.Ordinal);
                    reference.Display = slash > 0 ? target[..slash] : reference.Type ?? "Resource";
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

    private static IEnumerable<R4.ResourceReference> References(R4.Base element) => Descendants<R4.ResourceReference>(element);

    private static IEnumerable<T> Descendants<T>(R4.Base element)
        where T : R4.Base
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
                if (child is T match)
                {
                    yield return match;
                }

                foreach (var nested in Descendants<T>(child))
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
    public static R4.Narrative Generated(IEnumerable<string> lines, R4.Narrative.NarrativeStatus status = R4.Narrative.NarrativeStatus.Generated)
    {
        var html = new StringBuilder("<div xmlns=\"http://www.w3.org/1999/xhtml\">");
        foreach (var line in lines.SelectMany(l => l.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            html.Append("<p>").Append(SecurityElement.Escape(line)).Append("</p>");
        }

        return new R4.Narrative { Status = status, Div = html.Append("</div>").ToString() };
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
        R4.Condition c => Condition(c),
        R4.AllergyIntolerance a => Allergy(a),
        R4.MedicationRequest m => Join(Text(m.Medication as R4.CodeableConcept) + ":", m.DosageInstruction.FirstOrDefault()?.Text),
        R4.Observation o => Join(Text(o.Code) + ":", Value(o)),
        R4.DiagnosticReport r => Join(Text(r.Code), r.Conclusion is { } conclusion and not AbdmProfiler.SeeTheResults ? $"({conclusion})" : null),
        R4.DocumentReference d => d.Description ?? Text(d.Type),
        _ => null,
    };

    /// <summary>
    /// "Viral fever (differential)", "Asthma (provisional, resolved)", "Hypertension (chronic)": a diagnosis with what its
    /// verification and clinical statuses say. In FHIR they change what the resource means, and the narrative must read
    /// safely on its own, so a line never states as a fact what the record does not.
    /// </summary>
    private static string? Condition(R4.Condition condition)
    {
        List<string> qualifiers = [];
        if (condition.VerificationStatus?.Coding.FirstOrDefault()?.Code is { } verification and not "confirmed")
        {
            qualifiers.Add(verification);
        }

        if (condition.ClinicalStatus?.Coding.FirstOrDefault()?.Code is { } clinical and not "active")
        {
            qualifiers.Add(clinical);
        }
        else if (FhirElements.Clean(condition.ClinicalStatus?.Text) is { } text && !text.Equals("Active", StringComparison.OrdinalIgnoreCase))
        {
            qualifiers.Add(text.ToLowerInvariant());
        }

        return Qualified(Text(condition.Code), qualifiers);
    }

    /// <summary>
    /// "Allergy Penicillin (resolved): Rash (moderate)": an allergy with its status when it is not active, a high
    /// criticality, and the reaction recorded with its severity.
    /// </summary>
    private static string? Allergy(R4.AllergyIntolerance allergy)
    {
        List<string> qualifiers = [];
        if (allergy.ClinicalStatus?.Coding.FirstOrDefault()?.Code is { } clinical and not "active")
        {
            qualifiers.Add(clinical);
        }

        if (allergy.Criticality == R4.AllergyIntolerance.AllergyIntoleranceCriticality.High)
        {
            qualifiers.Add("high criticality");
        }

        var line = Qualified(Join("Allergy", Text(allergy.Code)), qualifiers);
        var manifestations = allergy.Reaction.SelectMany(r => r.Manifestation).Select(Text).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (manifestations.Count == 0)
        {
            return line;
        }

        var severity = allergy.Reaction.Select(r => r.Severity).FirstOrDefault(s => s is not null);
        return $"{line}: {string.Join(", ", manifestations)}" + (severity is { } s ? $" ({s.ToString().ToLowerInvariant()})" : string.Empty);
    }

    private static string? Qualified(string? text, List<string> qualifiers) =>
        text is null || qualifiers.Count == 0 ? text : $"{text} ({string.Join(", ", qualifiers)})";

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
