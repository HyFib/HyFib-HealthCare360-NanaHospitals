using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Mvpi;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

/// <summary>
/// What a device adverse event report to MvPI needs before it is sent, how its facts fit together, how it names the patient,
/// and that a sent report does not change but takes a follow-up and a corrected reference.
/// </summary>
public sealed class MvpiReportTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);
    private static readonly MvpiPatient Lakshmi = new(Guid.CreateVersion7(), "Lakshmi Narayanan", new DateOnly(1958, 11, 20), "Female");

    private static readonly MvpiDevice Knee = MvpiDevice.Blank with
    {
        Name = "Attune",
        GenericName = "Knee implant system: primary total knee, size 5",
        RiskClass = DeviceRiskClass.C,
        Manufacturer = "DePuy Synthes",
        LotNumber = "KN2407",
        SerialNumber = "SN-A1",
        Implantable = true,
        ImplantedOn = Today.AddDays(-40),
        Use = DeviceUse.SingleUse,
        Availability = DeviceAvailability.Available,
        KeptAt = "Biomedical quarantine shelf 2",
    };

    private static MvpiDetails Details(MvpiDevice? device = null) => new(
        Lakshmi, false, 64m, Today.AddDays(-2), Today.AddDays(-1), "Ward 3, bed 12",
        "Tibial component loosened 40 days after the knee replacement; revision surgery planned.", MvpiSeriousness.Hospitalisation, null,
        MvpiPatientOutcome.NotRecovered, device ?? Knee, "Quality manager", "Quality");

    private static MvpiReport Recorded(MvpiDetails? details = null)
    {
        var report = new MvpiReport { ReportedOn = Today, ReporterName = "Meena Iyer" };
        report.Record(details ?? Details(), Today).IsSuccess.ShouldBeTrue();
        return report;
    }

    [Fact]
    public void The_form_names_the_patient_by_initials_with_the_age_on_the_day_of_the_event()
    {
        var report = Recorded();

        (report.PatientInitials, report.PatientSex, report.WeightKg).ShouldBe(("L. N.", "Female", 64m));

        // Born 20 November 1958: 67 on the day of the event in November 2026, not yet 68.
        report.PatientAgeYears.ShouldBe(67);
        MvpiReport.AgeOn(new DateOnly(1958, 11, 20), new DateOnly(2026, 11, 20)).ShouldBe(68);
        MvpiReport.AgeOn(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 4)).ShouldBe(0);
    }

    [Fact]
    public void With_no_patient_involved_the_report_names_nobody()
    {
        var report = Recorded(Details() with { Patient = null, NoPatient = true, Seriousness = MvpiSeriousness.None, Outcome = MvpiPatientOutcome.Unknown });

        (report.PatientId, report.PatientInitials, report.PatientAgeYears, report.WeightKg, report.NoPatientInvolved).ShouldBe((null, null, null, null, true));
        report.Missing().ShouldNotContainKey("patientId");

        var both = new MvpiReport();
        both.Record(Details() with { NoPatient = true }, Today).Error!.Fields!.Keys.ShouldBe(["patientId"]);
    }

    [Fact]
    public void A_draft_keeps_its_dates_in_order_and_death_with_a_fatal_outcome()
    {
        var report = new MvpiReport();

        report.Record(Details() with { EventOn = Today.AddDays(1), AwareOn = Today.AddDays(1) }, Today).Error!.Fields!.Keys.ShouldContain("eventOn");
        report.Record(Details() with { AwareOn = Today.AddDays(-3) }, Today).Error!.Fields!.Keys.ShouldBe(["awareOn"]);
        report.Record(Details() with { Outcome = MvpiPatientOutcome.Fatal }, Today).Error!.Fields!.Keys.ShouldBe(["outcome"]);
        report.Record(Details() with { DeathOn = Today }, Today).Error!.Fields!.Keys.ShouldBe(["deathOn"]);
        report.Record(Details(Knee with { ImplantedOn = Today }), Today).Error!.Fields!.Keys.ShouldBe(["device.implantedOn"]);
        report.Record(Details(Knee with { ExplantedOn = Today.AddDays(-50) }), Today).Error!.Fields!.Keys.ShouldBe(["device.explantedOn"]);
        report.Record(Details(Knee with { ManufacturedOn = Today.AddYears(-1), ExpiresOn = Today.AddYears(-2) }), Today)
            .Error!.Fields!.Keys.ShouldBe(["device.manufacturedOn"]);

        report.Record(Details() with { Seriousness = MvpiSeriousness.Death, Outcome = MvpiPatientOutcome.Fatal, DeathOn = Today.AddDays(-1) }, Today)
            .IsSuccess.ShouldBeTrue();
        report.DeathOn.ShouldBe(Today.AddDays(-1));
    }

    [Fact]
    public void A_draft_may_be_saved_half_written_but_is_sent_only_with_what_mvpi_asks_for()
    {
        var draft = Recorded(Details(MvpiDevice.Blank with { Implantable = true, Availability = DeviceAvailability.ReturnedToManufacturer }) with
        {
            Patient = null,
            Location = " ",
            Description = null,
            Seriousness = MvpiSeriousness.Death,
            Outcome = MvpiPatientOutcome.Fatal,
            ReporterDesignation = null,
        });

        draft.Missing().Keys.Order(StringComparer.Ordinal).ShouldBe(
        [
            "deathOn", "description", "device.implantedOn", "device.manufacturer", "device.name", "device.returnedOn", "device.serialNumber",
            "location", "patientId", "reporterDesignation",
        ]);

        var refused = draft.Submit(Today, null, "Meena Iyer", "MvPI-2026-0042", Today);
        refused.Error!.Code.ShouldBe("mvpi.incomplete");
        refused.Error!.Fields!.Keys.ShouldContain("device.name");
        draft.Status.ShouldBe(MvpiReportStatus.Draft);
    }

    [Fact]
    public void A_device_kept_for_evaluation_says_where_and_any_one_number_identifies_it()
    {
        Recorded(Details(Knee with { KeptAt = null })).Missing().Keys.ShouldBe(["device.keptAt"]);
        Recorded(Details(Knee with { LotNumber = null, SerialNumber = null })).Missing().Keys.ShouldBe(["device.serialNumber"]);
        Recorded(Details(Knee with { LotNumber = null, SerialNumber = null, Udi = "(01)08717648200274" })).Missing().ShouldBeEmpty();

        // What does not apply to the device is not kept: no implant dates for a device that is not implanted.
        var monitor = Recorded(Details(Knee with { Implantable = false, Availability = DeviceAvailability.NotAvailable })).Device;
        (monitor.ImplantedOn, monitor.KeptAt).ShouldBe((null, null));
    }

    [Fact]
    public void Once_sent_the_report_does_not_change_but_its_reference_can_be_corrected_with_a_reason()
    {
        var report = Recorded();
        report.Submit(Today.AddDays(1), null, "Meena Iyer", null, Today).Error!.Fields!.Keys.ShouldBe(["submittedOn"]);
        report.Submit(Today.AddDays(-2), null, "Meena Iyer", null, Today).Error!.Fields!.Keys.ShouldBe(["submittedOn"]);
        report.CorrectReference("MDAE-2026-1187", "Typed wrong", "Meena Iyer", DateTimeOffset.UtcNow).Error!.Code.ShouldBe("mvpi.not_submitted");

        report.Submit(Today, null, " Meena Iyer ", " MDAE-2026-1178 ", Today).IsSuccess.ShouldBeTrue();
        (report.Status, report.SubmittedOn, report.SubmittedByName, report.Reference).ShouldBe((MvpiReportStatus.Submitted, Today, "Meena Iyer", "MDAE-2026-1178"));
        report.Record(Details(), Today).Error!.Code.ShouldBe("mvpi.submitted");
        report.Submit(Today, null, "Meena Iyer", null, Today).Error!.Code.ShouldBe("mvpi.submitted");

        report.CorrectReference("MDAE-2026-1187", " ", "Meena Iyer", DateTimeOffset.UtcNow).Error!.Fields!.Keys.ShouldBe(["reason"]);
        report.CorrectReference("MDAE-2026-1178", "Typed wrong", "Meena Iyer", DateTimeOffset.UtcNow).Error!.Fields!.Keys.ShouldBe(["reference"]);
        report.CorrectReference(" MDAE-2026-1187 ", "Digits swapped when typed", "Meena Iyer", DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        (report.Reference, report.ReferenceCorrectionReason).ShouldBe(("MDAE-2026-1187", "Digits swapped when typed"));
    }

    [Fact]
    public void A_follow_up_is_a_new_draft_naming_the_report_sent()
    {
        var draft = Recorded();
        draft.FollowUp(Today).Error!.Code.ShouldBe("mvpi.not_submitted");

        draft.Submit(Today, null, "Meena Iyer", "MDAE-2026-1178", Today).IsSuccess.ShouldBeTrue();
        var followUp = draft.FollowUp(Today.AddDays(5)).Value;

        (followUp.FollowUpOfId, followUp.Status, followUp.ReportedOn, followUp.Reference).ShouldBe((draft.Id, MvpiReportStatus.Draft, Today.AddDays(5), null));
        (followUp.PatientInitials, followUp.Device, followUp.Description).ShouldBe((draft.PatientInitials, draft.Device, draft.Description));
        followUp.Record(Details() with { Outcome = MvpiPatientOutcome.Recovering }, Today.AddDays(5)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_equipment_failure_or_an_incident_marked_as_involving_a_device_may_be_reported_to_mvpi()
    {
        new Incident { Type = IncidentType.EquipmentFailure }.IsDeviceRelated.ShouldBeTrue();
        new Incident { Type = IncidentType.PatientFall }.IsDeviceRelated.ShouldBeFalse();
        new Incident { Type = IncidentType.PatientFall, InvolvesDevice = true }.IsDeviceRelated.ShouldBeTrue();
    }

    [Fact]
    public void The_request_is_checked_before_it_reaches_the_rules()
    {
        var validator = new SaveMvpiReportRequestValidator();
        var request = new SaveMvpiReportRequest(
            null, Lakshmi.Id, false, 64m, Today, Today, "Ward 3", "Loosened tibial component", MvpiSeriousness.Hospitalisation, null,
            MvpiPatientOutcome.NotRecovered, Knee, "Quality manager", null);

        validator.Validate(request).IsValid.ShouldBeTrue();
        validator.Validate(request with { NoPatient = true }).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("NoPatient");
        validator.Validate(request with { Seriousness = (MvpiSeriousness)128 }).IsValid.ShouldBeFalse();
        validator.Validate(request with { Device = Knee with { Name = new string('x', 201) } }).IsValid.ShouldBeFalse();
        new CorrectMvpiReferenceRequestValidator().Validate(new CorrectMvpiReferenceRequest("MDAE-1", "", 1)).IsValid.ShouldBeFalse();
    }
}
