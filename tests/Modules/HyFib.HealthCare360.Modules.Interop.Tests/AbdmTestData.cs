using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.SharedKernel;
using Modality = HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging.Modality;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// One patient's records for the ABDM document tests: an out-patient consultation with its prescription, lab report and
/// chest X-ray, and an earlier admission with its discharge summary. The bundles built from them are the sample documents
/// in Fixtures/Abdm (validated against the NRCeS IG).
/// </summary>
internal static class AbdmTestData
{
    /// <summary>An ABHA number: 14 digits.</summary>
    public const string AbhaNumber = "91123456789010";

    /// <summary>Dr. Asha Rao's HPR ID: the one the NRCeS guide's examples give their doctor.</summary>
    public const string HprId = "21-1521-3828-3227";

    public static readonly Guid OpVisitId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab001");
    public static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab002");
    public static readonly Guid PrescriptionId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab003");
    public static readonly Guid LabReportId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab004");
    public static readonly Guid LabOrderId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab005");
    public static readonly Guid StayVisitId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab006");
    public static readonly Guid AdmissionId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab007");
    public static readonly Guid SummaryId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab008");
    public static readonly Guid StayLabReportId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab009");
    public static readonly Guid StayLabOrderId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab00a");
    public static readonly Guid CardiologistId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab00b");
    public static readonly Guid RadiologyReportId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab00c");
    public static readonly Guid ImagingStudyId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab00d");

    /// <summary>The radiologist who signed the chest X-ray off, and the radiology resident who wrote it.</summary>
    public static readonly Guid RadiologistId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab00e");
    public static readonly Guid ResidentId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab00f");

    // Their user accounts, which the radiology report names, and a clerk's, who is not a doctor.
    public static readonly Guid RadiologistUserId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab010");
    public static readonly Guid ResidentUserId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab011");
    public static readonly Guid ClerkUserId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab012");

    /// <summary>An ECG report and its study, and the user account of the cardiologist (<see cref="CardiologistId"/>) who signed it off.</summary>
    public static readonly Guid CardiologyReportId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab013");
    public static readonly Guid CardiologyStudyId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab014");
    public static readonly Guid CardiologistUserId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab015");

    /// <summary>A Td dose given in the consultation (<see cref="EncounterId"/>).</summary>
    public static readonly Guid DoseId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab016");

    /// <summary>
    /// A progress note Dr. Asha Rao wrote in the consultation, the cardiologist's addendum to it, and Dr. Rao's user
    /// account.
    /// </summary>
    public static readonly Guid NoteId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab017");
    public static readonly Guid AddendumId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab018");
    public static readonly Guid DoctorUserId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab019");

    /// <summary>The patient's enrolment in hypertension home monitoring, and the account of the nurse who enters readings by phone.</summary>
    public static readonly Guid EnrollmentId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab01a");
    public static readonly Guid NurseUserId = Guid.Parse("01923a4b-0000-7000-8000-0000000ab01b");

    /// <summary>The name of the programme the patient is enrolled in, as the hospital wrote it.</summary>
    public const string Programme = "Hypertension home monitoring";

    private static readonly DateTimeOffset Seen = TestData.Now.AddHours(-2);
    private static readonly DateTimeOffset Admitted = TestData.Now.AddDays(-20);
    private static readonly DateTimeOffset Discharged = TestData.Now.AddDays(-15);

