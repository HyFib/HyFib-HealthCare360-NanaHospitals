using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;

/// <summary>What an ABDM prescription record is made from: the signed prescription, its lines and the records it names.</summary>
internal sealed record PrescriptionSource
{
    public required PrescriptionRow Prescription { get; init; }

    /// <summary>The medicine lines, as the FHIR API shares them.</summary>
    public required IReadOnlyList<PrescriptionItemRow> Lines { get; init; }

    /// <summary>The patient, with the ABHA number decrypted when it is on file.</summary>
    public required PatientRow Patient { get; init; }

    /// <summary>The facility that issued the prescription (the document's custodian).</summary>
    public required FacilityRow Facility { get; init; }

    /// <summary>The prescribing doctor; null when they can no longer be read.</summary>
    public PractitionerRow? Prescriber { get; init; }

    /// <summary>The visit the prescription was written in (with its admission for an in-patient), when known.</summary>
    public VisitRow? Visit { get; init; }

    /// <summary>Names of the doctors, departments and facilities the visit refers to.</summary>
    public ReferenceDirectory References { get; init; } = ReferenceDirectory.Empty;
}

/// <summary>
/// A signed prescription → an ABDM health record: an NRCeS DocumentBundle whose Composition follows the PrescriptionRecord
/// profile (SNOMED CT 440545006), with the patient, the prescribing doctor, the hospital, the visit as the encounter and
/// one NRCeS MedicationRequest per medicine line.
/// <para>
/// The lines are the FHIR API's MedicationRequests (<see cref="MedicationRequestMapper"/>): the medicine as written
/// (brand, strength, form and generic, as text: HC360 holds no SNOMED CT medicine codes), the dosage as text and as
/// timing, the course and the quantity, referring to the document's patient, visit and doctor. The profile allows one
/// section, so the advice printed for the patient is part of its text; the notes for the pharmacist are not shared.
/// </para>
/// </summary>
internal static class PrescriptionRecordMapper
{
    public const string SnomedPrescriptionRecord = "440545006";

    public static readonly SectionCode PrescriptionSection = new(FhirSystems.Snomed, SnomedPrescriptionRecord, "Prescription record");

    /// <summary>The document, assembled at <paramref name="now"/> and identified by <paramref name="documentId"/>.</summary>
    public static R4.Bundle Map(PrescriptionSource source, Guid documentId, DateTimeOffset now)
    {
        var p = source.Prescription;
        var refs = AbdmParticipants.WithEntries(source.References, source.Facility, source.Prescriber);
        var today = DateOnly.FromDateTime(now.ToOffset(FhirTime.Ist).DateTime);
        var lines = source.Lines.OrderBy(l => l.Sequence).ToList();
        var text = new SectionText()
            .List("Medicines:", [.. lines.Select(Line)])
            .Labelled("Advice", p.Advice);
        var section = new R4.Composition.SectionComponent
        {
            Title = "Prescription",
            Code = PrescriptionSection.ToConcept(),
            Text = text.ToNarrative(),
            // The profile tells the section's entries apart by their type (MedicationRequest or Binary).
            Entry = [.. lines.Select(line => DocumentBundleBuilder.Reference(line.Id, MedicationRequestMapper.MedicationText(line), "MedicationRequest"))],
        };

        var builder = new DocumentBundleBuilder()
            .Add(p.Id, Composition(source, section), AbdmProfiles.PrescriptionRecord)
            .Add(source.Patient.Id, PatientMapper.Map(source.Patient, refs), AbdmProfiles.Patient);
        if (source.Visit is { } visit)
        {
            var encounter = EncounterMapper.FromVisit(visit, refs);
            encounter.Subject = AbdmParticipants.Patient(source.Patient);
            builder.Add(visit.Id, encounter, AbdmProfiles.Encounter);
        }

        if (source.Prescriber is { } prescriber)
        {
            builder.Add(prescriber.Id, AbdmParticipants.Practitioner(prescriber), AbdmProfiles.Practitioner);
        }

        builder.Add(source.Facility.Id, AbdmParticipants.Organization(source.Facility), AbdmProfiles.Organization);
        foreach (var line in lines)
        {
            builder.Add(line.Id, MedicationRequest(source, line, refs, today), AbdmProfiles.MedicationRequest);
        }

        return builder.Build(documentId, now);
    }

    private static R4.Composition Composition(PrescriptionSource source, R4.Composition.SectionComponent section)
    {
        var p = source.Prescription;
        var composition = new R4.Composition
        {
            Meta = FhirElements.Meta(p.CreatedAt, p.UpdatedAt),
            // The prescription's own identity, the same for every export of it (each export is a new Bundle).
            Identifier = new R4.Identifier(FhirSystems.Uri, DocumentBundleBuilder.FullUrl(p.Id)),
            Status = R4.CompositionStatus.Final,
            Type = new R4.CodeableConcept
            {
                Coding = [FhirElements.Coding(FhirSystems.Snomed, SnomedPrescriptionRecord, "Prescription record")],
                Text = "Prescription",
            },
            Subject = AbdmParticipants.Patient(source.Patient),
            Encounter = source.Visit is { } visit ? DocumentBundleBuilder.Reference(visit.Id) : null,
            Date = FhirTime.DateTime(p.SignedAt ?? p.PrescribedAt),
            Title = $"Prescription {p.PrescriptionNumber}",
            Custodian = DocumentBundleBuilder.Reference(source.Facility.Id, source.Facility.Name),
            Section = [section],
        };

        if (source.Prescriber is { } prescriber)
        {
            composition.Author.Add(DocumentBundleBuilder.Reference(prescriber.Id, prescriber.DisplayName));

            // Only the prescribing doctor signs a prescription in HC360; a refill is signed by whoever issued it.
            if (p.SignedAt is { } signed && p.RefillOfPrescriptionId is null)
            {
                composition.Attester.Add(new R4.Composition.AttesterComponent
                {
                    Mode = R4.Composition.CompositionAttestationMode.Legal,
                    Time = FhirTime.DateTime(signed),
                    Party = DocumentBundleBuilder.Reference(prescriber.Id, prescriber.DisplayName),
                });
            }
        }
        else
        {
            // No doctor on file: the hospital is the author (a Composition must have one).
            composition.Author.Add(DocumentBundleBuilder.Reference(source.Facility.Id, source.Facility.Name));
        }

        return composition;
    }

    /// <summary>The FHIR API's MedicationRequest of a line, referring to the document's patient, visit and doctor.</summary>
    private static R4.MedicationRequest MedicationRequest(PrescriptionSource source, PrescriptionItemRow line, ReferenceDirectory refs, DateOnly today)
    {
        var request = MedicationRequestMapper.Map(line, refs, today);
        request.Subject = AbdmParticipants.Patient(source.Patient);
        request.Encounter = source.Visit is { } visit ? DocumentBundleBuilder.Reference(visit.Id) : null;
        request.Requester ??= DocumentBundleBuilder.Reference(source.Facility.Id, source.Facility.Name);
        return request;
    }

    /// <summary>"Dolo 650 Tablet (Paracetamol): 1 tab, twice a day, for 5 days. After food".</summary>
    private static string Line(PrescriptionItemRow line) =>
        $"{MedicationRequestMapper.MedicationText(line)}: {MedicationRequestMapper.Dosage(line).Text}";
}
