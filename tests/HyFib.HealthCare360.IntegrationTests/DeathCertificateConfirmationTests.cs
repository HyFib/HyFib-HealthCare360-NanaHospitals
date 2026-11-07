using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Maker-checker on the medical certificate of cause of death: the doctor who certifies a death asks, and a second doctor
/// confirms the certificate as it stands before the hospital reports it to the Registrar. The death itself is recorded at
/// once; only the report waits.
/// </summary>
public sealed class DeathCertificateConfirmationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    [Fact]
    public async Task A_second_doctor_confirms_the_certificate_as_certified_and_is_named_on_it_before_it_is_reported()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1952, 2, 1));
        var certificate = BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-2));

        // The death is recorded at once (the patient is deceased); the certificate waits for a second doctor.
        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", certificate);
        (death.ConfirmationRequired, death.ConfirmationWaitingSince is not null, death.ConfirmedBy).ShouldBe((true, true, null));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeTrue();

        // What the second doctor confirms, without the patient's name: that is opened with the certificate itself.
        var asked = await WaitingRequestAsync(second, death.Id);
        (asked.Kind, asked.Summary, asked.SubjectType, asked.PatientId).ShouldBe(
            (DeathCertificateApproval.Key, $"Death certificate {death.SerialNumber}", nameof(DeathRecord), patient.Id));
        asked.Details.ShouldContain(new ApprovalDetail("Death register no.", death.SerialNumber));
        asked.Details.ShouldContain(new ApprovalDetail("Part I (a)", "Septic shock (6 hours)"));
        asked.Details.ShouldContain(new ApprovalDetail("Part I (b), underlying cause", "Pneumonia (5 days)"));
        asked.Details.ShouldContain(new ApprovalDetail("Part II", "Type 2 diabetes mellitus"));
        asked.Details.ShouldContain(d => d.Label == "Certified by" && d.Value.Contains("TNMC 45120"));
        asked.Details.ShouldNotContain(d => d.Value.Contains(patient.Uhid) || d.Value.Contains("Vital"));

        // Not the doctor who asked, not a nurse; and nothing is reported, corrected or withdrawn under the waiting request.
        using (var own = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await own.Content.ReadAsStringAsync(Ct));
        }

        using (var notANurses = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/approvals/{asked.Id}"))
        {
            notANurses.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await BirthsAndDeathsTests.RefusalAsync(mrd, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/report",
            new ReportToRegistrarRequest(Today, "D-2026-TN-00501", death.Version))).ShouldBe("death.not_confirmed");
        (await ConflictAsync(doctor, HttpMethod.Put, $"/api/v1/mrd/deaths/{death.Id}", certificate with { Version = death.Version }))
            .ShouldBe("death.confirmation_waiting");
        (await ConflictAsync(doctor, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/entered-in-error",
            new VitalEntryErrorRequest("Wrong patient", death.Version))).ShouldBe("death.confirmation_waiting");
        var unconfirmed = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/mrd/deaths/{death.Id}/document");
        unconfirmed.Html.ShouldContain("NOT YET CONFIRMED");
        var day = VitalRegistration.LocalDate(death.DiedAt);
        var registerPath = $"/api/v1/mrd/deaths?year={day.Year}&month={day.Month}";
        (await mrd.GetAsync<DeathRegister>(registerPath)).Rows.Single(r => r.Id == death.Id).AwaitingConfirmation.ShouldBeTrue();

        // The second doctor confirms it: named on the certificate with the registration number of their practitioner record.
        var approved = await MakerChecker.ApproveAsync(second, asked);
        approved.Outcome.ShouldBe($"Death certificate {death.SerialNumber} confirmed");
        var confirmed = await doctor.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}");
        var registration = await RegistrationNumberAsync(HospitalServer.Users.SecondDoctor);
        (confirmed.ConfirmationWaitingSince, confirmed.ConfirmerRegistrationNumber, confirmed.ConfirmedAt is not null).ShouldBe((null, registration, true));
        confirmed.ConfirmedBy.ShouldNotBeNullOrWhiteSpace();
        var printed = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/mrd/deaths/{death.Id}/document");
        printed.Html.ShouldNotContain("NOT YET CONFIRMED");
        printed.Html.ShouldContain("Confirmed by");
        printed.Html.ShouldContain(registration!);

        // Confirmed once: asking again does not open another request.
        (await BirthsAndDeathsTests.RefusalAsync(doctor, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/confirmation",
            new AskDeathConfirmationRequest(confirmed.Version))).ShouldBe("death.confirmed");
        (await mrd.GetAsync<DeathRegister>(registerPath)).Rows.Single(r => r.Id == death.Id).AwaitingConfirmation.ShouldBeFalse();
        var reported = await mrd.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}/report",
            new ReportToRegistrarRequest(Today, "D-2026-TN-00501", confirmed.Version));
        reported.Status.ShouldBe(VitalReportStatus.Reported);
    }

    [Fact]
    public async Task A_correction_takes_the_confirmation_away_and_asks_again_for_the_corrected_certificate()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1949, 9, 1));
        var certificate = BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-3));
        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", certificate);
        var confirmed = await ConfirmAsync(server, death);
        confirmed.ConfirmedAt.ShouldNotBeNull();

        var corrected = await doctor.PutAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}",
            certificate with { OtherConditions = "Chronic obstructive pulmonary disease", Version = confirmed.Version });
        (corrected.ConfirmedAt, corrected.ConfirmedBy, corrected.ConfirmationWaitingSince is not null).ShouldBe((null, null, true));
        (await BirthsAndDeathsTests.RefusalAsync(mrd, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/report",
            new ReportToRegistrarRequest(Today, "D-2026-TN-00502", corrected.Version))).ShouldBe("death.not_confirmed");

        var again = await WaitingRequestAsync(second, death.Id);
        again.Details.ShouldContain(new ApprovalDetail("Part II", "Chronic obstructive pulmonary disease"));
        await MakerChecker.ApproveAsync(second, again);
        (await mrd.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}")).ConfirmedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_certificate_saved_before_confirmations_were_asked_for_is_confirmed_once_its_certifier_asks()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1947, 4, 1));
        DeathRecordDto death;
        await using (await ConfirmationOffAsync())
        {
            // A facility that does not ask: the certificate is as certified, and nobody is asked.
            death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-4)));
            (death.ConfirmationRequired, death.ConfirmationWaitingSince).ShouldBe((false, null));
            (await BirthsAndDeathsTests.RefusalAsync(doctor, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/confirmation",
                new AskDeathConfirmationRequest(death.Version))).ShouldBe("death.confirmation_off");
        }

        // Once the facility asks, the certificate is not reported until a second doctor confirms it, and its certifier
        // (only they) asks for that.
        var now = await mrd.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}");
        (now.ConfirmationRequired, now.ConfirmationWaitingSince).ShouldBe((true, null));
        (await BirthsAndDeathsTests.RefusalAsync(mrd, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/report",
            new ReportToRegistrarRequest(Today, "D-2026-TN-00503", now.Version))).ShouldBe("death.not_confirmed");
        using (var notTheCertifier = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/confirmation",
                   new AskDeathConfirmationRequest(now.Version)))
        {
            notTheCertifier.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var waiting = await doctor.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}/confirmation", new AskDeathConfirmationRequest(now.Version));
        waiting.ConfirmationWaitingSince.ShouldNotBeNull();
        (await ConflictAsync(doctor, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/confirmation", new AskDeathConfirmationRequest(waiting.Version)))
            .ShouldBe("approval.already_requested");

        await MakerChecker.ApproveAsync(second, await WaitingRequestAsync(second, death.Id));
        var confirmed = await mrd.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}");
        (await mrd.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}/report",
            new ReportToRegistrarRequest(Today, "D-2026-TN-00503", confirmed.Version))).Status.ShouldBe(VitalReportStatus.Reported);
    }

    [Fact]
    public async Task A_certificate_changed_after_its_confirmation_was_asked_for_is_not_confirmed_on_that_request()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1951, 1, 1));
        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-5)));
        var asked = await WaitingRequestAsync(second, death.Id);

        // Changed outside the request (here, straight in the database): the approval fails, and the doctor who asked sees why.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var record = await db.Set<DeathRecord>().IgnoreQueryFilters().SingleAsync(d => d.Id == death.Id, Ct);
            record.AdmissionId = Guid.NewGuid();
            await db.SaveChangesAsync(Ct);
        }

        (await BirthsAndDeathsTests.RefusalAsync(second, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe("approval.stale");
        var failed = await doctor.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        failed.Status.ShouldBe(ApprovalStatus.Failed);
        failed.Outcome.ShouldNotBeNull().ShouldContain("changed after its confirmation was asked for");
        (await doctor.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}")).ConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_doctor_who_may_not_open_a_restricted_patients_record_does_not_confirm_their_certificate()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var patient = await BirthsAndDeathsTests.RegisterAsync(admin, Gender.Male, new DateOnly(1944, 7, 1), vip: true);
        var death = await admin.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-6)));
        var asked = await WaitingRequestAsync(second, death.Id);

        // The request stays for a doctor who may open the record.
        using (var refused = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync(Ct));
        }

        (await second.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
        (await admin.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}")).ConfirmedAt.ShouldBeNull();
    }

    /// <summary>The second doctor confirms the certificate from their approvals; the certificate as it is then.</summary>
    internal static async Task<DeathRecordDto> ConfirmAsync(HospitalServer server, DeathRecordDto death)
    {
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        await MakerChecker.ApproveAsync(second, await WaitingRequestAsync(second, death.Id));
        return await second.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}");
    }

    /// <summary>The request for a second doctor's confirmation of the certificate that waits.</summary>
    internal static async Task<ApprovalRequestDto> WaitingRequestAsync(ApiClient doctor, Guid deathId) =>
        (await doctor.GetAsync<PagedResult<ApprovalRequestDto>>(
            $"/api/v1/approvals?view=Subject&status=Pending&subjectType={nameof(DeathRecord)}&subjectId={deathId}")).Items.ShouldHaveSingleItem();

    private static async Task<string?> ConflictAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private async Task<string?> RegistrationNumberAsync(string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var practitioner = await db.Set<User>().IgnoreQueryFilters().Where(u => u.UserName == userName).Select(u => u.PractitionerId).SingleAsync(Ct);
        return await db.Set<Practitioner>().IgnoreQueryFilters().Where(p => p.Id == practitioner).Select(p => p.RegistrationNumber).SingleAsync(Ct);
    }

    /// <summary>The facility stops asking for confirmations; disposing asks again (the setting removed, so on by default).</summary>
    private async Task<IAsyncDisposable> ConfirmationOffAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await server.SaveSettingAsync(admin, VitalRegistrationService.ConfirmationSetting, new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null));
        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(VitalRegistrationService.ConfirmationSetting, facilityId, Ct);
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
