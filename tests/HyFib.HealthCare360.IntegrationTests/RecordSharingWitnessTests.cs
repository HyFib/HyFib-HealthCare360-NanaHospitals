using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A consent to share the patient's whole record (no recipient, purpose, kinds of records or period) recorded with no signed
/// form (MAKER-CHECKER.md, slice 14): a second member of staff witnesses it at the screen with their own credentials
/// (ADR-014, purpose <c>consent.record_sharing</c>), and the consent names them. A scoped consent, a refusal, or one with the
/// signed form attached is recorded as before.
/// </summary>
public sealed class RecordSharingWitnessTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sharing_the_whole_record_with_no_signed_form_is_witnessed_at_the_screen()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);
        var path = $"/api/v1/patients/{patient.Id}/consents";
        var whole = new CaptureConsentRequest(ConsentType.DataSharing, null, true, "Typed name", null, null, null, null);

        using (var alone = await frontOffice.SendRawAsync(HttpMethod.Post, path, whole))
        {
            alone.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await alone.Content.ReadAsStringAsync(Ct)).ShouldContain("consent.witness_required");
        }

        (await frontOffice.GetAsync<List<ConsentDto>>(path)).ShouldNotContain(c => c.Type == ConsentType.DataSharing);

        // A confirmation given for something else, or for another patient, does not stand in.
        var forAnother = await server.WitnessAsync(frontOffice, Users.Nurse, "consent.record_sharing", (await RegisterAsync(frontOffice)).Id);
        using (var wrong = await frontOffice.SendRawAsync(HttpMethod.Post, path, whole with { WitnessConfirmationId = forAnother.ConfirmationId }))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("witness.confirmation_unusable");
        }

        var witness = await server.WitnessAsync(frontOffice, Users.Nurse, "consent.record_sharing", patient.Id);
        var given = await frontOffice.PostAsync<Created>(path, whole with { WitnessConfirmationId = witness.ConfirmationId });
        var consent = (await frontOffice.GetAsync<List<ConsentDto>>(path)).Single(c => c.Id == given.Id);
        (consent.IsActive, consent.WitnessName, consent.SharingScope).ShouldBe((true, witness.Name, (RecordSharingScopeDto?)null));

        await using var scope = server.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.EntityId == given.Id.ToString() && a.Action == "patient.consent.witnessed", Ct);
        audit.Summary!.ShouldContain(witness.Name);
    }

    [Fact]
    public async Task A_signed_form_attached_or_a_refusal_needs_no_witness_and_a_form_that_is_not_there_does_not_count()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);
        var path = $"/api/v1/patients/{patient.Id}/consents";

        using (var missing = await frontOffice.SendRawAsync(HttpMethod.Post, path,
                   new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, Guid.NewGuid(), null)))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await missing.Content.ReadAsStringAsync(Ct)).ShouldContain("documentFileId");
        }

        var form = await PatientDocumentTests.UploadAsync(frontOffice, patient.Id);
        await frontOffice.PostAsync<Created>(path, new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, form, null));
        await frontOffice.PostAsync<Created>(path, new CaptureConsentRequest(ConsentType.DataSharing, null, false, null, null, null, null, null));
    }

    private static Task<PatientCreated> RegisterAsync(ApiClient frontOffice) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Radha", MiddleName: null, LastName: "Witness" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray()), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10)))), AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: true));

    private sealed record Created(Guid Id);
}