    /// <summary>A small one-page PDF, standing in for the printed document.</summary>
    public static readonly AbdmPdf Pdf = new(Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
        + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"));

    public static PatientRow Patient() => TestData.Patient() with { AbhaNumber = AbhaNumber };

    public static ReferenceDirectory Names { get; } = new()
    {
        Practitioners = new Dictionary<Guid, PractitionerInfo>
        {
            [TestData.DoctorId] = new("Dr. Asha Rao", HprId),
            [CardiologistId] = new("Dr. Vikram Shah", null),
            [RadiologistId] = new("Dr. Neha Joshi", null),
            [ResidentId] = new("Dr. Kiran Patil", null),
        },
        Departments = new Dictionary<Guid, string> { [TestData.DepartmentId] = "General Medicine" },
        Facilities = new Dictionary<Guid, FacilityInfo> { [TestData.FacilityId] = new("Nana Hospitals, Pune", "IN2710000123") },
        AdmissionVisits = new Dictionary<Guid, Guid> { [AdmissionId] = StayVisitId },
    };

    public static IReadOnlyList<PractitionerRow> Practitioners { get; } =
    [
        new()
        {
            Id = TestData.DoctorId,
            Code = "D0007",
            Title = "Dr.",
            FirstName = "Asha",
            LastName = "Rao",
            DisplayName = "Dr. Asha Rao",
            Gender = Gender.Female,
            Qualification = "MBBS, MD (General Medicine)",
            RegistrationCouncil = "Maharashtra Medical Council",
            RegistrationNumber = "MMC-2009-041234",
            HprId = HprId,
            CreatedAt = TestData.Now.AddYears(-3),
            UpdatedAt = TestData.Now.AddMonths(-6),
        },
        new()
        {
            Id = CardiologistId,
            Code = "D0012",
            Title = "Dr.",
            FirstName = "Vikram",
            LastName = "Shah",
            DisplayName = "Dr. Vikram Shah",
            Gender = Gender.Male,
            Qualification = "MBBS, MD, DM (Cardiology)",
            CreatedAt = TestData.Now.AddYears(-2),
        },
        new()
        {
            Id = RadiologistId,
            Code = "D0031",
            Title = "Dr.",
            FirstName = "Neha",
            LastName = "Joshi",
            DisplayName = "Dr. Neha Joshi",
            Gender = Gender.Female,
            Qualification = "MBBS, MD (Radiodiagnosis)",
            RegistrationCouncil = "Maharashtra Medical Council",
            RegistrationNumber = "MMC-2012-055321",
            CreatedAt = TestData.Now.AddYears(-2),
        },
        new()
        {
            Id = ResidentId,
            Code = "D0058",
            Title = "Dr.",
            FirstName = "Kiran",
            LastName = "Patil",
            DisplayName = "Dr. Kiran Patil",
            Gender = Gender.Male,
            Qualification = "MBBS",
            RegistrationCouncil = "Maharashtra Medical Council",
            RegistrationNumber = "MMC-2021-071002",
            CreatedAt = TestData.Now.AddYears(-1),
        },
    ];

    /// <summary>The user accounts of the doctors in radiology (user id to practitioner id); the clerk's is not a doctor's.</summary>
    public static IReadOnlyDictionary<Guid, Guid> RadiologyDoctors { get; } =
        new Dictionary<Guid, Guid> { [RadiologistUserId] = RadiologistId, [ResidentUserId] = ResidentId };

    public static IReadOnlyList<FacilityRow> Facilities { get; } =
    [
        new()
        {
            Id = TestData.FacilityId,
            Code = "NHP",
            Name = "Nana Hospitals, Pune",
            HfrId = "IN2710000123",
            Phone = "+912026001234",
            Email = "records@nanahospitals.example",
            Address = new Address { Line1 = "1 Hospital Road", City = "Pune", District = "Pune", State = "Maharashtra", PinCode = "411001" },
            CreatedAt = TestData.Now.AddYears(-4),
        },
    ];

    public static AbdmContext Context(VisitRow? visit = null, ClinicalEncounterRow? encounter = null, AbdmPdf? pdf = null) => new()
    {
        Patient = Patient(),
        Visit = visit,
        Encounter = encounter,
        Names = Names,
        Practitioners = Practitioners,
        Facilities = Facilities,
        FacilityId = TestData.FacilityId,
        Pdf = pdf,
    };

    public static VisitRow OpVisit() => new()
    {
        Id = OpVisitId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        VisitNumber = "NHOP2600871",
        Type = VisitType.OutPatient,
        Status = VisitStatus.Completed,
        DepartmentId = TestData.DepartmentId,
        PractitionerId = TestData.DoctorId,
        VisitedAt = Seen.AddMinutes(-20),
        CompletedAt = Seen.AddMinutes(40),
        ChiefComplaint = "Fever for three days",
        CreatedAt = Seen.AddMinutes(-20),
    };

    public static ClinicalEncounterRow Consultation() => new()
    {
        Id = EncounterId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        VisitId = OpVisitId,
        PractitionerId = TestData.DoctorId,
        DepartmentId = TestData.DepartmentId,
        Type = EncounterType.OutPatient,
        Status = EncounterStatus.Signed,
        StartedAt = Seen,
        SignedAt = Seen.AddMinutes(25),
        ChiefComplaint = "Fever for three days",
        CreatedAt = Seen,
        UpdatedAt = Seen.AddMinutes(25),
    };

    public static ConsultationNote Note() => new()
    {
        Id = EncounterId,
        PractitionerId = TestData.DoctorId,
        SignedByPractitionerId = TestData.DoctorId,
        Status = EncounterStatus.Signed,
        SignedAt = Seen.AddMinutes(25),
        ChiefComplaint = "Fever for three days",
        HistoryOfPresentIllness = "Intermittent fever up to 101 F for three days with body ache. No cough, rash or bleeding.",
        Examination = "Throat mildly congested, chest clear.",
        Assessment = "Acute febrile illness, probably viral.",
        Plan = "CBC and CRP today. Paracetamol for the fever, oral fluids.",
        FollowUpDate = TestData.Today.AddDays(3),
        FollowUpAdvice = "Come back if the fever lasts beyond two more days.",
        ReferralPractitionerId = CardiologistId,
        ReferralReason = "Palpitations on exertion",
        CreatedAt = Seen,
        UpdatedAt = Seen.AddMinutes(25),
    };

    public static IReadOnlyList<DiagnosisRow> ConsultationDiagnoses() =>
    [
        new()
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac001"),
            PatientId = TestData.PatientId,
            EncounterId = EncounterId,
            IcdCode = "R50.9",
            Title = "Fever, unspecified",
            Type = DiagnosisType.Provisional,
            IsPrimary = true,
            Status = DiagnosisStatus.Active,
            RecordedAt = Seen.AddMinutes(10),
            CreatedAt = Seen.AddMinutes(10),
        },
        new()
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac002"),
            PatientId = TestData.PatientId,
            EncounterId = EncounterId,
            Title = "Viral fever",
            Type = DiagnosisType.Differential,
            Status = DiagnosisStatus.Active,
            RecordedAt = Seen.AddMinutes(12),
            CreatedAt = Seen.AddMinutes(12),
        },
    ];

    public static IReadOnlyList<VitalSignsRow> Vitals() =>
    [
        new()
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac010"),
            PatientId = TestData.PatientId,
            EncounterId = EncounterId,
            RecordedAt = Seen.AddMinutes(5),
            Temperature = 100.4m,
            TemperatureUnit = TemperatureUnit.Fahrenheit,
            Pulse = 96,
            RespiratoryRate = 18,
            SystolicBp = 122,
            DiastolicBp = 78,
            SpO2 = 98,
            HeightCm = 162m,
            WeightKg = 64.5m,
            Bmi = 24.6m,
            CreatedAt = Seen.AddMinutes(5),
        },
    ];

    public static IReadOnlyList<AllergyRow> Allergies() =>
    [
        new()
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac020"),
            PatientId = TestData.PatientId,
            Allergen = "Penicillin",
            Type = AllergyType.Drug,
            Reaction = "Hives",
            Severity = AllergySeverity.Severe,
            Status = AllergyStatus.Active,
            RecordedAt = TestData.Now.AddYears(-1),
            CreatedAt = TestData.Now.AddYears(-1),
        },
        new()
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac021"),
            PatientId = TestData.PatientId,
            Allergen = "Peanuts",
            Type = AllergyType.Food,
            Severity = AllergySeverity.Mild,
            Status = AllergyStatus.EnteredInError,
            RecordedAt = TestData.Now.AddYears(-1),
            CreatedAt = TestData.Now.AddYears(-1),
        },
    ];

    public static IReadOnlyList<PrescriptionItemRow> PrescriptionLines() =>
    [
        Line(1, Guid.Parse("01923a4b-0000-7000-8000-0000000ac030"), "Dolo 650", "Paracetamol", "650 mg", "Tablet", "1 tab", DoseFrequency.TDS, 3, 9m, "After food"),
        Line(2, Guid.Parse("01923a4b-0000-7000-8000-0000000ac031"), "Cetirizine", "Cetirizine", "10 mg", "Tablet", "1 tab", DoseFrequency.HS, 5, 5m, null),
    ];

    public static PrescriptionData Prescription(AbdmPdf? pdf) => new(
        Context(encounter: Consultation(), pdf: pdf), PrescriptionId, "NHRX2600873", TestData.DoctorId, Seen.AddMinutes(20), Seen.AddMinutes(22),
        "Drink plenty of fluids and rest.", Seen.AddMinutes(20), Seen.AddMinutes(22), PrescriptionLines());

    public static ConsultationData ConsultationRecord() => new(
        Context(encounter: Consultation()), Note(), ConsultationDiagnoses(), Vitals(), PrescriptionLines(), Allergies(), [ConsultationProcedure()]);

    /// <summary>An abscess drained in the consultation, described in words by the doctor who did it.</summary>
    public static ProcedureRow ConsultationProcedure() => new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac0a0"),
        PatientId = TestData.PatientId,
        EncounterId = EncounterId,
        VisitId = OpVisitId,
        PerformedAt = Seen.AddMinutes(15),
        Display = "Incision and drainage of abscess",
        PerformerPractitionerId = TestData.DoctorId,
        Side = Platform.Contracts.ProcedureSide.Right,
        BodySite = "Axilla",
        Outcome = "Pus drained; wound packed",
        Source = ProcedureSource.Recorded,
        Status = ProcedureStatus.Completed,
        CreatedAt = Seen.AddMinutes(18),
    };

    /// <summary>A stent placed in the cath lab during the stay, by the cardiologist, as the theatre recorded it.</summary>
    public static ProcedureRow StayProcedure() => new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac0a1"),
        PatientId = TestData.PatientId,
        AdmissionId = AdmissionId,
        VisitId = StayVisitId,
        PerformedAt = Admitted.AddHours(20),
        Code = "027034Z",
        CodeSystem = CodeReleases.Icd10Pcs,
        Display = "Coronary angioplasty with a drug-eluting stent to one artery",
        PerformerPractitionerId = CardiologistId,
        Side = Platform.Contracts.ProcedureSide.NotApplicable,
        BodySite = "Left anterior descending artery",
        Outcome = "TIMI 3 flow restored",
        Source = ProcedureSource.OperationTheatre,
        Status = ProcedureStatus.Completed,
        CreatedAt = Admitted.AddHours(22),
    };

    public static LabReportRow LabReport() => new()
    {
        Id = LabReportId,
        PatientId = TestData.PatientId,
        LabOrderId = LabOrderId,
        VisitId = OpVisitId,
        ReportNumber = "NHLB2600342",
        Status = LabReportStatus.Released,
        OrderedAt = Seen.AddMinutes(20),
        CollectedAt = Seen.AddMinutes(45),
        ReleasedAt = TestData.Now.AddMinutes(-10),
        ResultIds = [Guid.Parse("01923a4b-0000-7000-8000-0000000ac040"), Guid.Parse("01923a4b-0000-7000-8000-0000000ac041")],
        TestNames = ["Haemoglobin", "C-reactive protein"],
        CreatedAt = Seen.AddMinutes(20),
        UpdatedAt = TestData.Now.AddMinutes(-10),
    };

    public static IReadOnlyList<LabResultRow> LabResults() =>
    [
        Result(Guid.Parse("01923a4b-0000-7000-8000-0000000ac040"), LabOrderId, OpVisitId, "HB", "Haemoglobin", "718-7", "Hemoglobin [Mass/volume] in Blood",
            "11.2", 11.2m, "g/dL", ResultFlag.Low, "12.0 - 15.0"),
        Result(Guid.Parse("01923a4b-0000-7000-8000-0000000ac041"), LabOrderId, OpVisitId, "CRP", "C-reactive protein", "1988-5",
            "C reactive protein [Mass/volume] in Serum or Plasma", "18", 18m, "mg/L", ResultFlag.High, "< 6"),
    ];

    public static LabReportData LabReportRecord(AbdmPdf? pdf) => new(Context(visit: OpVisit(), pdf: pdf), LabReport(), LabResults());

    /// <summary>
    /// The consultation's chest X-ray: its images linked from PACS, the report written by the resident, signed off by the
    /// radiologist and released.
    /// </summary>
    public static RadiologyReportRow RadiologyReport() => new()
    {
        Id = RadiologyReportId,
        PatientId = TestData.PatientId,
        ImagingStudyId = ImagingStudyId,
        VisitId = OpVisitId,
        AccessionNumber = "NHRD2600217",
        ServiceCode = "RAD-XR-CHEST",
        StudyName = "X-ray chest PA view",
        Modality = Modality.XR,
        BodyPart = "Chest",
        OrderedAt = Seen.AddMinutes(20),
        StartedAt = Seen.AddMinutes(48),
        AcquiredAt = Seen.AddMinutes(52),
        StudyInstanceUid = "2.25.119382716123407654321098765432109876",
        SeriesCount = 1,
        InstanceCount = 2,
        Technique = "PA view, erect, in full inspiration.",
        ClinicalHistory = "Fever for three days with body ache.",
        Findings = "Both lung fields are clear. Cardiac size is normal. Both costophrenic angles are clear.",
        Impression = "No active lung lesion.",
        ReportedBy = ResidentUserId,
        VerifiedBy = RadiologistUserId,
        VerifiedAt = TestData.Now.AddMinutes(-50),
        ReleasedAt = TestData.Now.AddMinutes(-45),
        CreatedAt = Seen.AddMinutes(60),
        UpdatedAt = TestData.Now.AddMinutes(-45),
    };

    public static RadiologyReportData RadiologyReportRecord(AbdmPdf? pdf) =>
        new(Context(visit: OpVisit(), pdf: pdf), RadiologyReport(), RadiologyDoctors);

    /// <summary>A resting 12-lead ECG of the out-patient visit, recorded by a technician and reported and signed off by the cardiologist.</summary>
    public static CardiologyReportRow EcgReport() => new()
    {
        Id = CardiologyReportId,
        PatientId = TestData.PatientId,
        CardiologyStudyId = CardiologyStudyId,
        VisitId = OpVisitId,
        AccessionNumber = "NHCD2600031",
        ServiceCode = "CARD-ECG",
        StudyName = "ECG 12 lead",
        StudyType = CardiologyStudyType.Ecg,
        OrderedAt = Seen.AddMinutes(20),
        AcquiredAt = Seen.AddMinutes(35),
        Ecg = new EcgFindings
        {
            HeartRate = 78,
            Rhythm = "Sinus rhythm",
            PrInterval = 160,
            QrsDuration = 88,
            QtInterval = 380,
            QtcInterval = 420,
            Axis = "Normal",
            StTChanges = "None",
            Interpretation = "Normal ECG",
        },
        Conclusion = "Normal sinus rhythm. No ST-T changes.",
        ReportedBy = CardiologistUserId,
        VerifiedBy = CardiologistUserId,
        VerifiedAt = TestData.Now.AddMinutes(-40),
        ReleasedAt = TestData.Now.AddMinutes(-38),
        CreatedAt = Seen.AddMinutes(40),
        UpdatedAt = TestData.Now.AddMinutes(-38),
    };

    public static IReadOnlyDictionary<Guid, Guid> CardiologyDoctors { get; } = new Dictionary<Guid, Guid> { [CardiologistUserId] = CardiologistId };

    public static CardiologyReportData CardiologyReportRecord(AbdmPdf? pdf) =>
        new(Context(visit: OpVisit(), pdf: pdf), EcgReport(), CardiologyDoctors);

    /// <summary>
    /// The first dose of Td (tetanus and adult diphtheria), given by the OPD nurse in the consultation, with the second
    /// dose due four weeks later, as the adult schedule has it.
    /// </summary>
    public static ImmunizationRow TdDose() => new()
    {
        Id = DoseId,
        PatientId = TestData.PatientId,
        EncounterId = EncounterId,
        VaccineName = "Td",
        VaccineCode = "TD",
        DoseNumber = 1,
        AdministeredOn = new DateOnly(2026, 9, 25),
        Route = "IM",
        Site = "Left deltoid",
        BatchNumber = "TD2608-114",
        Manufacturer = "Biological E. Limited",
        ExpiryDate = new DateOnly(2027, 8, 31),
        AdministeredByUserName = "Sr. Meena Pillai",
        IsExternal = false,
        NextDueOn = new DateOnly(2026, 10, 23),
        CreatedAt = Seen.AddMinutes(30),
    };

    public static ImmunizationData ImmunizationRecord(ImmunizationRow? dose = null) =>
        new(Context(encounter: Consultation()), dose ?? TdDose());

    /// <summary>
    /// The progress note Dr. Asha Rao wrote in the consultation and signed at 9:20 IST, in two paragraphs, as the FHIR API
    /// serves it (who wrote and signed it named from their accounts).
    /// </summary>
    public static ClinicalNoteRow ProgressNote() => new()
    {
        Id = NoteId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        EncounterId = EncounterId,
        Type = ClinicalNoteType.ProgressNote,
        Title = "Fever review",
        Body = "Fever settled since last evening; tolerating oral fluids.\r\n\r\nPlan: paracetamol only when needed. Review with the CBC report.",
        AuthorPractitionerId = TestData.DoctorId,
        AuthorUserId = DoctorUserId,
        AuthorUserName = "Asha Rao",
        SignedByUserId = DoctorUserId,
        SignedByPractitionerId = TestData.DoctorId,
        SignedByUserName = "Asha Rao",
        SignedAt = Seen.AddMinutes(50),
        CreatedAt = Seen.AddMinutes(40),
        UpdatedAt = Seen.AddMinutes(50),
    };

    /// <summary>The cardiologist's addendum to the progress note, signed the next morning at 8:30 IST.</summary>
    public static ClinicalNoteRow Addendum() => ProgressNote() with
    {
        Id = AddendumId,
        Title = "Addendum to Fever review",
        Body = "ECG reviewed: sinus rhythm, no ischaemic change. No cardiology follow-up needed.",
        AuthorPractitionerId = CardiologistId,
        AuthorUserId = CardiologistUserId,
        AuthorUserName = "Vikram Shah",
        SignedByUserId = CardiologistUserId,
        SignedByPractitionerId = CardiologistId,
        SignedByUserName = "Vikram Shah",
        AmendsNoteId = NoteId,
        SignedAt = Seen.AddDays(1),
        CreatedAt = Seen.AddDays(1).AddMinutes(-10),
        UpdatedAt = Seen.AddDays(1),
    };

    public static ClinicalNoteData ClinicalNoteRecord(params ClinicalNoteRow[] addenda) =>
        new(Context(encounter: Consultation()), ProgressNote(), addenda);

    /// <summary>An instant in India in 2026 (IST, UTC+05:30).</summary>
    public static DateTimeOffset Ist(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, TimeSpan.FromMinutes(330));

    /// <summary>
    /// The patient's September readings at home, each received two minutes after it was measured: a blood pressure (high)
    /// and pulse the monitor took together; an SpO2; a temperature the patient read out on a call, which a nurse entered;
    /// a weight; a fasting glucose beyond the critical limit; and an ECG patch's rhythm (atrial fibrillation suspected).
    /// </summary>
    public static IReadOnlyList<DeviceReadingRow> HomeReadings() =>
    [
        Reading(0x101, RpmReadingType.SystolicBp, 150m, Ist(9, 2, 7, 30), ReadingSeverity.Warning, BpMonitor),
        Reading(0x102, RpmReadingType.DiastolicBp, 95m, Ist(9, 2, 7, 30), ReadingSeverity.Warning, BpMonitor),
        Reading(0x103, RpmReadingType.HeartRate, 78m, Ist(9, 2, 7, 30), ReadingSeverity.Normal, BpMonitor),
        Reading(0x104, RpmReadingType.SpO2, 97m, Ist(9, 3, 8, 0), ReadingSeverity.Normal, Oximeter),
        Reading(0x105, RpmReadingType.Temperature, 37.9m, Ist(9, 5, 21, 15), ReadingSeverity.Normal, null) with
        {
            Source = ReadingSource.Manual,
            RecordedByUserId = NurseUserId,
            RecordedByUserName = "Priya Nair",
        },
        Reading(0x106, RpmReadingType.Weight, 72.4m, Ist(9, 10, 7, 0), ReadingSeverity.Normal, Scale),
        Reading(0x107, RpmReadingType.Glucose, 182m, Ist(9, 12, 7, 5), ReadingSeverity.Critical, Glucometer),
        Reading(0x108, RpmReadingType.EcgRhythm, 2m, Ist(9, 20, 23, 50), ReadingSeverity.Critical, Patch) with { TextValue = "Atrial fibrillation suspected" },
    ];

    /// <summary>The September readings as one document: the whole month, or <paramref name="span"/>.</summary>
    public static HomeReadingsData HomeReadingsRecord(IReadOnlyList<DeviceReadingRow>? readings = null, ReadingsSpan? span = null) =>
        new(Context(), EnrollmentId, Programme, span ?? ReadingsSpan.Of(new DateOnly(2026, 9, 1)), readings ?? HomeReadings());

    /// <summary>A reading from the patient's devices, numbered for its id.</summary>
    public static DeviceReadingRow Reading(
        int number, RpmReadingType type, decimal value, DateTimeOffset measuredAt, ReadingSeverity severity, (RpmDeviceType Type, string Maker, string Model, string Serial)? device) => new()
        {
            Id = Guid.Parse($"01923a4b-0000-7000-8000-0000000bd{number:x3}"),
            PatientId = TestData.PatientId,
            EnrollmentId = EnrollmentId,
            Type = type,
            Value = value,
            MeasuredAt = measuredAt.ToUniversalTime(),
            ReceivedAt = measuredAt.ToUniversalTime().AddMinutes(2),
            Severity = severity,
            Source = ReadingSource.Device,
            DeviceId = device is null ? null : Guid.Parse($"01923a4b-0000-7000-8000-0000000be{(int)device.Value.Type:x3}"),
            DeviceType = device?.Type,
            DeviceManufacturer = device?.Maker,
            DeviceModel = device?.Model,
            DeviceSerial = device?.Serial,
        };

    private static readonly (RpmDeviceType, string, string, string) BpMonitor = (RpmDeviceType.BpMonitor, "Omron", "HEM-7156", "OM2609-0041");
    private static readonly (RpmDeviceType, string, string, string) Oximeter = (RpmDeviceType.PulseOximeter, "BPL", "Smart Oxy", "BPL-55120");
    private static readonly (RpmDeviceType, string, string, string) Scale = (RpmDeviceType.WeightScale, "Omron", "HN-289", "OM2607-7781");
    private static readonly (RpmDeviceType, string, string, string) Glucometer = (RpmDeviceType.Glucometer, "Accu-Chek", "Instant", "AC-30981");
    private static readonly (RpmDeviceType, string, string, string) Patch = (RpmDeviceType.EcgPatch, "Cardiotrack", "Patch 2", "CT-00412");

    public static VisitRow StayVisit() => new()
    {
        Id = StayVisitId,
        PatientId = TestData.PatientId,
        FacilityId = TestData.FacilityId,
        VisitNumber = "NHIP2600112",
        Type = VisitType.InPatient,
        Status = VisitStatus.Completed,
        DepartmentId = TestData.DepartmentId,
        PractitionerId = CardiologistId,
        VisitedAt = Admitted,
        CompletedAt = Discharged,
        ChiefComplaint = "Chest pain",
        CreatedAt = Admitted,
        Admission = new AdmissionRow
        {
            Id = AdmissionId,
            AdmissionNumber = "NHAD2600112",
            Type = AdmissionType.Emergency,
            Source = AdmissionSource.Emergency,
            Status = AdmissionStatus.Discharged,
            AdmittedAt = Admitted,
            AdmittingPractitionerId = CardiologistId,
            ConsultantPractitionerId = CardiologistId,
            DepartmentId = TestData.DepartmentId,
            DischargeType = DischargeType.Normal,
            DischargedAt = Discharged,
            CreatedAt = Admitted,
            UpdatedAt = Discharged,
        },
    };

    public static DischargeSummaryRow Summary() => new()
    {
        Id = SummaryId,
        PatientId = TestData.PatientId,
        AdmissionId = AdmissionId,
        PractitionerId = CardiologistId,
        SignedByPractitionerId = CardiologistId,
        AdmittedAt = Admitted,
        DischargedAt = Discharged,
        Diagnoses = "Non-ST elevation myocardial infarction",
        DiagnosisCodes = ["I21.4"],
        PresentingComplaints = "Central chest pain for two hours, sweating.",
        CourseInHospital = "Admitted to the CCU. Coronary angiography showed a tight LAD stenosis, treated with one drug-eluting stent. Pain settled; no arrhythmia.",
        Procedures = "Coronary angiography; PCI to the LAD with one drug-eluting stent.",
        InvestigationsSummary = "Troponin I raised; ECG with ST depression in V4 to V6.",
        ConditionAtDischarge = "Stable, pain free.",
        Medications = "Aspirin 75 mg once daily; Atorvastatin 40 mg at night.",
        Advice = "Low-salt, low-fat diet. No heavy exertion for four weeks.",
        FollowUpDate = TestData.Today.AddDays(7),
        FollowUpInstructions = "Cardiology OPD with the angiography report.",
        Status = DischargeSummaryStatus.Signed,
        SignedAt = Discharged.AddHours(-2),
        CreatedAt = Discharged.AddHours(-6),
        UpdatedAt = Discharged.AddHours(-2),
    };

    public static DischargeSummaryData DischargeSummaryRecord(AbdmPdf? pdf) => new(
        Context(visit: StayVisit(), pdf: pdf),
        Summary(),
        [
            new()
            {
                Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac050"),
                PatientId = TestData.PatientId,
                EncounterId = Guid.Parse("01923a4b-0000-7000-8000-0000000ac051"),
                IcdCode = "I21.4",
                Title = "Non-ST elevation (NSTEMI) myocardial infarction",
                Type = DiagnosisType.Final,
                IsPrimary = true,
                Status = DiagnosisStatus.Active,
                RecordedAt = Admitted.AddHours(3),
                CreatedAt = Admitted.AddHours(3),
            },
        ],
        [
            Line(1, Guid.Parse("01923a4b-0000-7000-8000-0000000ac060"), "Atorvastatin", "Atorvastatin", "40 mg", "Tablet", "1 tab", DoseFrequency.HS, 30, 30m, null) with
            {
                PrescriptionId = Guid.Parse("01923a4b-0000-7000-8000-0000000ac061"),
                PrescriptionNumber = "NHRX2600120",
                EncounterId = null,
                AdmissionId = AdmissionId,
                PractitionerId = CardiologistId,
                IsInpatient = true,
                PrescribedAt = Discharged.AddHours(-3),
                SignedAt = Discharged.AddHours(-3),
                CreatedAt = Discharged.AddHours(-3),
            },
        ],
        Allergies(),
        [
            new()
            {
                Id = StayLabReportId,
                PatientId = TestData.PatientId,
                LabOrderId = StayLabOrderId,
                VisitId = StayVisitId,
                ReportNumber = "NHLB2600101",
                Status = LabReportStatus.Released,
                OrderedAt = Admitted.AddMinutes(10),
                CollectedAt = Admitted.AddMinutes(20),
                ReleasedAt = Admitted.AddHours(2),
                ResultIds = [Guid.Parse("01923a4b-0000-7000-8000-0000000ac070")],
                TestNames = ["Troponin I"],
                CreatedAt = Admitted.AddMinutes(10),
                UpdatedAt = Admitted.AddHours(2),
            },
        ],
        [
            Result(Guid.Parse("01923a4b-0000-7000-8000-0000000ac070"), StayLabOrderId, StayVisitId, "TROPI", "Troponin I", "10839-9",
                "Troponin I.cardiac [Mass/volume] in Serum or Plasma", "2.4", 2.4m, "ng/mL", ResultFlag.CriticalHigh, "< 0.04") with
            {
                OrderedAt = Admitted.AddMinutes(10),
                CollectedAt = Admitted.AddMinutes(20),
                VerifiedAt = Admitted.AddHours(2),
                ReleasedAt = Admitted.AddHours(2),
                CreatedAt = Admitted.AddHours(1),
            },
        ],
        Procedures: [StayProcedure()]);

    private static PrescriptionItemRow Line(
        int sequence, Guid id, string drug, string generic, string strength, string form, string dose, DoseFrequency frequency, int days, decimal quantity,
        string? instructions) => new()
        {
            Id = id,
            PatientId = TestData.PatientId,
            PrescriptionId = PrescriptionId,
            PrescriptionNumber = "NHRX2600873",
            EncounterId = EncounterId,
            VisitId = OpVisitId,
            PractitionerId = TestData.DoctorId,
            Status = PrescriptionStatus.Signed,
            PrescribedAt = Seen.AddMinutes(20),
            SignedAt = Seen.AddMinutes(22),
            Sequence = sequence,
            DrugName = drug,
            GenericName = generic,
            Strength = strength,
            DosageForm = form,
            Route = "Oral",
            Dose = dose,
            Frequency = frequency,
            DurationDays = days,
            Quantity = quantity,
            Instructions = instructions,
            CreatedAt = Seen.AddMinutes(20),
        };

    private static LabResultRow Result(
        Guid id, Guid order, Guid visit, string code, string name, string loinc, string loincName, string value, decimal number, string unit, ResultFlag flag,
        string range) => new()
        {
            Id = id,
            PatientId = TestData.PatientId,
            LabOrderId = order,
            VisitId = visit,
            TestCode = code,
            TestName = name,
            Loinc = loinc,
            LoincName = loincName,
            ResultType = LabResultType.Numeric,
            ValueText = value,
            NumericValue = number,
            Unit = unit,
            Flag = flag,
            ReferenceText = range,
            OrderedAt = Seen.AddMinutes(20),
            CollectedAt = Seen.AddMinutes(45),
            VerifiedAt = TestData.Now.AddMinutes(-15),
            ReleasedAt = TestData.Now.AddMinutes(-10),
            CreatedAt = Seen.AddMinutes(50),
        };
}
