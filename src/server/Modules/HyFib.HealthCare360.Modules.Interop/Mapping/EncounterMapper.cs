using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Encounters come from two places. A front-office visit (<c>Encounter/visit-{id}</c>) is the hospital encounter; for
/// an in-patient visit it carries the admission (period, admit source, discharge disposition, admitting and treating
/// doctors). A clinician's encounter note (<c>Encounter/enc-{id}</c>) is a sub-encounter <c>partOf</c> its visit or
/// admission.
/// </summary>
internal static class EncounterMapper
{
    public static R4.Encounter FromVisit(VisitRow v, ReferenceDirectory refs)
    {
        var admission = v.Admission;
        var encounter = new R4.Encounter
        {
            Id = FhirIds.Of(FhirIds.Visit, v.Id),
            Meta = FhirElements.Meta(FhirElements.Latest(v.CreatedAt, v.UpdatedAt, admission?.CreatedAt, admission?.UpdatedAt), null),
            Status = admission is null ? VisitStatus(v.Status) : AdmissionStatus(admission.Status),
            Class = Class(v.Type),
            Subject = FhirReferences.Patient(v.PatientId),
            Period = admission is null
                ? Period(v.VisitedAt, v.CompletedAt)
                : Period(admission.AdmittedAt, admission.DischargedAt ?? admission.CancelledAt),
            ServiceType = refs.Department(admission?.DepartmentId ?? v.DepartmentId) is { } department ? FhirElements.Text(department) : null,
            ServiceProvider = refs.Organization(v.FacilityId),
        };

        encounter.Identifier.Add(FhirElements.Identifier(FhirSystems.VisitNumber, v.VisitNumber, "VN", "Visit number"));
        encounter.Type.Add(FhirElements.Text(VisitTypeText(v.Type)));
        AddParticipant(encounter, "ATND", "attender", refs.Practitioner(v.PractitionerId));
        if (FhirElements.Clean(v.ChiefComplaint) is { } complaint)
        {
            encounter.ReasonCode.Add(FhirElements.Text(complaint));
        }

        if (admission is not null)
        {
            encounter.Identifier.Add(new R4.Identifier(FhirSystems.AdmissionNumber, admission.AdmissionNumber));
            encounter.Type.Add(FhirElements.Text(AdmissionTypeText(admission.Type)));
            AddParticipant(encounter, "ADM", "admitter", refs.Practitioner(admission.AdmittingPractitionerId));
            AddParticipant(encounter, "ATND", "attender", refs.Practitioner(admission.ConsultantPractitionerId));
            encounter.Hospitalization = new R4.Encounter.HospitalizationComponent
            {
                AdmitSource = AdmitSource(admission.Source, admission.Type),
                DischargeDisposition = admission.DischargedAt is not null && admission.DischargeType is { } discharge ? DischargeDisposition(discharge) : null,
            };
        }

        return encounter;
    }

    public static R4.Encounter FromClinicalEncounter(ClinicalEncounterRow e, ReferenceDirectory refs)
    {
        var encounter = new R4.Encounter
        {
            Id = FhirIds.Of(FhirIds.ClinicalEncounter, e.Id),
            Meta = FhirElements.Meta(e.CreatedAt, e.UpdatedAt),
            Status = e.Status == EncounterStatus.InProgress ? R4.Encounter.EncounterStatus.InProgress : R4.Encounter.EncounterStatus.Finished,
            Class = Class(e.Type),
            Subject = FhirReferences.Patient(e.PatientId),
            Period = Period(e.StartedAt, e.Status == EncounterStatus.InProgress ? null : e.SignedAt),
            ServiceType = refs.Department(e.DepartmentId) is { } department ? FhirElements.Text(department) : null,
            ServiceProvider = refs.Organization(e.FacilityId),
            PartOf = FhirReferences.Context(null, e.VisitId, e.AdmissionId, refs),
        };

        encounter.Type.Add(FhirElements.Text(ClinicalTypeText(e.Type)));
        AddParticipant(encounter, "PPRF", "primary performer", refs.Practitioner(e.PractitionerId));
        if (FhirElements.Clean(e.ChiefComplaint) is { } complaint)
        {
            encounter.ReasonCode.Add(FhirElements.Text(complaint));
        }

        return encounter;
    }

    public static R4.Encounter.EncounterStatus VisitStatus(VisitStatus status) => status switch
    {
        Patients.Domain.VisitStatus.Open => R4.Encounter.EncounterStatus.Arrived,
        Patients.Domain.VisitStatus.InProgress => R4.Encounter.EncounterStatus.InProgress,
        Patients.Domain.VisitStatus.Completed => R4.Encounter.EncounterStatus.Finished,
        Patients.Domain.VisitStatus.Cancelled => R4.Encounter.EncounterStatus.Cancelled,
        _ => R4.Encounter.EncounterStatus.Unknown,
    };

    public static R4.Encounter.EncounterStatus AdmissionStatus(AdmissionStatus status) => status switch
    {
        Inpatient.Domain.AdmissionStatus.Admitted or Inpatient.Domain.AdmissionStatus.DischargeInitiated => R4.Encounter.EncounterStatus.InProgress,
        Inpatient.Domain.AdmissionStatus.Cancelled => R4.Encounter.EncounterStatus.Cancelled,
        _ => R4.Encounter.EncounterStatus.Finished,
    };

