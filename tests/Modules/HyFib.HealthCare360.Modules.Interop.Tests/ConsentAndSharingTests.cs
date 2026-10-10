using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class RecordSharingConsentTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_record_sharing_consent_is_the_data_sharing_consent() =>
        RecordSharingConsent.Type.ShouldBe(ConsentType.DataSharing);

    [Fact]
    public void No_consent_on_file_means_no_sharing() => RecordSharingConsent.IsGranted([]).ShouldBeFalse();

    [Fact]
    public void A_granted_consent_allows_sharing() =>
        RecordSharingConsent.IsGranted([new ConsentFact(true, Monday, null)]).ShouldBeTrue();

    [Fact]
    public void A_revoked_consent_does_not() =>
        RecordSharingConsent.IsGranted([new ConsentFact(true, Monday, Monday.AddDays(1))]).ShouldBeFalse();

    [Fact]
    public void A_later_refusal_supersedes_an_earlier_grant() =>
        RecordSharingConsent.IsGranted([new ConsentFact(true, Monday, null), new ConsentFact(false, Monday.AddDays(1), null)]).ShouldBeFalse();

    [Fact]
    public void A_grant_after_a_refusal_restores_sharing() =>
        RecordSharingConsent.IsGranted([new ConsentFact(false, Monday, null), new ConsentFact(true, Monday.AddDays(1), null)]).ShouldBeTrue();

    [Fact]
    public void A_refusal_alone_is_not_a_consent() =>
        RecordSharingConsent.IsGranted([new ConsentFact(false, Monday, null)]).ShouldBeFalse();

    [Fact]
    public void The_query_form_applies_the_same_rules_per_patient_and_only_to_data_sharing()
    {
        Patient[] patients = [Patient("A"), Patient("B"), Patient("C"), Patient("D"), Patient("E")];
        PatientConsent[] consents =
        [
            Consent(patients[0], ConsentType.DataSharing, true, Monday),
            Consent(patients[1], ConsentType.DataSharing, true, Monday, revokedAt: Monday.AddDays(2)),
            Consent(patients[2], ConsentType.DataSharing, true, Monday),
            Consent(patients[2], ConsentType.DataSharing, false, Monday.AddDays(3)),
            Consent(patients[3], ConsentType.GeneralTreatment, true, Monday),
            Consent(patients[3], ConsentType.MarketingCommunication, true, Monday),
            Consent(patients[4], ConsentType.DataSharing, false, Monday),
            Consent(patients[4], ConsentType.DataSharing, true, Monday.AddDays(1)),
        ];

        var shared = patients.AsQueryable().Where(RecordSharingConsent.IsGranted(consents.AsQueryable())).Select(p => p.Uhid).ToList();

        shared.ShouldBe(["A", "E"]);
    }

    [Theory]
    [InlineData(false, false, null, false, false, nameof(SharingDecision.NotFound))]
    [InlineData(true, true, null, true, true, nameof(SharingDecision.Deleted))]
    [InlineData(true, false, "merged", true, true, nameof(SharingDecision.Merged))]
    [InlineData(true, false, null, false, true, nameof(SharingDecision.NoConsent))]
    [InlineData(true, false, null, false, false, nameof(SharingDecision.NoConsent))]
    [InlineData(true, false, null, true, false, nameof(SharingDecision.Restricted))]
    [InlineData(true, false, null, true, true, nameof(SharingDecision.Allowed))]
    public void Applies_lifecycle_then_consent_then_the_restricted_record_guard(
        bool exists, bool deleted, string? merged, bool consent, bool canOpen, string expected) =>
        PatientSharingRules.Decide(new PatientSharingFacts(exists, deleted, merged is null ? null : TestData.OtherPatientId, consent, canOpen)).ToString().ShouldBe(expected);

    internal static Patient Patient(string uhid, bool vip = false) => new()
    {
        Uhid = uhid,
        FirstName = uhid,
        FullName = $"Patient {uhid}",
        IsVip = vip,
        RegisteredAt = Monday,
        CreatedAt = Monday,
    };

    internal static PatientConsent Consent(Patient patient, ConsentType type, bool granted, DateTimeOffset capturedAt, DateTimeOffset? revokedAt = null) => new()
    {
        PatientId = patient.Id,
        Type = type,
        Granted = granted,
        CapturedAt = capturedAt,
        RevokedAt = revokedAt,
    };
}

