using System.Net;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The swab, needle and instrument count (maker-checker slice 10), over the real API and PostgreSQL: each count is made by
/// one nurse and witnessed by a second from her own sign-in; a final count that does not tally opens a discrepancy that is
/// settled (found, an X-ray, or a retained item raised in the Quality register as a never event) before the WHO Sign-out.
/// </summary>
public sealed class TheatreCountTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_count_needs_a_second_nurse_and_the_sign_out_needs_the_counts_not_a_tick()
    {
        var cases = new TheatreCases(server);
        var nurse = server.FromOwnAddress(await server.SignInAsync(Users.Nurse));
        var c = await cases.UnderWayAsync();

        // No witness, or one confirmed for another purpose, is no count.
        (await TheatreCases.InvalidFieldsAsync(nurse, HttpMethod.Post, $"{c.Path}/counts",
            new RecordSurgicalCountRequest(CountCategory.Swabs, CountStage.Initial, 10, Guid.Empty))).ShouldContain("witnessConfirmationId");

        var forADose = (await server.WitnessAsync(nurse, Users.SecondNurse, "mar.dose", c.PatientId)).ConfirmationId;
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{c.Path}/counts", new RecordSurgicalCountRequest(CountCategory.Swabs, CountStage.Initial, 10, forADose)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "witness.confirmation_unusable"));

        // Ticking "counts correct" with nothing counted is refused.
        var detail = await nurse.GetAsync<SurgeryDetail>(c.Path);
        var signOut = TheatreCases.AllDone(detail, SafetyPhase.SignOut);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{c.Path}/safety-checklist/sign-out", signOut))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.counts_missing"));

        // Counted at the start, more opened, counted at the end: it tallies, and each count names both nurses.
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Initial, 10);
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Additional, 5);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{c.Path}/safety-checklist/sign-out", signOut))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.counts_incomplete"));
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Final, 15);

        var counted = await nurse.GetAsync<SurgeryDetail>(c.Path);
        var tally = counted.CountTallies.ShouldNotBeNull().ShouldHaveSingleItem();
        (tally.Category, tally.Expected, tally.Final, tally.Tallies).ShouldBe((CountCategory.Swabs, 15, 15, true));
        counted.Counts.ShouldNotBeNull().ShouldAllBe(n => n.WitnessName != n.CountedByName && n.WitnessName.Length > 0);
        counted.CountDiscrepancies.ShouldNotBeNull().ShouldBeEmpty();

        await nurse.PostAsync($"{c.Path}/safety-checklist/sign-out", signOut);
        await (await server.SignInAsync(Users.Doctor)).PostAsync($"{c.Path}/complete");
    }

    [Fact]
    public async Task A_count_that_does_not_tally_holds_the_sign_out_until_an_x_ray_settles_it()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.UnderWayAsync();
        await cases.CountedAsync(c, CountCategory.Needles, CountStage.Initial, 6);
        await cases.CountedAsync(c, CountCategory.Needles, CountStage.Final, 5);

        var detail = await nurse.GetAsync<SurgeryDetail>(c.Path);
        var open = detail.CountDiscrepancies.ShouldNotBeNull().ShouldHaveSingleItem();
        (open.Category, open.Expected, open.Counted, open.IsOpen).ShouldBe((CountCategory.Needles, 6, 5, true));
        (await cases.AuditAsync(c.PatientId, "surgery.count_discrepancy")).Severity.ShouldBe(AuditSeverity.Warning);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{c.Path}/safety-checklist/sign-out", TheatreCases.AllDone(detail, SafetyPhase.SignOut)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.count_discrepancy_open"));

        // An X-ray with no result is not a settlement; with its result it is.
        var resolve = $"{c.Path}/counts/discrepancies/{open.Id}/resolve";
        (await TheatreCases.InvalidFieldsAsync(nurse, HttpMethod.Post, resolve, new ResolveCountDiscrepancyRequest(DiscrepancyResolution.XrayNoRetainedItem, "X-ray on the table")))
            .ShouldContain("xrayResult");
        var settled = await nurse.PostAsync<CountDiscrepancyResolved>(resolve,
            new ResolveCountDiscrepancyRequest(DiscrepancyResolution.XrayNoRetainedItem, "X-ray on the table", "No radio-opaque foreign body"));
        (settled.Resolution, settled.IncidentNumber).ShouldBe((DiscrepancyResolution.XrayNoRetainedItem, null));

        await nurse.PostAsync($"{c.Path}/safety-checklist/sign-out", TheatreCases.AllDone(await nurse.GetAsync<SurgeryDetail>(c.Path), SafetyPhase.SignOut));
        var signedOut = await nurse.GetAsync<SurgeryDetail>(c.Path);
        signedOut.SafetyChecklist.Single(p => p.Phase == SafetyPhase.SignOut).IsComplete.ShouldBeTrue();
        signedOut.CountDiscrepancies!.Single().ResolvedByName.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_retained_swab_is_raised_in_the_quality_register_as_a_never_event()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var quality = await server.SignInAsync(Users.QualityManager);
        var c = await cases.UnderWayAsync();
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Initial, 20);
        await cases.CountedAsync(c, CountCategory.Swabs, CountStage.Final, 19);
        var open = (await nurse.GetAsync<SurgeryDetail>(c.Path)).CountDiscrepancies!.Single();

        // A retained item is graded for the report.
        var resolve = $"{c.Path}/counts/discrepancies/{open.Id}/resolve";
        (await TheatreCases.InvalidFieldsAsync(nurse, HttpMethod.Post, resolve, new ResolveCountDiscrepancyRequest(DiscrepancyResolution.RetainedItem, "Swab seen on X-ray")))
            .ShouldContain("harm");
        var settled = await nurse.PostAsync<CountDiscrepancyResolved>(resolve, new ResolveCountDiscrepancyRequest(
            DiscrepancyResolution.RetainedItem, "Swab seen in the pelvis on X-ray; surgeon informed, re-exploration planned", "Radio-opaque swab marker in the pelvis",
            IncidentHarm.Moderate));
        var number = settled.IncidentNumber.ShouldNotBeNull();

        // The quality team finds it: sentinel, a retained foreign object, on this patient, named to the nurse who raised it.
        var incident = await quality.GetAsync<IncidentDetail>($"/api/v1/quality/incidents/{settled.IncidentId}");
        (incident.IncidentNumber, incident.IsSentinel, incident.SentinelKind, incident.Severity, incident.PatientId)
            .ShouldBe((number, true, SentinelEventKind.RetainedForeignObject, IncidentSeverity.Moderate, c.PatientId));
        (await cases.AuditAsync(c.PatientId, "surgery.count_discrepancy_settled")).Summary.ShouldContain(number);

        // The count is settled as a retained item, and the Sign-out records it.
        await nurse.PostAsync($"{c.Path}/safety-checklist/sign-out", TheatreCases.AllDone(await nurse.GetAsync<SurgeryDetail>(c.Path), SafetyPhase.SignOut));
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).CountDiscrepancies!.Single().IncidentNumber.ShouldBe(number);
    }
}
