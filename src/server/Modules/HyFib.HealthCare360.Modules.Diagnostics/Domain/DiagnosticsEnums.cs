using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain;

/// <summary>
/// The kind of encounter a diagnostic order belongs to. It decides payment gating: out-patients and external
/// (walk-in / outside-referral) patients pay before the service, while in-patient, emergency and day-care work is
/// billed to the running account and must never be held up at the counter.
/// </summary>
public enum PatientClass
{
    OutPatient = 0,
    InPatient,
    Emergency,
    DayCare,

    /// <summary>Walk-in or outside-referral patient registered only for diagnostics (no hospital visit).</summary>
    External,
}

/// <summary>Reporting workflow shared by radiology and cardiology reports (IMG-03, CAR-04).</summary>
public enum DiagnosticReportStatus
{
    Draft = 0,

    /// <summary>Submitted by the reporting clinician and waiting for sign-off.</summary>
    Reported,

    /// <summary>Signed off; not yet visible to the patient.</summary>
    Verified,

    /// <summary>Visible in the EHR and the patient portal.</summary>
    Released,
}

/// <summary>How a critical value was communicated (documented in the critical call register).</summary>
public enum CommunicationMethod
{
    Phone = 0,
    InPerson,
    VideoCall,
    SecureMessage,
}

public static class PatientClasses
{
    /// <summary>
    /// Maps the Patients-module visit type to the diagnostic patient class. An admission always wins because the
    /// work is charged to the IP account even when the order was raised from an earlier OP visit.
    /// </summary>
    public static PatientClass From(VisitType? visitType, bool hasAdmission) =>
        hasAdmission
            ? PatientClass.InPatient
            : visitType switch
            {
                null => PatientClass.External,
                VisitType.InPatient => PatientClass.InPatient,
                VisitType.Emergency => PatientClass.Emergency,
                VisitType.DayCare => PatientClass.DayCare,
                _ => PatientClass.OutPatient,
            };

    /// <summary>True when the patient settles the bill before the service is performed (OP and external patients).</summary>
    public static bool PaysBeforeService(this PatientClass patientClass) =>
        patientClass is PatientClass.OutPatient or PatientClass.External;
}
