using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The second doctor's opinion on a termination from 20 weeks (maker-checker, slice 9; MTP Act s.3(2)(b) as amended in
/// 2021), over the real API and PostgreSQL: it is never typed on the case; the second registered medical practitioner
/// records it from their own sign-in, or confirms it at the first doctor's screen with their own user name and password
/// (ADR-014). Their name and registration come from their own practitioner record; they are not the doctor who certified
/// the case nor the doctor of the first opinion; the termination waits for it; and the audit trail names both doctors.
/// </summary>
public sealed class MtpSecondOpinionTests(HospitalServer server)
{
    private const string Approval = "MTP/TN/MDU/2026/007";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    [Fact]
    public async Task The_second_doctor_records_their_own_opinion_from_their_sign_in_before_the_termination()
    {
        var admin = await server.SignInAsync(Users.Admin);
        var gynae = await server.SignInAsync(Users.Gynaecologist);
        var secondGynae = await server.SignInAsync(Users.SecondGynaecologist);
        var doctor = await server.SignInAsync(Users.Doctor);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin, "Mtp");

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(MtpService.ApprovalSetting, Approval, facilityId, false, Ct);
        try
        {
            // The first doctor cannot type the second opinion.
            (await RefusalAsync(gynae, HttpMethod.Post, "/api/v1/mtp/cases", Late(patient.Id) with
            {
                Opinions = [new MtpOpinion("Dr. Sneha Iyer", "REG-DR004", Today), new MtpOpinion("Dr. Meera Pillai", "REG-DR006", Today)],
            })).ShouldBe("mtp.second_opinion_typed");

            var opened = await gynae.PostAsync<MtpCaseDto>("/api/v1/mtp/cases", Late(patient.Id));
            opened.AwaitingSecondOpinion.ShouldBeTrue();
            var second = $"/api/v1/mtp/cases/{opened.Id}/second-opinion";

            // Until the second doctor gives it, the termination waits.
            (await RefusalAsync(gynae, HttpMethod.Post, $"/api/v1/mtp/cases/{opened.Id}/termination", Termination(opened.Version)))
                .ShouldBe("mtp.second_opinion_required");

            // Not the doctor who certified the case; not a user who may not certify; not someone who is no registered doctor.
            (await RefusalAsync(gynae, HttpMethod.Post, second, new RecordSecondOpinionRequest(Today, null, opened.Version))).ShouldBe("mtp.second_opinion_same_doctor");
            using (var notMtp = await doctor.SendRawAsync(HttpMethod.Post, second, new RecordSecondOpinionRequest(Today, null, opened.Version)))
            {
                notMtp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            (await RefusalAsync(admin, HttpMethod.Post, second, new RecordSecondOpinionRequest(Today, null, opened.Version))).ShouldBe("mtp.second_opinion_not_rmp");

            // The second gynaecologist gives it from her own sign-in: her name and registration from her practitioner record.
            var confirmed = await secondGynae.PostAsync<MtpCaseDto>(second, new RecordSecondOpinionRequest(Today, null, opened.Version));
            confirmed.AwaitingSecondOpinion.ShouldBeFalse();
            var opinion = confirmed.Opinions.Single(o => o.Confirmed);
            (opinion.DoctorName, opinion.RegistrationNumber, opinion.OpinionOn).ShouldBe(("Dr. Meera Pillai", "REG-DR006", Today));
            opinion.UserId.ShouldBe(opinion.RecordedBy);
            var audit = await AuditAsync(patient.Id, "mtp.second_opinion_recorded");
            audit.ShouldContain("Dr. Meera Pillai (REG-DR006), recorded from their own sign-in");
            audit.ShouldContain("first opinion Dr. Sneha Iyer (REG-DR004)");
            (await RefusalAsync(gynae, HttpMethod.Post, second, new RecordSecondOpinionRequest(Today, null, confirmed.Version))).ShouldBe("mtp.second_opinion_recorded");

            var done = await gynae.PostAsync<MtpCaseDto>($"/api/v1/mtp/cases/{opened.Id}/termination", Termination(confirmed.Version));
            done.Status.ShouldBe(MtpCaseStatus.Terminated);
            var row = (await gynae.GetAsync<MtpRegister>($"/api/v1/mtp/register?year={Today.Year}&month={Today.Month}")).Rows.Single(r => r.Id == opened.Id);
            row.OpinionBy.ShouldBe("Dr. Sneha Iyer (REG-DR004); Dr. Meera Pillai (REG-DR006)");
        }
        finally
        {
            await settings.RemoveAsync(MtpService.ApprovalSetting, facilityId, Ct);
        }
    }

