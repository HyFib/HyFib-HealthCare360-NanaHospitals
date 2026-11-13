using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// A critical value kept among the unmatched results reaches whoever matches them, with what they need to find it; and an
/// alert after commit is never lost to a failed look-up of the patient.
/// </summary>
public sealed class CriticalValueNotifierTests
{
    private static readonly LabInstrument Em360 = new() { FacilityId = Guid.CreateVersion7(), Code = "EM360", Name = "Clinical chemistry analyser" };

    private static UnmatchedLabResult Kept(string? flags, string? barcode = "000000999", UnmatchedResultReason reason = UnmatchedResultReason.UnknownSample) => new()
    {
        FacilityId = Em360.FacilityId,
        InstrumentCode = "EM360",
        SampleBarcode = barcode,
        ChannelCode = "K",
        Value = "7.1",
        InstrumentUnit = "mmol/L",
        InstrumentFlags = flags,
        InstrumentStatus = "F",
        ReceivedAt = Now,
        Reason = reason,
        Detail = barcode is null ? "EM360 sent the result without a sample id." : $"No sample of EM360's facility has the barcode {barcode}.",
    };

    [Fact]
    public void A_critical_value_kept_aside_asks_whoever_assigns_unmatched_results_to_match_it_now()
    {
        var alert = CriticalValueNotifier.UnmatchedAlert(Em360, Kept("H,HH")).ShouldNotBeNull();

        (alert.Permission, alert.FacilityId, alert.Severity).ShouldBe((DiagnosticsPermissions.LabResultsEnter, Em360.FacilityId, "critical"));
        alert.Title.ShouldBe("Critical analyser result to match: K from EM360");
        alert.Body.ShouldBe("Clinical chemistry analyser (EM360) sent K 7.1 mmol/L, flagged H,HH, for sample 000000999. It went on no test, so no pathologist "
                            + "has been alerted: No sample of EM360's facility has the barcode 000000999. Match it to the right sample's test now.");
        alert.Link.ShouldBe("/lab/unmatched?search=000000999");
    }

    [Fact]
    public void Without_a_sample_id_the_alert_says_so_and_finds_the_result_by_its_channel()
    {
        var alert = CriticalValueNotifier.UnmatchedAlert(Em360, Kept("LL", barcode: null)).ShouldNotBeNull();

        alert.Body.ShouldStartWith("Clinical chemistry analyser (EM360) sent K 7.1 mmol/L, flagged LL, without a sample id.");
        alert.Link.ShouldBe("/lab/unmatched?search=K");
    }

    [Fact]
    public void A_value_the_analyser_did_not_flag_critical_or_its_withdrawal_tells_nobody()
    {
        CriticalValueNotifier.UnmatchedAlert(Em360, Kept("H")).ShouldBeNull();
        CriticalValueNotifier.UnmatchedAlert(Em360, Kept(null)).ShouldBeNull();
        CriticalValueNotifier.UnmatchedAlert(Em360, Kept("HH", reason: UnmatchedResultReason.Withdrawn)).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_look_up_of_the_patient_after_the_value_is_saved_neither_fails_the_caller_nor_loses_the_alert()
    {
        // A database that refuses the connection: the look-up of the patient throws.
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Timeout=2")
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new DiagnosticsModule()]));
        var lookups = new DiagnosticsLookups(db, Substitute.For<ISettingsService>());
        var notifications = Substitute.For<INotificationService>();
        var realtime = Substitute.For<IRealtimePublisher>();
        var notifier = new CriticalValueNotifier(
            lookups,
            notifications,
            new DiagnosticsRealtime(realtime, Substitute.For<IOutbox>(), NullLogger<DiagnosticsRealtime>.Instance),
            NullLogger<CriticalValueNotifier>.Instance);
        var facility = Guid.CreateVersion7();
        var patientId = Guid.CreateVersion7();
        CriticalAlert[] alerts = [new(DiagnosticKind.Laboratory, facility, patientId, null, "Potassium", "7.1 mmol/L (CriticalHigh)", "/lab/orders/1")];
        await Should.ThrowAsync<Exception>(() => lookups.PatientAsync(patientId, TestContext.Current.CancellationToken));

        await notifier.AlertVerifiersAsync(alerts, TestContext.Current.CancellationToken);
        await notifier.NotifyVerifiedAsync(alerts, TestContext.Current.CancellationToken);

        await notifications.Received(1).NotifyPermissionHoldersAsync(
            DiagnosticsPermissions.LabResultsVerify, facility, "Critical value awaiting verification: Potassium", "Patient: Potassium 7.1 mmol/L (CriticalHigh)",
            "/lab/orders/1", "diagnostics", "critical", Arg.Any<CancellationToken>());
        await notifications.Received(1).NotifyPermissionHoldersAsync(
            DiagnosticsPermissions.CriticalAlerts, facility, "Critical value: Potassium", "Patient: Potassium 7.1 mmol/L (CriticalHigh)",
            "/lab/orders/1", "diagnostics", "critical", Arg.Any<CancellationToken>());
        await realtime.Received(1).PublishAsync(Arg.Any<string>(), "lab.critical", Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }
}
