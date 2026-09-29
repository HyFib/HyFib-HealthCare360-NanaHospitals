using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

/// <summary>
/// A WhatsApp STOP withdraws the marketing consent of every patient registered with the number (DPDP Act: withdrawing
/// is as easy as giving), and START gives back only what a STOP took. The rules run in memory here, as the handler
/// applies them to the patients and consent history it loads.
/// </summary>
public sealed class WhatsAppReplyConsentTests
{
    private const string Number = "+919876543210";
    private static readonly DateTimeOffset Stop = new(2026, 9, 29, 4, 30, 0, TimeSpan.Zero);

    private readonly List<Patient> _patients = [];
    private readonly List<PatientConsent> _consents = [];

    private Patient AddPatient(string name, string? phone = Number, bool marketing = true)
    {
        var patient = new Patient { Uhid = $"NH{_patients.Count + 1:D6}", FirstName = name, FullName = name, Phone = phone, IsActive = true };
        _patients.Add(patient);
        if (marketing)
        {
            Consent(patient, granted: true, Stop.AddDays(-30), "Given via patient portal");
        }

        return patient;
    }

    private PatientConsent Consent(Patient patient, bool granted, DateTimeOffset at, string notes, Guid? capturedBy = null)
    {
        var consent = new PatientConsent
        {
            PatientId = patient.Id,
            Type = ConsentType.MarketingCommunication,
            Granted = granted,
            CapturedAt = at,
            CapturedByUserId = capturedBy,
            Notes = notes,
        };
        _consents.Add(consent);
        return consent;
    }

    /// <summary>What <c>CommunicationConsentService.ApplyWhatsAppReplyAsync</c> does: every patient on the number, one rule each.</summary>
    private List<PatientConsent> Reply(bool optedOut, DateTimeOffset at, string phone = Number)
    {
        var added = new List<PatientConsent>();
        foreach (var patient in _patients.AsQueryable().Where(WhatsAppReplyConsent.RegisteredWith(phone)).ToList())
        {
            var history = _consents.Where(c => c.PatientId == patient.Id && c.Type == ConsentType.MarketingCommunication).ToList();
            if (WhatsAppReplyConsent.Apply(patient.Id, history, optedOut, at, capturedByUserId: null) is { } record)
            {
                added.Add(record);
            }
        }

        _consents.AddRange(added);
        return added;
    }

    private bool MarketingAllowed(Patient patient) =>
        _patients.AsQueryable().Where(p => p.Id == patient.Id).Any(CampaignAudienceQuery.HasActiveMarketingConsent(_consents.AsQueryable()));

    [Fact]
    public void Stop_withdraws_marketing_consent_for_every_patient_registered_with_the_number()
    {
        var mother = AddPatient("Lakshmi");
        var son = AddPatient("Ravi");
        var neighbour = AddPatient("Meena", phone: "+919876543211");

        var records = Reply(optedOut: true, Stop);

        records.Select(r => r.PatientId).ShouldBe([mother.Id, son.Id], ignoreOrder: true);
        records.ShouldAllBe(r => !r.Granted && r.Type == ConsentType.MarketingCommunication && r.CapturedAt == Stop && r.Notes == "Withdrawn via WhatsApp STOP reply");
        MarketingAllowed(mother).ShouldBeFalse();
        MarketingAllowed(son).ShouldBeFalse();
        MarketingAllowed(neighbour).ShouldBeTrue();
        _consents.Where(c => c.Granted && c.PatientId != neighbour.Id)
            .ShouldAllBe(c => c.RevokedAt == Stop && c.RevokedReason == "Withdrawn via WhatsApp STOP reply");
    }

    [Fact]
    public void Handling_the_same_stop_again_records_nothing_more()
    {
        AddPatient("Lakshmi");
        AddPatient("Ravi");
        Reply(optedOut: true, Stop);

        Reply(optedOut: true, Stop).ShouldBeEmpty();
        Reply(optedOut: true, Stop.AddHours(1)).ShouldBeEmpty();
        _consents.Count(c => !c.Granted).ShouldBe(2);
    }

    [Fact]
    public void Merged_duplicates_and_patients_who_never_agreed_get_no_record()
    {
        var merged = AddPatient("Lakshmi (duplicate)");
        merged.MergedIntoPatientId = Guid.NewGuid();
        AddPatient("Ravi", marketing: false);

        Reply(optedOut: true, Stop).ShouldBeEmpty();
        MarketingAllowed(merged).ShouldBeTrue();
    }

    [Fact]
    public void A_choice_made_after_the_stop_was_sent_wins_over_a_late_webhook()
    {
        var patient = AddPatient("Lakshmi", marketing: false);
        Consent(patient, granted: true, Stop.AddMinutes(10), "Given via patient portal");

        Reply(optedOut: true, Stop).ShouldBeEmpty();
        MarketingAllowed(patient).ShouldBeTrue();
    }

    [Fact]
    public void Start_gives_back_the_consent_a_stop_withdrew_once()
    {
        var patient = AddPatient("Lakshmi");
        Reply(optedOut: true, Stop);

        var records = Reply(optedOut: false, Stop.AddDays(2));

        var record = records.ShouldHaveSingleItem();
        (record.Granted, record.CapturedAt, record.Notes).ShouldBe((true, Stop.AddDays(2), "Given again via WhatsApp START reply"));
        MarketingAllowed(patient).ShouldBeTrue();
        Reply(optedOut: false, Stop.AddDays(2)).ShouldBeEmpty();
        Reply(optedOut: false, Stop.AddDays(3)).ShouldBeEmpty();
    }

    [Fact]
    public void Start_never_creates_consent_the_patient_had_not_given()
    {
        var patient = AddPatient("Ravi", marketing: false);
        Reply(optedOut: true, Stop);

        Reply(optedOut: false, Stop.AddDays(2)).ShouldBeEmpty();
        MarketingAllowed(patient).ShouldBeFalse();
    }

    [Fact]
    public void Start_does_not_override_an_opt_out_made_another_way()
    {
        var patient = AddPatient("Lakshmi");
        Reply(optedOut: true, Stop);
        Consent(patient, granted: false, Stop.AddDays(1), "Opted out: Unsubscribe link in a campaign message");

        Reply(optedOut: false, Stop.AddDays(2)).ShouldBeEmpty();
        MarketingAllowed(patient).ShouldBeFalse();
    }

    [Fact]
    public void Start_does_not_undo_a_refusal_recorded_by_staff()
    {
        var patient = AddPatient("Lakshmi", marketing: false);
        Consent(patient, granted: false, Stop, "Withdrawn via WhatsApp STOP reply", capturedBy: Guid.NewGuid());

        Reply(optedOut: false, Stop.AddDays(2)).ShouldBeEmpty();
    }

    [Fact]
    public void A_start_sent_before_the_stop_changes_nothing()
    {
        var patient = AddPatient("Lakshmi");
        Reply(optedOut: true, Stop);

        Reply(optedOut: false, Stop.AddMinutes(-5)).ShouldBeEmpty();
        MarketingAllowed(patient).ShouldBeFalse();
    }
}
