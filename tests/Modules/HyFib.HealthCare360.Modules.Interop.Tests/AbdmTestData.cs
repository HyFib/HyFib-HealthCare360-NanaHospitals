using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// One patient's records for the ABDM document tests: an out-patient consultation with its prescription and lab report,
/// and an earlier admission with its discharge summary. The bundles built from them are the sample documents in
/// Fixtures/Abdm (validated with the HL7 FHIR validator against the NRCeS IG).
/// </summary>
internal static class AbdmTestData
{
    /// <summary>A valid ABHA number (the last digit is its Verhoeff check digit).</summary>
    public const string AbhaNumber = "91123456789010";

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
        Practitioners = new Dictionary<Guid, string> { [TestData.DoctorId] = "Dr. Asha Rao", [CardiologistId] = "Dr. Vikram Shah" },
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
        },
    ];

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
        Today = TestData.Today,
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
        Context(encounter: Consultation(), pdf: pdf), PrescriptionId, TestData.DoctorId, Seen.AddMinutes(20), Seen.AddMinutes(22),
        "Drink plenty of fluids and rest.", Seen.AddMinutes(20), Seen.AddMinutes(22), PrescriptionLines());

    public static ConsultationData ConsultationRecord() => new(
        Context(encounter: Consultation()), Note(), ConsultationDiagnoses(), Vitals(), PrescriptionLines(), Allergies());

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
        Result(Guid.Parse("01923a4b-0000-7000-8000-0000000ac040"), LabOrderId, OpVisitId, "HB", "Haemoglobin", "718-7", "11.2", 11.2m, "g/dL", ResultFlag.Low, "12.0 - 15.0"),
        Result(Guid.Parse("01923a4b-0000-7000-8000-0000000ac041"), LabOrderId, OpVisitId, "CRP", "C-reactive protein", "1988-5", "18", 18m, "mg/L", ResultFlag.High, "< 6"),
    ];

    public static LabReportData LabReportRecord(AbdmPdf? pdf) => new(Context(visit: OpVisit(), pdf: pdf), LabReport(), LabResults());

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
        CourseInHospital = "Managed medically in the CCU. Pain settled; no arrhythmia. Coronary angiography advised as an out-patient.",
        Procedures = "None",
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
            Result(Guid.Parse("01923a4b-0000-7000-8000-0000000ac070"), StayLabOrderId, StayVisitId, "TROPI", "Troponin I", "10839-9", "2.4", 2.4m, "ng/mL",
                ResultFlag.CriticalHigh, "< 0.04") with
            {
                OrderedAt = Admitted.AddMinutes(10),
                CollectedAt = Admitted.AddMinutes(20),
                VerifiedAt = Admitted.AddHours(2),
                ReleasedAt = Admitted.AddHours(2),
                CreatedAt = Admitted.AddHours(1),
            },
        ]);

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
        Guid id, Guid order, Guid visit, string code, string name, string loinc, string value, decimal number, string unit, ResultFlag flag, string range) => new()
        {
            Id = id,
            PatientId = TestData.PatientId,
            LabOrderId = order,
            VisitId = visit,
            TestCode = code,
            TestName = name,
            Loinc = loinc,
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