    /// <summary>Encounter class (v3 ActCode): AMB, IMP, EMER, SS (day care) or VR (telehealth).</summary>
    public static R4.Coding Class(VisitType type) => type switch
    {
        VisitType.InPatient => FhirElements.Coding(FhirSystems.ActCode, "IMP", "inpatient encounter"),
        VisitType.Emergency => FhirElements.Coding(FhirSystems.ActCode, "EMER", "emergency"),
        VisitType.DayCare => FhirElements.Coding(FhirSystems.ActCode, "SS", "short stay"),
        VisitType.Telehealth => FhirElements.Coding(FhirSystems.ActCode, "VR", "virtual"),
        _ => FhirElements.Coding(FhirSystems.ActCode, "AMB", "ambulatory"),
    };

    public static R4.Coding Class(EncounterType type) => type switch
    {
        EncounterType.InPatient => Class(VisitType.InPatient),
        EncounterType.Emergency => Class(VisitType.Emergency),
        EncounterType.Telehealth => Class(VisitType.Telehealth),
        _ => Class(VisitType.OutPatient),
    };

    /// <summary>Admit source (THO admit-source); a transfer-in admission is always "hosp-trans".</summary>
    public static R4.CodeableConcept AdmitSource(AdmissionSource source, AdmissionType type) =>
        type == AdmissionType.TransferIn
            ? FhirElements.Concept(FhirSystems.AdmitSource, "hosp-trans", "Transferred from other hospital")
            : source switch
            {
                Inpatient.Domain.AdmissionSource.OutPatient => FhirElements.Concept(FhirSystems.AdmitSource, "outp", "From outpatient department"),
                Inpatient.Domain.AdmissionSource.Emergency => FhirElements.Concept(FhirSystems.AdmitSource, "emd", "From accident/emergency department"),
                Inpatient.Domain.AdmissionSource.Referral => FhirElements.Concept(FhirSystems.AdmitSource, "mp", "Medical Practitioner/physician referral"),
                _ => FhirElements.Concept(FhirSystems.AdmitSource, "other", "Other"),
            };

    /// <summary>Discharge disposition (THO discharge-disposition); LAMA, DAMA and absconding are self-discharges.</summary>
    public static R4.CodeableConcept DischargeDisposition(DischargeType type) => type switch
    {
        Inpatient.Domain.DischargeType.Normal => FhirElements.Concept(FhirSystems.DischargeDisposition, "home", "Home"),
        Inpatient.Domain.DischargeType.Referred => FhirElements.Concept(FhirSystems.DischargeDisposition, "other-hcf", "Other healthcare facility"),
        Inpatient.Domain.DischargeType.Lama => FhirElements.Concept(FhirSystems.DischargeDisposition, "aadvice", "Left against advice", "Left against medical advice (LAMA)"),
        Inpatient.Domain.DischargeType.Dama => FhirElements.Concept(FhirSystems.DischargeDisposition, "aadvice", "Left against advice", "Discharged against medical advice (DAMA)"),
        Inpatient.Domain.DischargeType.Absconded => FhirElements.Concept(FhirSystems.DischargeDisposition, "aadvice", "Left against advice", "Absconded"),
        Inpatient.Domain.DischargeType.Expired => FhirElements.Concept(FhirSystems.DischargeDisposition, "exp", "Expired"),
        _ => FhirElements.Concept(FhirSystems.DischargeDisposition, "oth", "Other"),
    };

    private static string VisitTypeText(VisitType type) => type switch
    {
        VisitType.InPatient => "In-patient admission",
        VisitType.Emergency => "Emergency visit",
        VisitType.DayCare => "Day care",
        VisitType.Telehealth => "Telehealth consultation",
        VisitType.HealthCheckup => "Health checkup",
        _ => "Out-patient consultation",
    };

    private static string ClinicalTypeText(EncounterType type) => type switch
    {
        EncounterType.InPatient => "In-patient clinical encounter",
        EncounterType.Emergency => "Emergency clinical encounter",
        EncounterType.Telehealth => "Telehealth consultation",
        EncounterType.HealthCheckup => "Health checkup review",
        _ => "Out-patient consultation",
    };

    private static string AdmissionTypeText(AdmissionType type) => type switch
    {
        AdmissionType.Emergency => "Emergency admission",
        AdmissionType.DayCare => "Day-care admission",
        AdmissionType.Maternity => "Maternity admission",
        AdmissionType.TransferIn => "Transfer-in admission",
        _ => "Elective admission",
    };

    private static R4.Period Period(DateTimeOffset start, DateTimeOffset? end) => new()
    {
        Start = FhirTime.DateTime(start),
        End = end is { } value ? FhirTime.DateTime(value) : null,
    };

    private static void AddParticipant(R4.Encounter encounter, string code, string display, R4.ResourceReference? individual)
    {
        if (individual is null
            || encounter.Participant.Any(p => p.Individual?.Display == individual.Display && p.Type.Any(t => t.Coding.Any(c => c.Code == code))))
        {
            return;
        }

        encounter.Participant.Add(new R4.Encounter.ParticipantComponent
        {
            Type = [FhirElements.Concept(FhirSystems.ParticipationType, code, display)],
            Individual = individual,
        });
    }
}
