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

    /// <summary>The integration accounts of two other providers, and the hospital's date the checks run on.</summary>
    internal static readonly Guid PartnerA = Guid.Parse("0192a1b2-0000-7000-8000-00000000a001");
    internal static readonly Guid PartnerB = Guid.Parse("0192a1b2-0000-7000-8000-00000000b002");
    internal static readonly DateOnly Today = new(2026, 9, 24);

    /// <summary>Partner A, reading for treatment.</summary>
    internal static SharingReader Reader { get; } = new(PartnerA, SharingPurpose.Treatment, Today);

    [Fact]
    public void The_record_sharing_consent_is_the_data_sharing_consent() =>
        RecordSharingConsent.Type.ShouldBe(ConsentType.DataSharing);

    [Fact]
    public void No_consent_on_file_means_no_sharing() => RecordSharingConsent.IsGranted([], Reader).ShouldBeFalse();

    [Fact]
    public void A_granted_consent_allows_sharing() =>
        RecordSharingConsent.IsGranted([new ConsentFact(true, Monday, null)], Reader).ShouldBeTrue();

    [Fact]
    public void A_revoked_consent_does_not() =>
        RecordSharingConsent.IsGranted([new ConsentFact(true, Monday, Monday.AddDays(1))], Reader).ShouldBeFalse();

    [Fact]
    public void A_later_refusal_supersedes_an_earlier_grant() =>
        RecordSharingConsent.IsGranted([new ConsentFact(true, Monday, null), new ConsentFact(false, Monday.AddDays(1), null)], Reader).ShouldBeFalse();

    [Fact]
    public void A_grant_after_a_refusal_restores_sharing() =>
        RecordSharingConsent.IsGranted([new ConsentFact(false, Monday, null), new ConsentFact(true, Monday.AddDays(1), null)], Reader).ShouldBeTrue();

    [Fact]
    public void A_refusal_alone_is_not_a_consent() =>
        RecordSharingConsent.IsGranted([new ConsentFact(false, Monday, null)], Reader).ShouldBeFalse();

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

        var shared = patients.AsQueryable().Where(RecordSharingConsent.IsGranted(consents.AsQueryable(), Reader)).Select(p => p.Uhid).ToList();

        shared.ShouldBe(["A", "E"]);
    }

    [Fact]
    public void A_consent_without_a_scope_admits_every_account_for_any_purpose_or_none_and_covers_every_record_but_counselling_notes()
    {
        foreach (var reader in new[] { Reader, new SharingReader(PartnerB, SharingPurpose.Payment, Today), new SharingReader(PartnerB, null, Today) })
        {
            var scopes = RecordSharingConsent.ScopesFor([new ConsentFact(true, Monday, null)], reader);

            scopes.ShouldHaveSingleItem().ShouldBe(SharingScope.Everything);
            Enum.GetValues<SharedRecordKind>().Where(kind => kind != SharedRecordKind.CounsellingNotes).ShouldAllBe(kind => scopes[0].Covers(kind, null));
            scopes[0].Covers(SharedRecordKind.CounsellingNotes, null).ShouldBeFalse("agreeing to share the record is not agreeing to share what was said in counselling");
        }
    }

    [Fact]
    public void Counselling_notes_are_shared_only_under_a_consent_that_names_them()
    {
        SharedRecordKinds.NamedOnly(SharedRecordKind.CounsellingNotes).ShouldBeTrue();
        Enum.GetValues<SharedRecordKind>().Where(SharedRecordKinds.NamedOnly).ShouldBe([SharedRecordKind.CounsellingNotes], "no other kind needs naming");

        Scope(kinds: [SharedRecordKind.CounsellingNotes]).Covers(SharedRecordKind.CounsellingNotes, Today).ShouldBeTrue();
        Scope(kinds: [SharedRecordKind.ClinicalNotes]).Covers(SharedRecordKind.CounsellingNotes, Today).ShouldBeFalse("other notes are not counselling notes");
        Scope(kinds: [SharedRecordKind.CounsellingNotes]).Covers(SharedRecordKind.ClinicalNotes, Today).ShouldBeFalse();
        Scope(from: new DateOnly(2026, 1, 1)).Covers(SharedRecordKind.CounsellingNotes, Today).ShouldBeFalse("dates alone name no kind");
        Scope(shareWith: [PartnerA], purposes: [SharingPurpose.Treatment]).Covers(SharedRecordKind.ClinicalNotes, Today).ShouldBeTrue();
    }

    [Fact]
    public void A_consent_for_documents_shares_no_notes()
    {
        var documents = Scope(kinds: [SharedRecordKind.Documents]);

        documents.Covers(SharedRecordKind.ClinicalNotes, Today).ShouldBeFalse("a patient who shared their discharge summaries did not share every note");
        documents.Covers(SharedRecordKind.CounsellingNotes, Today).ShouldBeFalse();
    }

    [Fact]
    public void A_consent_naming_accounts_admits_only_them()
    {
        var scope = Scope(shareWith: [PartnerA]);

        scope.Admits(Reader).ShouldBeTrue();
        scope.Admits(Reader with { UserId = PartnerB }).ShouldBeFalse();
    }

    [Fact]
    public void A_consent_naming_purposes_admits_only_a_reader_who_states_one_of_them()
    {
        var scope = Scope(purposes: [SharingPurpose.Treatment, SharingPurpose.EmergencyTreatment]);

        scope.Admits(Reader).ShouldBeTrue();
        scope.Admits(Reader with { Purpose = SharingPurpose.EmergencyTreatment }).ShouldBeTrue();
        scope.Admits(Reader with { Purpose = SharingPurpose.Payment }).ShouldBeFalse("an insurer reading for a claim is not treating the patient");
        scope.Admits(Reader with { Purpose = null }).ShouldBeFalse("a reader who states no purpose cannot be held to one");
    }

    [Fact]
    public void A_consent_lets_anyone_read_until_the_end_of_its_last_day()
    {
        var scope = Scope(until: Today);

        scope.Admits(Reader).ShouldBeTrue();
        scope.Admits(Reader with { Today = Today.AddDays(1) }).ShouldBeFalse();
    }

    [Fact]
    public void Kinds_and_dates_limit_the_records_a_consent_covers()
    {
        var scope = Scope(kinds: [SharedRecordKind.LabResults, SharedRecordKind.Medicines], from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 6, 30));

        scope.Covers(SharedRecordKind.LabResults, new DateOnly(2026, 3, 1)).ShouldBeTrue();
        scope.Covers(SharedRecordKind.Medicines, new DateOnly(2026, 1, 1)).ShouldBeTrue("the first day is included");
        scope.Covers(SharedRecordKind.Medicines, new DateOnly(2026, 6, 30)).ShouldBeTrue("the last day is included");
        scope.Covers(SharedRecordKind.Visits, new DateOnly(2026, 3, 1)).ShouldBeFalse("visits are not named");
        scope.Covers(SharedRecordKind.LabResults, new DateOnly(2025, 12, 31)).ShouldBeFalse("before the first day");
        scope.Covers(SharedRecordKind.LabResults, new DateOnly(2026, 7, 1)).ShouldBeFalse("after the last day");
        scope.Covers(SharedRecordKind.LabResults, null).ShouldBeFalse("a record without a date cannot be shown to be within the dates");
        Scope(kinds: [SharedRecordKind.LabResults]).Covers(SharedRecordKind.LabResults, null).ShouldBeTrue("without dates, the date does not matter");
        Scope(from: new DateOnly(2026, 1, 1)).Covers(SharedRecordKind.Documents, new DateOnly(2027, 1, 1)).ShouldBeTrue("open after the first day");
    }

    [Fact]
    public void A_reader_reads_what_any_consent_that_admits_them_covers()
    {
        ConsentFact[] history =
        [
            new(true, Monday, null, Scope(shareWith: [PartnerA], kinds: [SharedRecordKind.LabResults])),
            new(true, Monday.AddDays(1), null, Scope(shareWith: [PartnerA], kinds: [SharedRecordKind.Medicines], until: Today)),
            new(true, Monday.AddDays(2), null, Scope(shareWith: [PartnerB], kinds: [SharedRecordKind.Visits])),
        ];

        var scopes = RecordSharingConsent.ScopesFor(history, Reader);

        scopes.Count.ShouldBe(2);
        scopes.Any(s => s.Covers(SharedRecordKind.LabResults, Today)).ShouldBeTrue();
        scopes.Any(s => s.Covers(SharedRecordKind.Medicines, Today)).ShouldBeTrue();
        scopes.Any(s => s.Covers(SharedRecordKind.Visits, Today)).ShouldBeFalse("only partner B may read the visits");
        RecordSharingConsent.ScopesFor(history, Reader with { Today = Today.AddDays(1) }).ShouldHaveSingleItem().Kinds.ShouldBe([SharedRecordKind.LabResults]);
    }

    [Fact]
    public void A_later_refusal_ends_every_scoped_consent_before_it()
    {
        ConsentFact[] history =
        [
            new(true, Monday, null, Scope(shareWith: [PartnerA])),
            new(false, Monday.AddDays(1), null),
            new(true, Monday.AddDays(2), null, Scope(shareWith: [PartnerB])),
        ];

        RecordSharingConsent.IsGranted(history, Reader).ShouldBeFalse();
        RecordSharingConsent.IsGranted(history, Reader with { UserId = PartnerB }).ShouldBeTrue("given again after the refusal, for partner B");
    }

    [Fact]
    public void The_query_form_admits_readers_as_the_scopes_do()
    {
        Patient[] patients = [Patient("ANY"), Patient("A-ONLY"), Patient("B-ONLY"), Patient("TREAT"), Patient("PAY"), Patient("ENDED"), Patient("TODAY"), Patient("LAB")];
        var consents = new[]
        {
            Consent(patients[0], ConsentType.DataSharing, true, Monday),
            Scoped(Consent(patients[1], ConsentType.DataSharing, true, Monday), c => c.ShareWith = [PartnerA]),
            Scoped(Consent(patients[2], ConsentType.DataSharing, true, Monday), c => c.ShareWith = [PartnerB]),
            Scoped(Consent(patients[3], ConsentType.DataSharing, true, Monday), c => c.SharePurposes = [SharingPurpose.Treatment]),
            Scoped(Consent(patients[4], ConsentType.DataSharing, true, Monday), c => c.SharePurposes = [SharingPurpose.Payment]),
            Scoped(Consent(patients[5], ConsentType.DataSharing, true, Monday), c => c.ShareUntil = Today.AddDays(-1)),
            Scoped(Consent(patients[6], ConsentType.DataSharing, true, Monday), c => c.ShareUntil = Today),
            Scoped(Consent(patients[7], ConsentType.DataSharing, true, Monday), c => c.ShareKinds = [SharedRecordKind.LabResults]),
        }.AsQueryable();

        List<string> Shared(SharingReader reader) => [.. patients.AsQueryable().Where(RecordSharingConsent.IsGranted(consents, reader)).Select(p => p.Uhid)];

        Shared(Reader).ShouldBe(["ANY", "A-ONLY", "TREAT", "TODAY", "LAB"]);
        Shared(new SharingReader(PartnerB, SharingPurpose.Payment, Today)).ShouldBe(["ANY", "B-ONLY", "PAY", "TODAY", "LAB"]);
        Shared(new SharingReader(PartnerB, null, Today)).ShouldBe(["ANY", "B-ONLY", "TODAY", "LAB"], "a consent naming purposes admits no reader who states none");
    }

    [Theory]
    [InlineData("TREAT", SharingPurpose.Treatment)]
    [InlineData(" etreat ", SharingPurpose.EmergencyTreatment)]
    [InlineData("CAREMGT", SharingPurpose.CareManagement)]
    [InlineData("HPAYMT", SharingPurpose.Payment)]
    [InlineData("PUBHLTH", SharingPurpose.PublicHealth)]
    [InlineData("HRESCH", SharingPurpose.Research)]
    [InlineData("PATRQT", SharingPurpose.PatientRequest)]
    public void Reads_the_v3_purposes_of_use(string code, SharingPurpose expected)
    {
        SharingPurposeCodes.TryParse(code, out var purpose).ShouldBeTrue();
        purpose.ShouldBe(expected);
        SharingPurposeCodes.CodeOf(expected).ShouldBe(code.Trim().ToUpperInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("TREATMENT")]
    [InlineData("TREAT,HPAYMT")]
    [InlineData("0")]
    public void Refuses_anything_else_as_a_purpose(string code) => SharingPurposeCodes.TryParse(code, out _).ShouldBeFalse();

    internal static SharingScope Scope(
        Guid[]? shareWith = null, SharingPurpose[]? purposes = null, SharedRecordKind[]? kinds = null, DateOnly? from = null, DateOnly? to = null, DateOnly? until = null) =>
        new(shareWith ?? [], purposes ?? [], kinds ?? [], from, to, until);

    private static PatientConsent Scoped(PatientConsent consent, Action<PatientConsent> scope)
    {
        scope(consent);
        return consent;
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
        return [.. PatientSearchQuery.Apply(patients.AsQueryable(), consents.AsQueryable(), criteria, includeRestricted, RecordSharingConsentTests.Reader, Contains)
            .Select(p => p.Uhid)];
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

        PatientSearchQuery.Ordered(new[] { older, newer }.AsQueryable(), []).Select(p => p.Uhid).ShouldBe(["NEW", "OLD"]);
    }

    [Fact]
    public void Sorts_patients_by_the_birth_date_they_are_served_with_an_estimated_one_as_its_whole_year()
    {
        // Served as "1980-06-15", "1980-10-01", "1980" (from a stated age, stored as 1980-09-01) and with no birth date.
        // The year starts before both days and ends after both: first either way. By the stored day it would sit between them.
        var june = RecordSharingConsentTests.Patient("JUNE");
        june.DateOfBirth = new DateOnly(1980, 6, 15);
        var october = RecordSharingConsentTests.Patient("OCTOBER");
        october.DateOfBirth = new DateOnly(1980, 10, 1);
        var estimated = RecordSharingConsentTests.Patient("ESTIMATED");
        (estimated.DateOfBirth, estimated.IsDateOfBirthEstimated) = (new DateOnly(1980, 9, 1), true);
        var unknown = RecordSharingConsentTests.Patient("UNKNOWN");
        var patients = new[] { june, unknown, estimated, october }.AsQueryable();

        Sorted(patients, "_sort=birthdate").ShouldBe(["ESTIMATED", "JUNE", "OCTOBER", "UNKNOWN"]);
        Sorted(patients, "_sort=-birthdate").ShouldBe(["ESTIMATED", "OCTOBER", "JUNE", "UNKNOWN"]);
    }

    [Fact]
    public void Sorts_patients_by_gender_code_last_update_and_id()
    {
        var male = RecordSharingConsentTests.Patient("MALE");
        male.Gender = Gender.Male;
        var unknown = RecordSharingConsentTests.Patient("UNKNOWN");
        unknown.Gender = Gender.Unknown;
        var female = RecordSharingConsentTests.Patient("FEMALE");
        female.Gender = Gender.Female;
        (female.CreatedAt, female.UpdatedAt) = (Monday, Monday.AddDays(2));
        (male.CreatedAt, unknown.CreatedAt) = (Monday.AddDays(1), Monday);
        var patients = new[] { male, unknown, female }.AsQueryable();

        Sorted(patients, "_sort=gender").ShouldBe(["FEMALE", "MALE", "UNKNOWN"]);
        Sorted(patients, "_sort=-_lastUpdated").ShouldBe(["FEMALE", "MALE", "UNKNOWN"]);
        Sorted(patients, "_sort=_id").ShouldBe([.. patients.OrderBy(p => p.Id).Select(p => p.Uhid)]);
    }

    [Fact]
    public void Patients_changed_within_the_same_millisecond_tie_on_last_update_and_fall_back_to_newest_registration()
    {
        // Both are served with the same meta.lastUpdated, to the millisecond. The microseconds stored apart must not order
        // them: they tie, and the patient registered later comes first.
        var changed = Monday.AddDays(3);
        var earlier = RecordSharingConsentTests.Patient("EARLIER");
        (earlier.RegisteredAt, earlier.UpdatedAt) = (Monday, changed.AddTicks(9_000));
        var later = RecordSharingConsentTests.Patient("LATER");
        (later.RegisteredAt, later.UpdatedAt) = (Monday.AddHours(1), changed.AddTicks(1_000));
        var patients = new[] { earlier, later }.AsQueryable();

        Sorted(patients, "_sort=-_lastUpdated").ShouldBe(["LATER", "EARLIER"]);
        Sorted(patients, "_sort=_lastUpdated").ShouldBe(["LATER", "EARLIER"]);
    }

    private static List<string> Sorted(IQueryable<Patients.Domain.Patient> patients, string sort) =>
        [.. PatientSearchQuery.Ordered(patients, TestData.Search("Patient", $"name=patient&{sort}").Sort).Select(p => p.Uhid)];
}
