using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

/// <summary>
/// The campaign audience is the DPDP consent gate for marketing: these rules run in memory here and translate to SQL in
/// production, so every inclusion and exclusion is pinned down.
/// </summary>
public sealed class CampaignAudienceQueryTests
{
    private static readonly Guid Facility = Guid.NewGuid();
    private static readonly Guid OtherFacility = Guid.NewGuid();
    private static readonly Guid Cardiology = Guid.NewGuid();
    private static readonly Guid Orthopaedics = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private readonly List<Patient> _patients = [];
    private readonly List<PatientConsent> _consents = [];
    private readonly List<Visit> _visits = [];

    private Patient AddPatient(
        string name = "Lakshmi",
        DateOnly? dateOfBirth = null,
        Gender gender = Gender.Female,
        string? city = "Chennai",
        string? phone = "+919876543210",
        Guid? registeredAt = null,
        bool consent = true)
    {
        var patient = new Patient
        {
            Uhid = $"NH{_patients.Count + 1:D6}",
            FirstName = name,
            FullName = name,
            Gender = gender,
            DateOfBirth = dateOfBirth ?? new DateOnly(1980, 1, 1),
            Phone = phone,
            Address = new Address { City = city },
            RegisteredFacilityId = registeredAt ?? Facility,
            IsActive = true,
        };
        _patients.Add(patient);
        if (consent)
        {
            Consent(patient, granted: true, Now.AddDays(-30));
        }

        return patient;
    }

    private PatientConsent Consent(Patient patient, bool granted, DateTimeOffset at, ConsentType type = ConsentType.MarketingCommunication)
    {
        var consent = new PatientConsent { PatientId = patient.Id, Type = type, Granted = granted, CapturedAt = at };
        _consents.Add(consent);
        return consent;
    }

    private void AddVisit(Patient patient, DateTimeOffset at, Guid? facility = null, Guid? department = null, VisitStatus status = VisitStatus.Completed) =>
        _visits.Add(new Visit { PatientId = patient.Id, FacilityId = facility ?? Facility, VisitedAt = at, DepartmentId = department, Status = status });

    private List<Patient> Audience(CampaignSegment? segment = null) =>
        CampaignAudienceQuery.Build(_patients.AsQueryable(), _consents.AsQueryable(), _visits.AsQueryable(), segment ?? CampaignSegment.Everyone, Facility, Today)
            .ToList();

    private static CampaignSegment Segment(
        int? minAge = null,
        int? maxAge = null,
        Gender? gender = null,
        string? city = null,
        DateOnly? lastVisitFrom = null,
        DateOnly? lastVisitTo = null,
        IReadOnlyList<Guid>? departments = null) =>
        new(minAge, maxAge, gender, city, lastVisitFrom, lastVisitTo, departments ?? []);

    [Fact]
    public void A_patient_with_granted_marketing_consent_is_included()
    {
        var patient = AddPatient();

        Audience().ShouldBe([patient]);
    }

    [Fact]
    public void Without_marketing_consent_nobody_is_messaged()
    {
        AddPatient(consent: false);

        Audience().ShouldBeEmpty();
    }

    [Fact]
    public void Consent_to_service_updates_is_not_consent_to_marketing()
    {
        var patient = AddPatient(consent: false);
        Consent(patient, granted: true, Now.AddDays(-5), ConsentType.SmsWhatsAppUpdates);

        Audience().ShouldBeEmpty();
    }

    [Fact]
    public void A_revoked_consent_excludes_the_patient()
    {
        var patient = AddPatient(consent: false);
        Consent(patient, granted: true, Now.AddDays(-30)).RevokedAt = Now.AddDays(-1);

        Audience().ShouldBeEmpty();
    }

    [Fact]
    public void A_later_refusal_supersedes_an_earlier_grant()
    {
        var patient = AddPatient();
        Consent(patient, granted: false, Now.AddDays(-2));

        Audience().ShouldBeEmpty();
    }

    [Fact]
    public void A_new_grant_after_an_earlier_refusal_counts()
    {
        var patient = AddPatient(consent: false);
        Consent(patient, granted: false, Now.AddDays(-60));
        Consent(patient, granted: true, Now.AddDays(-1));

        Audience().ShouldBe([patient]);
    }

    [Fact]
    public void Deceased_inactive_merged_and_unreachable_patients_are_never_included()
    {
        AddPatient(name: "Deceased").IsDeceased = true;
        AddPatient(name: "Inactive").IsActive = false;
        AddPatient(name: "Merged").MergedIntoPatientId = Guid.NewGuid();
        AddPatient(name: "No phone", phone: null);
        var reachable = AddPatient(name: "Reachable");

        Audience().ShouldBe([reachable]);
    }

    [Fact]
    public void Only_patients_of_this_facility_are_included()
    {
        var registeredHere = AddPatient(name: "Registered here");
        AddPatient(name: "Elsewhere only", registeredAt: OtherFacility);
        var visitedHere = AddPatient(name: "Visited here", registeredAt: OtherFacility);
        AddVisit(visitedHere, Now.AddDays(-10));

        Audience().ShouldBe([registeredHere, visitedHere], ignoreOrder: true);
    }

