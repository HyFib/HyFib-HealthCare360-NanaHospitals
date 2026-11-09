using System.Net;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A practitioner's HPR ID checked against ABDM's Healthcare Professionals Registry (here its simulator): the name,
/// council and registration number compared, the result shown on the practitioner master with when, by whom and against
/// what, and a check that no longer vouches for the master once it changes.
/// </summary>
public sealed class HprVerificationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_hpr_id_is_verified_against_the_registry_and_shown_on_the_master()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var hprId = "71-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999);
        var practitionerId = await PractitionerAsync(hprId, "Tamil Nadu Medical Council", "TNMC/0012345");
        var registry = (SimulatedHprRegistry)server.Services.GetRequiredService<IHprRegistry>();

        // Not in the registry.
        var notFound = await CheckAsync(admin, practitionerId);
        (notFound.Result, notFound.Source, notFound.Current).ShouldBe((HprCheckResult.NotFound, "simulator", true));

        // In it, with the same name (titles, case and order aside), council and number (punctuation and zeros aside).
        registry.Add(new HprRegistryRecord(hprId, "MENON ASHA", "Medical Council of Tamil Nadu", "TNMC 12345", Active: true));
        var verified = await CheckAsync(admin, practitionerId);
        (verified.Result, verified.Mismatches.Count).ShouldBe((HprCheckResult.Verified, 0));

        // The master shows the latest check, by whom and when.
        var shown = (await admin.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{practitionerId}")).HprCheck.ShouldNotBeNull();
        (shown.Result, shown.Current, shown.CheckedByName.Length > 0).ShouldBe((HprCheckResult.Verified, true, true));

        // A different council and a suspended registration are listed.
        registry.Add(new HprRegistryRecord(hprId, "Asha Menon", "Karnataka Medical Council", "TNMC12345", Active: false));
        var mismatch = await CheckAsync(admin, practitionerId);
        mismatch.Result.ShouldBe(HprCheckResult.Mismatch);
        mismatch.Mismatches.ShouldBe(["council", "status"]);
        mismatch.RegistryCouncil.ShouldBe("Karnataka Medical Council");

        // Once the registration number changes, the check no longer vouches for the master.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var practitioner = await db.Set<Practitioner>().FirstAsync(p => p.Id == practitionerId, Ct);
            practitioner.RegistrationNumber = "TNMC/99999";
            await db.SaveChangesAsync(Ct);
        }

        (await admin.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{practitionerId}")).HprCheck!.Current.ShouldBeFalse();

        // Checking a version the administrator did not see is refused; front office may not check at all.
        using (var stale = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/masters/practitioners/{practitionerId}/hpr-check", new CheckHprRequest(1)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/masters/practitioners/{practitionerId}/hpr-check", new CheckHprRequest(1));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<PractitionerHprCheckDto> CheckAsync(ApiClient admin, Guid practitionerId)
    {
        var version = (await admin.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{practitionerId}")).Version;
        return await admin.PostAsync<PractitionerHprCheckDto>($"/api/v1/masters/practitioners/{practitionerId}/hpr-check", new CheckHprRequest(version));
    }

    private async Task<Guid> PractitionerAsync(string hprId, string council, string number)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var practitioner = new Practitioner
        {
            Code = "HPR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Title = "Dr.",
            FirstName = "Asha",
            LastName = "Menon",
            DisplayName = "Dr. Asha Menon",
            Kind = PractitionerKind.Consultant,
            RegistrationCouncil = council,
            RegistrationNumber = number,
            HprId = hprId,
        };
        db.Add(practitioner);
        await db.SaveChangesAsync(Ct);
        return practitioner.Id;
    }
}