    [Fact]
    public async Task The_second_doctor_confirms_at_the_first_doctors_screen_and_a_correction_asks_for_it_again()
    {
        var admin = await server.SignInAsync(Users.Admin);
        var gynae = await server.SignInAsync(Users.Gynaecologist);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin, "Mtp");
        var other = await RegisterWomanAsync(admin, "Mtp");

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(MtpService.ApprovalSetting, Approval, facilityId, false, Ct);
        try
        {
            var opened = await gynae.PostAsync<MtpCaseDto>("/api/v1/mtp/cases", Late(patient.Id));
            var second = $"/api/v1/mtp/cases/{opened.Id}/second-opinion";

            // The second gynaecologist confirms at the first's screen with her own credentials.
            var witness = await server.WitnessAsync(gynae, Users.SecondGynaecologist, MtpService.SecondOpinionWitness.Key, patient.Id);
            var confirmed = await gynae.PostAsync<MtpCaseDto>(second, new RecordSecondOpinionRequest(Today, witness.ConfirmationId, opened.Version));
            var opinion = confirmed.Opinions.Single(o => o.Confirmed);
            (opinion.DoctorName, opinion.UserId).ShouldBe(("Dr. Meera Pillai", (Guid?)witness.UserId));
            opinion.RecordedBy.ShouldNotBe(opinion.UserId);
            (await AuditAsync(patient.Id, "mtp.second_opinion_recorded")).ShouldContain("confirmed with their own credentials at the screen of Dr. Sneha Iyer");

            // A correction drops it, audited: the second doctor gives it again on the case as it now reads.
            var corrected = await gynae.PutAsync<MtpCaseDto>($"/api/v1/mtp/cases/{opened.Id}", Late(patient.Id) with { WeeksOfGestation = 23, Version = confirmed.Version });
            corrected.AwaitingSecondOpinion.ShouldBeTrue();
            corrected.Opinions.ShouldAllBe(o => !o.Confirmed);
            (await AuditAsync(patient.Id, "mtp.second_opinion_dropped")).ShouldContain("Dr. Meera Pillai");

            // A confirmation is spent once: it is not used again for this case.
            (await RefusalAsync(gynae, HttpMethod.Post, second, new RecordSecondOpinionRequest(Today, witness.ConfirmationId, corrected.Version)))
                .ShouldBe("witness.confirmation_unusable");

            // The first opinion typed as the second doctor's registration: she is not the second doctor for this case.
            var typedAsHer = await admin.PostAsync<MtpCaseDto>("/api/v1/mtp/cases", Late(other.Id) with
            {
                Opinions = [new MtpOpinion("Dr. M. Pillai", "reg-dr006", Today)],
            });
            var again = await server.WitnessAsync(admin, Users.SecondGynaecologist, MtpService.SecondOpinionWitness.Key, other.Id);
            (await RefusalAsync(admin, HttpMethod.Post, $"/api/v1/mtp/cases/{typedAsHer.Id}/second-opinion",
                new RecordSecondOpinionRequest(Today, again.ConfirmationId, typedAsHer.Version))).ShouldBe("mtp.second_opinion_same_doctor");
        }
        finally
        {
            await settings.RemoveAsync(MtpService.ApprovalSetting, facilityId, Ct);
        }
    }

    /// <summary>A termination at 22 weeks for a woman in a rule 3B category, with the first doctor's opinion only.</summary>
    private static SaveMtpCaseRequest Late(Guid patientId) => new(
        patientId, AdmissionId: null, AgeYears: 27, MentallyIll: false, MtpConsentBy.Woman, GuardianName: null, GuardianRelation: null,
        ConsentOn: Today, ConsentFileId: null, WeeksOfGestation: 22, Grounds: [MtpGround.InjuryToMentalHealth], Category: MtpRule3BCategory.ChangeOfMaritalStatus,
        MedicalBoardReference: null, Opinions: [new MtpOpinion("Dr. Sneha Iyer", "REG-DR004", Today)]);

    private static RecordMtpTerminationRequest Termination(uint version) =>
        new(Today, MtpMethod.Medical, "Dr. Sneha Iyer", Today, "Complete abortion, discharged well", null, version);

    private async Task<string> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.Summary ?? string.Empty)
            .FirstAsync(Ct);
    }

    private static async Task<string?> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    internal static Task<PatientCreated> RegisterWomanAsync(ApiClient api, string prefix) =>
        api.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Anitha"), MiddleName: null,
            LastName: prefix + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1999, 3, 1).AddDays(Random.Shared.Next(300)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture), AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: "Hindu", Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
}
