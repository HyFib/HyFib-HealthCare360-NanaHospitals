using System.Net;
using HyFib.HealthCare360.Modules.Analytics.Features.Journeys;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.SharedKernel;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The recovery room (PACU) over the real API and PostgreSQL. After the WHO Sign-out the patient is in recovery: the
/// signed-in nurse receives them, the anaesthetist hands over (from their own sign-in, or at the nurse's screen with their
/// own credentials), the chart is kept, and the patient goes back to the ward at a modified Aldrete score of 9 or more, or
/// on the anaesthetist's override with a reason (audited at Warning); to ICU or HDU at any score. The surgery and the
/// patient's journey show "In recovery" until then.
/// </summary>
public sealed class RecoveryRoomTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_signed_out_is_in_recovery_until_the_nurse_sends_them_to_the_ward_on_the_score()
    {
        var cases = new TheatreCases(server);
        var nurse = server.FromOwnAddress(await server.SignInAsync(Users.Nurse));
        var doctor = await server.SignInAsync(Users.Doctor);
        var c = await cases.UnderWayAsync();

        // Before the Sign-out the patient is the theatre's, not the recovery room's.
        (await Refused(nurse, $"{c.Path}/recovery/receive", Receive())).ShouldBe((HttpStatusCode.UnprocessableEntity, "recovery.not_signed_out"));
        (await doctor.GetAsync<SurgeryDetail>(c.Path)).InRecovery.ShouldBeFalse();

        await SignedOutAsync(cases, c);
        var signedOut = await doctor.GetAsync<SurgeryDetail>(c.Path);
        signedOut.InRecovery.ShouldBeTrue();
        (await doctor.GetAsync<PagedResult<SurgeryListItem>>($"{TheatreCases.Bookings}?search={signedOut.BookingNumber}")).Items.Single().InRecovery.ShouldBeTrue();
        (await SurgeryJourneyAsync(doctor, c)).Current.ShouldNotBeNull().ShouldSatisfyAllConditions(
            s => s.Key.ShouldBe("in-recovery"), s => s.Detail.ShouldBe("Not yet received in recovery"));

        // The nurse receives the patient: she is the receiving nurse.
        var me = await nurse.GetAsync<AuthUser>("/api/v1/auth/me");
        var received = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/receive", Receive());
        (received.ReceivedByName, received.Oxygen, received.OxygenLitresPerMinute, received.PreAnaesthesiaSystolic, received.HandoverRequired)
            .ShouldBe((me.DisplayName, OxygenDelivery.FaceMask, 6m, 126, true));
        (await Refused(nurse, $"{c.Path}/recovery/receive", Receive())).Code.ShouldBe("recovery.already_received");
        (await SurgeryJourneyAsync(doctor, c)).Current!.Detail.ShouldBe($"Received by {me.DisplayName}");

        // The anaesthetist hands over: the nurse cannot record it as hers, nor with a confirmation given for something else, and
        // a second nurse is no anaesthetist to confirm it.
        (await Refused(nurse, $"{c.Path}/recovery/handover", new RecoveryHandoverRequest())).ShouldBe((HttpStatusCode.Forbidden, "recovery.handover_anaesthetist"));
        var forACount = (await server.WitnessAsync(nurse, Users.SecondNurse, OtClinicalService.CountWitness.Key, c.PatientId)).ConfirmationId;
        (await Refused(nurse, $"{c.Path}/recovery/handover", new RecoveryHandoverRequest(forACount))).Code.ShouldBe("witness.confirmation_unusable");
        using (var notAnAnaesthetist = await server.FromOwnAddress(nurse).SendRawAsync(HttpMethod.Post, "/api/v1/auth/witness",
                   new ConfirmWitnessRequest(Users.SecondNurse, server.Password, null, RecoveryService.HandoverWitness.Key, c.PatientId)))
        {
            notAnAnaesthetist.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await notAnAnaesthetist.Content.ReadAsStringAsync(Ct));
        }

        var anaesthetist = await server.WitnessAsync(nurse, Users.SecondDoctor, RecoveryService.HandoverWitness.Key, c.PatientId);
        var handedOver = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/handover", new RecoveryHandoverRequest(anaesthetist.ConfirmationId, "Spinal, uneventful"));
        (handedOver.HandedOverByName, handedOver.HandoverConfirmedAtScreen).ShouldBe((anaesthetist.Name, true));

        // Still drowsy: the score names what is missing, and the ward waits. The surgeon has not completed the case either.
        var drowsy = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/observations", Observation() with { Consciousness = RecoveryConsciousness.Voice, LimbsMoving = null });
        drowsy.Score.ShouldSatisfyAllConditions(
            s => s.Total.ShouldBeNull(), s => s.Missing.ShouldBe(["how many limbs the patient moves on command"]), s => s.WardBlockedBy.ShouldNotBeNull());
        (await Refused(nurse, $"{c.Path}/recovery/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Ward))).Code.ShouldBe("recovery.surgery_not_completed");
        await doctor.PostAsync($"{c.Path}/complete");
        (await doctor.GetAsync<SurgeryDetail>(c.Path)).InRecovery.ShouldBeTrue("out of the theatre, into recovery");
        (await Refused(nurse, $"{c.Path}/recovery/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Ward)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "recovery.score_too_low"));

        // Awake, moving all four limbs on room air with a steady pressure: 10, and back to the ward.
        var awake = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/observations", Observation());
        (awake.Score.Total, awake.Score.MeetsWardDischarge, awake.Score.WardBlockedBy, awake.Observations.Count).ShouldBe((10, true, null, 2));
        var discharged = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Ward));
        (discharged.Destination, discharged.DischargeScore, discharged.DischargedByName, discharged.OverrideReason).ShouldBe((RecoveryDestination.Ward, 10, me.DisplayName, null));

        var back = await doctor.GetAsync<SurgeryDetail>(c.Path);
        (back.InRecovery, back.RecoveryDestination).ShouldBe((false, RecoveryDestination.Ward));
        back.RecoveryDischargedAt.ShouldNotBeNull().ShouldBe(discharged.DischargedAt!.Value, TimeSpan.FromMilliseconds(1));
        (await cases.AuditAsync(c.PatientId, "ot.recovery_discharged")).Summary.ShouldContain("to the ward");
        (await Refused(nurse, $"{c.Path}/recovery/observations", Observation())).Code.ShouldBe("recovery.discharged");
        var journey = await SurgeryJourneyAsync(doctor, c);
        journey.Stages.Single(s => s.Key == "recovery-discharged").Detail.ShouldBe("Back to the ward");
        (await doctor.GetAsync<RecoveryRecordDto>($"{c.Path}/recovery")).Observations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Below_the_score_only_the_anaesthetist_sends_the_patient_to_the_ward_with_a_reason_audited_at_warning()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var anaesthetist = await server.SignInAsync(Users.SecondDoctor);
        var c = await InRecoveryAsync(cases);

        // The anaesthetist hands over from their own sign-in.
        var handedOver = await anaesthetist.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/handover", new RecoveryHandoverRequest());
        (handedOver.HandedOverByName, handedOver.HandoverConfirmedAtScreen)
            .ShouldBe(((await anaesthetist.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName, false));

        // The legs are still numb from the spinal: 8 of 10.
        var numb = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/observations", Observation() with { LimbsMoving = 0 });
        numb.Score.Total.ShouldBe(8);
        (await Refused(nurse, $"{c.Path}/recovery/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Ward))).Code.ShouldBe("recovery.score_too_low");

        // The nurse holds no override; the anaesthetist gives one, with the reason.
        using (var notTheirs = await nurse.SendRawAsync(HttpMethod.Post, $"{c.Path}/recovery/discharge/override", new OverrideRecoveryDischargeRequest("Ward is fine")))
        {
            notTheirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await TheatreCases.InvalidFieldsAsync(anaesthetist, HttpMethod.Post, $"{c.Path}/recovery/discharge/override", new OverrideRecoveryDischargeRequest(" ")))
            .ShouldContain("reason");
        var reason = "Spinal block regressing; observed stable for an hour, the ward will chart the legs every 30 minutes";
        var overridden = await anaesthetist.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/discharge/override", new OverrideRecoveryDischargeRequest(reason));
        (overridden.Destination, overridden.DischargeScore, overridden.OverrideReason, overridden.OverrideByName)
            .ShouldBe((RecoveryDestination.Ward, 8, reason, handedOver.HandedOverByName));

        var (summary, severity) = await cases.AuditAsync(c.PatientId, "ot.recovery_override");
        severity.ShouldBe(AuditSeverity.Warning);
        summary.ShouldContain("The modified Aldrete score is 8 of 10.");
        summary.ShouldContain(reason);
    }

    [Fact]
    public async Task Intensive_care_takes_the_patient_at_any_score_and_an_override_is_not_asked_for_where_none_is_needed()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var anaesthetist = await server.SignInAsync(Users.SecondDoctor);
        var c = await InRecoveryAsync(cases);

        // Without the handover the patient does not leave, wherever to.
        (await Refused(nurse, $"{c.Path}/recovery/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Icu))).Code.ShouldBe("recovery.handover_missing");
        await anaesthetist.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/handover", new RecoveryHandoverRequest(null, "Desaturating; for ICU"));

        // Fit for the ward on the score, an override is not what sends the patient there.
        await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/observations", Observation());
        (await Refused(anaesthetist, $"{c.Path}/recovery/discharge/override", new OverrideRecoveryDischargeRequest("For the ward")))
            .Code.ShouldBe("recovery.override_not_needed");

        // Then the patient desaturates and drowses: 5 of 10, and the ICU takes them.
        var unwell = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/observations",
            Observation() with { Consciousness = RecoveryConsciousness.Pain, SpO2 = 88, OnOxygen = true, Systolic = 80 });
        unwell.Score.Total.ShouldBe(5);
        var toIcu = await nurse.PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Icu));
        (toIcu.Destination, toIcu.DischargeScore, toIcu.OverrideReason).ShouldBe((RecoveryDestination.Icu, 5, null));
        (await SurgeryJourneyAsync(nurse, c)).ShouldSatisfyAllConditions(
            j => j.Status.ShouldBe(Modules.Analytics.Domain.Journeys.JourneyStatus.Completed),
            j => j.Stages.Single(s => s.Key == "recovery-discharged").Detail.ShouldBe("To the ICU"));
    }

    /// <summary>A case booked, operated, counted, signed out and completed, the patient received in recovery by the nurse.</summary>
    private async Task<TheatreCases.Case> InRecoveryAsync(TheatreCases cases)
    {
        var c = await cases.UnderWayAsync();
        await SignedOutAsync(cases, c);
        await (await server.SignInAsync(Users.Doctor)).PostAsync($"{c.Path}/complete");
        await (await server.SignInAsync(Users.Nurse)).PostAsync<RecoveryRecordDto>($"{c.Path}/recovery/receive", Receive());
        return c;
    }

    private async Task SignedOutAsync(TheatreCases cases, TheatreCases.Case c)
    {
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Initial, 10);
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Final, 10);
        var nurse = await server.SignInAsync(Users.Nurse);
        await nurse.PostAsync($"{c.Path}/safety-checklist/sign-out", TheatreCases.AllDone(await nurse.GetAsync<SurgeryDetail>(c.Path), SafetyPhase.SignOut));
    }

    private static async Task<JourneyDto> SurgeryJourneyAsync(ApiClient api, TheatreCases.Case c) =>
        (await api.GetAsync<PatientJourneys>($"/api/v1/patients/{c.PatientId}/journey")).Surgeries.Single(s => s.Id == c.BookingId);

    private static Task<(HttpStatusCode Status, string? Code)> Refused(ApiClient api, string path, object body) =>
        TheatreCases.RefusalAsync(api, HttpMethod.Post, path, body);

    private static ReceiveInRecoveryRequest Receive() => new(AirwayDevice.None, OxygenDelivery.FaceMask, 6m, PreAnaesthesiaSystolic: 126);

    private static AddRecoveryObservationRequest Observation() => new(
        HeartRate: 82, Systolic: 122, Diastolic: 76, SpO2: 97, OnOxygen: false, RespiratoryRate: 14, TemperatureC: 36.4m,
        RecoveryConsciousness.Alert, RecoveryBreathing.DeepBreathAndCough, LimbsMoving: 4, PainScore: 2, Ponv.None);
}
