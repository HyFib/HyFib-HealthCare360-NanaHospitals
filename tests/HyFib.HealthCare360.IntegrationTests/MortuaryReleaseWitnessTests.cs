using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Mortuary;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A body released from the mortuary on the police's clearance (maker-checker, slice 9), over the real API and PostgreSQL:
/// a medico-legal body to the relatives, or an unclaimed body to the municipality, goes only with the clearance recorded and
/// a second member of the mortuary staff witnessing the release at the screen with their own user name and password
/// (ADR-014). The register, the slip and the audit trail name both.
/// </summary>
public sealed class MortuaryReleaseWitnessTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_medico_legal_body_goes_to_the_relatives_on_the_police_clearance_only_with_a_second_member_of_staff_witnessing()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var nurse = await server.SignInAsync(Users.Nurse);
        var doctor = await server.SignInAsync(Users.Doctor);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var slot = await SlotAsync(admin);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1971, 6, 1));
        await doctor.PostAsync<object>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-3)));
        var body = await nurse.PostAsync<BodyDto>("/api/v1/mortuary/bodies", new ReceiveBodyRequest(
            BodySource.InHospitalDeath, patient.Id, null, true, null, null, null, "Ward 6 attender", IsMlc: true, "MLC 41/2026", "B2 Ashok Nagar", null,
            "Wallet, phone", slot));
        var release = new ReleaseBodyRequest(ReleaseTo.Relatives, "Suresh Kumar", "Son", "Aadhaar XXXX-4410", "PM 211/2026, Govt. Rajaji Hospital", true, null,
            body.Version);
        var path = $"/api/v1/mortuary/bodies/{body.Id}/release";

        // The clearance typed by one nurse alone no longer releases the body.
        (await BirthsAndDeathsTests.RefusalAsync(nurse, HttpMethod.Post, path, release)).ShouldBe("mortuary.witness_required");
        (await nurse.GetAsync<BodyDto>($"/api/v1/mortuary/bodies/{body.Id}")).Status.ShouldBe(BodyStatus.InStorage);

        // A confirmation given for another body's patient is not this one's.
        var forSomeoneElse = await server.WitnessAsync(nurse, Users.SecondNurse, MortuaryService.ReleaseWitness.Key, Guid.CreateVersion7());
        (await BirthsAndDeathsTests.RefusalAsync(nurse, HttpMethod.Post, path, release with { WitnessConfirmationId = forSomeoneElse.ConfirmationId }))
            .ShouldBe("witness.confirmation_unusable");

        // A second nurse witnesses at the screen: the body goes, and the record, the slip and the audit trail name both.
        var witness = await server.WitnessAsync(nurse, Users.SecondNurse, MortuaryService.ReleaseWitness.Key, patient.Id);
        var released = await nurse.PostAsync<BodyDto>(path, release with { WitnessConfirmationId = witness.ConfirmationId });
        (released.Status, released.PoliceClearance, released.ReleaseWitnessName).ShouldBe((BodyStatus.Released, "PM 211/2026, Govt. Rajaji Hospital", witness.Name));
        released.ReleasedByName.ShouldNotBe(witness.Name);
        var slip = await nurse.GetAsync<PrintableDocumentDto>($"/api/v1/mortuary/bodies/{body.Id}/slip");
        slip.Html.ShouldContain("Release witnessed by");
        slip.Html.ShouldContain(witness.Name);
        var audit = await AuditAsync(body.Id);
        audit.ShouldContain($"by {released.ReleasedByName} on the police's clearance PM 211/2026");
        audit.ShouldContain($"witnessed by {witness.Name}");
    }

    [Fact]
    public async Task An_unclaimed_body_goes_to_the_municipality_only_with_a_witness()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var nurse = await server.SignInAsync(Users.Nurse);
        var slot = await SlotAsync(admin);
        var body = await nurse.PostAsync<BodyDto>("/api/v1/mortuary/bodies", new ReceiveBodyRequest(
            BodySource.Police, null, "Unknown female, about 60", false, "Female", 60, null, "HC 2231, B2 police station", true, "MLC 52/2026", "B2 Ashok Nagar",
            "Mole on the right cheek", null, slot));
        var release = new ReleaseBodyRequest(ReleaseTo.Municipality, "Corporation sanitary inspector", null, null, "Letter 91/2026, B2 Ashok Nagar", false, null,
            body.Version);
        var path = $"/api/v1/mortuary/bodies/{body.Id}/release";

        (await BirthsAndDeathsTests.RefusalAsync(nurse, HttpMethod.Post, path, release)).ShouldBe("mortuary.witness_required");

        // No patient record: the witness confirms for the body, with no patient named.
        var witness = await server.WitnessAsync(nurse, Users.SecondNurse, MortuaryService.ReleaseWitness.Key, null);
        var released = await nurse.PostAsync<BodyDto>(path, release with { WitnessConfirmationId = witness.ConfirmationId });
        (released.ReleasedTo, released.ReleaseWitnessName).ShouldBe(((ReleaseTo?)ReleaseTo.Municipality, witness.Name));
        (await AuditAsync(body.Id)).ShouldContain($"witnessed by {witness.Name}");
    }

    private static async Task<Guid> SlotAsync(ApiClient admin)
    {
        var code = "W" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return (await admin.PutAsync<List<MortuarySlotDto>>("/api/v1/mortuary/slots", new SaveSlotsRequest([new SlotInput(null, code, "Chamber W", true, null)])))
            .Single(s => s.Code == code).Id;
    }

    private async Task<string> AuditAsync(Guid bodyId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = bodyId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityId == id && e.Action == "mortuary.body_released")
            .Select(e => e.Summary ?? string.Empty)
            .SingleAsync(Ct);
    }
}
