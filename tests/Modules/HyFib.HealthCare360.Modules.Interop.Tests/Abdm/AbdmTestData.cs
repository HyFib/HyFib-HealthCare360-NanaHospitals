using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>
/// A discharge summary as HC360 holds it: Mrs Meera Kulkarni (ABHA number on file), admitted with community-acquired
/// pneumonia at Nana Hospitals, Pune (HFR id on file), signed by Dr Asha Rao.
/// </summary>
internal static class AbdmTestData
{
    public static readonly Guid SummaryId = Guid.Parse("01923a4b-0000-7000-8000-00000000d501");
    public static readonly Guid AdmissionId = Guid.Parse("01923a4b-0000-7000-8000-00000000d502");
    public static readonly Guid VisitId = Guid.Parse("01923a4b-0000-7000-8000-00000000d503");
    public static readonly Guid DocumentId = Guid.Parse("01923a4b-0000-7000-8000-00000000d504");
    public const string Abha = "91234567890123";

    public static DischargeSummaryRow Summary(DischargeSummaryStatus status = DischargeSummaryStatus.Signed) => new()
    {
        Id = SummaryId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        AdmissionId = AdmissionId,
        PractitionerId = TestData.DoctorId,
        SignedByPractitionerId = TestData.DoctorId,
        AdmittedAt = TestData.Now.AddDays(-4),
        DischargedAt = TestData.Now,
        Diagnoses = "Community-acquired pneumonia, right lower lobe\nType 2 diabetes mellitus",
        DiagnosisCodes = ["J18.9", "E11.9", "1A00"],
        PresentingComplaints = "Fever and cough with breathlessness for 4 days",
        CourseInHospital = "Treated with IV antibiotics and oxygen.\n\nAfebrile from day 3; oxygen stopped on day 3.",
        Procedures = "Chest physiotherapy",
        InvestigationsSummary = "Chest X-ray: right lower lobe consolidation\nCBC: TLC 14,200/µL",
        ConditionAtDischarge = "Stable, afebrile, SpO2 97% on room air",
        Medications = "Tab. Amoxicillin-clavulanate 625 mg BD x 5 days\nTab. Metformin 500 mg BD",
        Advice = "Plenty of fluids. Return if the fever comes back or breathing gets difficult.",
        FollowUpDate = new DateOnly(2026, 10, 2),
        FollowUpInstructions = "Review in the medicine OPD with a repeat chest X-ray",
        Status = status,
        SignedAt = TestData.Now.AddHours(-1),
        AmendedAt = status == DischargeSummaryStatus.Amended ? TestData.Now : null,
        CreatedAt = TestData.Now.AddHours(-3),
        UpdatedAt = TestData.Now.AddHours(-1),
    };

    public static PatientRow Patient(string? abha = Abha) => TestData.Patient() with { AbhaNumber = abha, PreferredLanguage = "Marathi" };

    public static FacilityRow Facility(string? hfrId = "IN2710000123") => new()
    {
        Id = TestData.FacilityId,
        Code = "NHP",
        Name = "Nana Hospitals, Pune",
        HfrId = hfrId,
        Address = new Address { Line1 = "1 Hospital Road", City = "Pune", District = "Pune", State = "Maharashtra", PinCode = "411004" },
        Phone = "+912025550100",
        Email = "records@nanahospitals.example",
        CreatedAt = TestData.Now.AddYears(-2),
    };

    public static PractitionerRow Doctor() => new()
    {
        Id = TestData.DoctorId,
        Code = "DR-ASHA",
        Title = "Dr.",
        FirstName = "Asha",
        LastName = "Rao",
        DisplayName = "Dr. Asha Rao",
        Gender = Gender.Female,
        Qualification = "MBBS, MD (General Medicine)",
        RegistrationCouncil = "Maharashtra Medical Council",
        RegistrationNumber = "2009/03/1234",
        CreatedAt = TestData.Now.AddYears(-3),
    };

    public static VisitRow Stay() => new()
    {
        Id = VisitId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        VisitNumber = "NHIP260000042",
        Type = VisitType.InPatient,
        Status = VisitStatus.Completed,
        DepartmentId = TestData.DepartmentId,
        PractitionerId = TestData.DoctorId,
        VisitedAt = TestData.Now.AddDays(-4),
        CompletedAt = TestData.Now,
        ChiefComplaint = "Fever and cough",
        CreatedAt = TestData.Now.AddDays(-4),
        Admission = new AdmissionRow
        {
            Id = AdmissionId,
            AdmissionNumber = "NHADM260000007",
            Type = AdmissionType.Emergency,
            Source = AdmissionSource.Emergency,
            Status = AdmissionStatus.Discharged,
            AdmittedAt = TestData.Now.AddDays(-4),
            AdmittingPractitionerId = TestData.DoctorId,
            ConsultantPractitionerId = TestData.DoctorId,
            DepartmentId = TestData.DepartmentId,
            DischargeType = DischargeType.Normal,
            DischargedAt = TestData.Now,
            CreatedAt = TestData.Now.AddDays(-4),
        },
    };

    public static DischargeSummarySource Source() => new()
    {
        Summary = Summary(),
        Patient = Patient(),
        Facility = Facility(),
        Author = Doctor(),
        Stay = Stay(),
        CodeTitles = new Dictionary<string, string>
        {
            ["J18.9"] = "Pneumonia, unspecified",
            ["E11.9"] = "Type 2 diabetes mellitus without complications",
            ["1A00"] = "Cholera",
        },
        RecordedDiagnoses = new Dictionary<string, RecordedDiagnosis>
        {
            ["J18.9"] = new(DiagnosisStatus.Active, new DateOnly(2026, 9, 20), null),
            ["E11.9"] = new(DiagnosisStatus.Chronic, new DateOnly(2018, 1, 1), null),
        },
        References = new ReferenceDirectory
        {
            Practitioners = TestData.Directory.Practitioners,
            Departments = TestData.Directory.Departments,
            Facilities = TestData.Directory.Facilities,
            AdmissionVisits = new Dictionary<Guid, Guid> { [AdmissionId] = VisitId },
        },
    };

    /// <summary>The least a signed summary holds: what signing requires, and nothing coded or on file beyond it.</summary>
    public static DischargeSummarySource Minimal() => new()
    {
        Summary = new DischargeSummaryRow
        {
            Id = SummaryId,
            PatientId = TestData.PatientId,
            FacilityId = TestData.FacilityId,
            AdmissionId = AdmissionId,
            Diagnoses = "Viral fever",
            CourseInHospital = "Symptomatic treatment.",
            ConditionAtDischarge = "Stable",
            Status = DischargeSummaryStatus.Signed,
            SignedAt = TestData.Now,
            CreatedAt = TestData.Now.AddHours(-2),
        },
        Patient = Patient(abha: null),
        Facility = Facility(hfrId: null),
    };
}