    [Fact]
    public void Age_limits_are_inclusive_and_exclude_unknown_ages()
    {
        var turnsEighteenToday = AddPatient(name: "Eighteen", dateOfBirth: Today.AddYears(-18));
        AddPatient(name: "Seventeen", dateOfBirth: Today.AddYears(-18).AddDays(1));
        var sixty = AddPatient(name: "Sixty", dateOfBirth: Today.AddYears(-61).AddDays(1));
        AddPatient(name: "Sixty-one", dateOfBirth: Today.AddYears(-61));
        AddPatient(name: "Unknown age").DateOfBirth = null;

        Audience(Segment(minAge: 18, maxAge: 60)).ShouldBe([turnsEighteenToday, sixty], ignoreOrder: true);
    }

    [Fact]
    public void Gender_and_city_filters_apply_with_city_matched_case_insensitively()
    {
        var match = AddPatient(name: "Match", gender: Gender.Female, city: "Chennai");
        AddPatient(name: "Male", gender: Gender.Male, city: "Chennai");
        AddPatient(name: "Other city", gender: Gender.Female, city: "Madurai");
        AddPatient(name: "No city", gender: Gender.Female, city: null);

        Audience(Segment(gender: Gender.Female, city: "  CHENNAI ")).ShouldBe([match]);
    }

    [Fact]
    public void Last_visit_range_uses_the_most_recent_visit_at_this_facility()
    {
        var lastVisitInRange = AddPatient(name: "In range");
        AddVisit(lastVisitInRange, new DateTimeOffset(2026, 3, 10, 5, 0, 0, TimeSpan.Zero));

        var cameBackLater = AddPatient(name: "Came back later");
        AddVisit(cameBackLater, new DateTimeOffset(2026, 3, 10, 5, 0, 0, TimeSpan.Zero));
        AddVisit(cameBackLater, new DateTimeOffset(2026, 8, 1, 5, 0, 0, TimeSpan.Zero));

        var laterVisitCancelled = AddPatient(name: "Later visit cancelled");
        AddVisit(laterVisitCancelled, new DateTimeOffset(2026, 3, 10, 5, 0, 0, TimeSpan.Zero));
        AddVisit(laterVisitCancelled, new DateTimeOffset(2026, 8, 1, 5, 0, 0, TimeSpan.Zero), status: VisitStatus.Cancelled);

        var laterVisitElsewhere = AddPatient(name: "Later visit elsewhere");
        AddVisit(laterVisitElsewhere, new DateTimeOffset(2026, 3, 10, 5, 0, 0, TimeSpan.Zero));
        AddVisit(laterVisitElsewhere, new DateTimeOffset(2026, 8, 1, 5, 0, 0, TimeSpan.Zero), facility: OtherFacility);

        AddPatient(name: "Never visited");

        Audience(Segment(lastVisitFrom: new DateOnly(2026, 3, 1), lastVisitTo: new DateOnly(2026, 3, 31)))
            .ShouldBe([lastVisitInRange, laterVisitCancelled, laterVisitElsewhere], ignoreOrder: true);
    }

    [Fact]
    public void Visit_dates_are_hospital_local_days()
    {
        // 31 March 23:30 IST is 18:00 UTC on 31 March; 1 April 00:30 IST is still 31 March in UTC.
        var lateOn31st = AddPatient(name: "31 March late");
        AddVisit(lateOn31st, new DateTimeOffset(2026, 3, 31, 18, 0, 0, TimeSpan.Zero));
        var earlyOn1st = AddPatient(name: "1 April early");
        AddVisit(earlyOn1st, new DateTimeOffset(2026, 3, 31, 19, 0, 0, TimeSpan.Zero));

        Audience(Segment(lastVisitFrom: new DateOnly(2026, 3, 1), lastVisitTo: new DateOnly(2026, 3, 31))).ShouldBe([lateOn31st]);
    }

    [Fact]
    public void Department_filter_needs_a_visit_to_one_of_the_departments_here()
    {
        var cardiac = AddPatient(name: "Cardiac");
        AddVisit(cardiac, Now.AddDays(-40), department: Cardiology);
        var orthoOnly = AddPatient(name: "Ortho");
        AddVisit(orthoOnly, Now.AddDays(-40), department: Orthopaedics);
        var cardiacElsewhere = AddPatient(name: "Cardiac elsewhere");
        AddVisit(cardiacElsewhere, Now.AddDays(-40), facility: OtherFacility, department: Cardiology);
        var cardiacCancelled = AddPatient(name: "Cardiac cancelled");
        AddVisit(cardiacCancelled, Now.AddDays(-40), department: Cardiology, status: VisitStatus.Cancelled);

        Audience(Segment(departments: [Cardiology])).ShouldBe([cardiac]);
    }

    [Fact]
    public void Consent_is_required_even_when_every_segment_filter_matches()
    {
        var withoutConsent = AddPatient(name: "Perfect match", consent: false, gender: Gender.Female, city: "Chennai");
        AddVisit(withoutConsent, Now.AddDays(-5), department: Cardiology);

        Audience(Segment(minAge: 18, gender: Gender.Female, city: "Chennai", departments: [Cardiology])).ShouldBeEmpty();
    }
}