public sealed class PatientSearchQueryTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);

    private static Expression<Func<Patient, bool>> Contains(string word) => p => p.FullName.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static List<string> Run(IEnumerable<Patient> patients, IEnumerable<PatientConsent> consents, string query, bool includeRestricted = false)
    {
        var request = TestData.Search("Patient", query);
        var criteria = PatientCriteria.From(request, abha => ["idx:" + abha]);
        return [.. PatientSearchQuery.Apply(patients.AsQueryable(), consents.AsQueryable(), criteria, includeRestricted, Contains).Select(p => p.Uhid)];
    }

    private static (List<Patient> Patients, List<PatientConsent> Consents) Consenting(params Patient[] patients) =>
        ([.. patients], [.. patients.Select(p => RecordSharingConsentTests.Consent(p, ConsentType.DataSharing, true, Monday))]);

    [Fact]
    public void Patients_without_record_sharing_consent_are_excluded()
    {
        var (patients, consents) = Consenting(RecordSharingConsentTests.Patient("NH1"));
        patients.Add(RecordSharingConsentTests.Patient("NH2"));

        Run(patients, consents, "name=patient").ShouldBe(["NH1"]);
    }

    [Fact]
    public void Merged_and_inactive_records_are_excluded()
    {
        var merged = RecordSharingConsentTests.Patient("NH2");
        merged.MarkMergedInto(Guid.CreateVersion7());
        var inactive = RecordSharingConsentTests.Patient("NH3");
        inactive.IsActive = false;
        var (patients, consents) = Consenting(RecordSharingConsentTests.Patient("NH1"), merged, inactive);

        Run(patients, consents, "name=patient").ShouldBe(["NH1"]);
    }

    [Fact]
    public void Restricted_patients_are_found_only_by_callers_who_may_open_them()
    {
        var (patients, consents) = Consenting(RecordSharingConsentTests.Patient("NH1"), RecordSharingConsentTests.Patient("VIP1", vip: true));

        Run(patients, consents, "name=patient").ShouldBe(["NH1"]);
        Run(patients, consents, "name=patient", includeRestricted: true).ShouldBe(["NH1", "VIP1"]);
    }

    [Fact]
    public void Finds_a_patient_by_uhid_with_or_without_the_system()
    {
        var (patients, consents) = Consenting(RecordSharingConsentTests.Patient("NH-UH-1"), RecordSharingConsentTests.Patient("NH-UH-2"));

        Run(patients, consents, "identifier=urn:hc360:uhid|nh-uh-2").ShouldBe(["NH-UH-2"]);
        Run(patients, consents, "identifier=NH-UH-1").ShouldBe(["NH-UH-1"]);
        Run(patients, consents, "identifier=http://other.example|NH-UH-1").ShouldBeEmpty();
    }

    [Fact]
    public void Finds_a_patient_by_abha_number_through_the_blind_index()
    {
        var withAbha = RecordSharingConsentTests.Patient("NH-UH-1");
        withAbha.AbhaNumberIndex = "idx:91123456789012";
        var (patients, consents) = Consenting(withAbha, RecordSharingConsentTests.Patient("NH-UH-2"));

        Run(patients, consents, "identifier=https://healthid.ndhm.gov.in|91-1234-5678-9012").ShouldBe(["NH-UH-1"]);
        Run(patients, consents, "identifier=91-1234-5678-9012").ShouldBe(["NH-UH-1"]);
        Run(patients, consents, "identifier=https://healthid.ndhm.gov.in|NH-UH-2").ShouldBeEmpty();
    }

    [Fact]
    public void Filters_by_gender_and_birth_date()
    {
        var woman = RecordSharingConsentTests.Patient("W");
        woman.Gender = Gender.Female;
        woman.DateOfBirth = new DateOnly(1984, 4, 12);
        var man = RecordSharingConsentTests.Patient("M");
        man.Gender = Gender.Male;
        man.DateOfBirth = new DateOnly(1990, 1, 1);
        var (patients, consents) = Consenting(woman, man);

        Run(patients, consents, "gender=female&name=patient").ShouldBe(["W"]);
        Run(patients, consents, "gender=male,female&name=patient").ShouldBe(["W", "M"]);
        Run(patients, consents, "birthdate=1984").ShouldBe(["W"]);
        Run(patients, consents, "birthdate=1984-04-12").ShouldBe(["W"]);
        Run(patients, consents, "birthdate=ge1985-01-01").ShouldBe(["M"]);
        Run(patients, consents, "birthdate=lt1985").ShouldBe(["W"]);
        Run(patients, consents, "birthdate=ne1984").ShouldBe(["M"]);
    }

    [Fact]
    public void Filters_by_last_update()
    {
        var recent = RecordSharingConsentTests.Patient("R");
        recent.UpdatedAt = Monday.AddDays(3);
        var (patients, consents) = Consenting(recent, RecordSharingConsentTests.Patient("O"));

        Run(patients, consents, "_lastUpdated=ge2026-09-23").ShouldBe(["R"]);
        Run(patients, consents, "_lastUpdated=lt2026-09-22").ShouldBe(["O"]);
    }

    [Fact]
    public void Name_words_must_all_match_and_alternatives_are_either()
    {
        var asha = RecordSharingConsentTests.Patient("A");
        asha.FullName = "Asha Kulkarni";
        var ravi = RecordSharingConsentTests.Patient("R");
        ravi.FullName = "Ravi Kumar";
        var (patients, consents) = Consenting(asha, ravi);

        Run(patients, consents, "name=asha kulk").ShouldBe(["A"]);
        Run(patients, consents, "name=asha kumar").ShouldBeEmpty();
        Run(patients, consents, "name=asha,ravi").ShouldBe(["A", "R"]);
    }

    [Fact]
    public void Searches_by_id()
    {
        var (patients, consents) = Consenting(RecordSharingConsentTests.Patient("NH1"), RecordSharingConsentTests.Patient("NH2"));

        Run(patients, consents, $"_id={patients[1].Id:D}").ShouldBe(["NH2"]);
        Run(patients, consents, "_id=not-a-guid").ShouldBeEmpty();
    }

    [Fact]
    public void Orders_newest_registrations_first()
    {
        var older = RecordSharingConsentTests.Patient("OLD");
        var newer = RecordSharingConsentTests.Patient("NEW");
        newer.RegisteredAt = Monday.AddDays(1);

        PatientSearchQuery.Ordered(new[] { older, newer }.AsQueryable()).Select(p => p.Uhid).ShouldBe(["NEW", "OLD"]);
    }
}
