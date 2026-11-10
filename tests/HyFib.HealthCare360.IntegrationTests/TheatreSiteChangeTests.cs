using System.Net;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.SharedKernel;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The side and the site of an operation (maker-checker slice 10), over the real API and PostgreSQL. With a side booked, the
/// site is marked by the surgeon, never "not applicable". Once the WHO Sign-in is done, only the operating surgeon or the
/// anaesthetist changes the side or procedure, with a reason; the Sign-in and Time-out are voided and redone, the change is
/// audited at Warning and the theatre team is told. After the incision the side is not changed.
/// </summary>
public sealed class TheatreSiteChangeTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_a_side_booked_the_site_is_marked_by_the_surgeon_and_never_not_applicable()
    {
        var cases = new TheatreCases(server);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.InPreOpAsync(Laterality.Left);
        var detail = await nurse.GetAsync<SurgeryDetail>(c.Path);
        detail.SiteMarkingRequired.ShouldBeTrue();

        // The nurse can neither mark the left groin "not applicable" nor record a mark she did not make.
        var notMarked = new ChecklistInput(SiteMarking.Item, ChecklistItemState.NotApplicable, "Midline");
        var marked = new ChecklistInput(SiteMarking.Item, ChecklistItemState.Done, null);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Put, $"{c.Path}/pre-op-checklist", new RecordChecklistRequest([notMarked], detail.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "checklist.site_marking_required"));
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Put, $"{c.Path}/pre-op-checklist", new RecordChecklistRequest([marked], detail.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "checklist.site_marked_by_surgeon"));

        // The surgeon marks it from his own sign-in; the nurse completes the rest, and the mark keeps his name.
        await cases.PreOpDoneAsync(c);
        var me = await doctor.GetAsync<HyFib.HealthCare360.Platform.Identity.AuthUser>("/api/v1/auth/me");
        detail = await nurse.GetAsync<SurgeryDetail>(c.Path);
        detail.PreOpChecklist.Single(i => i.Key == SiteMarking.Item).RecordedBy.ShouldBe(me.DisplayName);

        // At the Sign-in, too, the site is confirmed, not waved through.
        var signIn = TheatreCases.AllDone(detail, SafetyPhase.SignIn);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{c.Path}/safety-checklist/sign-in",
                new RecordChecklistRequest(signIn.Items.Select(i => i.Key == SiteMarking.Item ? notMarked : i).ToList())))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "checklist.site_marking_required"));
        await nurse.PostAsync($"{c.Path}/safety-checklist/sign-in", signIn);

        // A midline operation may still say why no mark is needed.
        var midline = await cases.InPreOpAsync(Laterality.NotApplicable, "Exploratory laparotomy");
        var open = await nurse.GetAsync<SurgeryDetail>(midline.Path);
        open.SiteMarkingRequired.ShouldBeFalse();
        await nurse.PutAsync($"{midline.Path}/pre-op-checklist", new RecordChecklistRequest([notMarked with { Remarks = "Midline incision" }], open.Version));
    }

    [Fact]
    public async Task After_the_sign_in_only_the_surgeon_or_the_anaesthetist_changes_the_side_and_the_checklist_is_done_again()
    {
        var cases = new TheatreCases(server);
        var doctor = await server.SignInAsync(Users.Doctor);
        var anaesthetist = await server.SignInAsync(Users.SecondDoctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var outsider = await OutsideDoctorAsync();
        var c = await cases.InPreOpAsync(Laterality.Left);
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);
        var signedIn = await nurse.GetAsync<SurgeryDetail>(c.Path);
        signedIn.SafetyChecklist.Where(p => p.Phase != SafetyPhase.SignOut).ShouldAllBe(p => p.IsComplete);

        // The consent says right. A doctor who is not of this case's team may not change it now; the surgeon needs a reason.
        var right = TheatreCases.Plan(signedIn) with { Laterality = Laterality.Right };
        (await TheatreCases.RefusalAsync(outsider, HttpMethod.Put, c.Path, right with { ChangeReason = "Consent says right" }))
            .ShouldBe((HttpStatusCode.Forbidden, "surgery.site_change_team"));
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Put, c.Path, right)).ShouldBe((HttpStatusCode.BadRequest, "validation"));

        // The anaesthetist of the case changes it, with the reason: the Sign-in and Time-out no longer stand, nor does the mark.
        await anaesthetist.PutAsync(c.Path, right with { ChangeReason = "Consent form and MRI say right inguinal hernia" });
        var changed = await nurse.GetAsync<SurgeryDetail>(c.Path);
        changed.Laterality.ShouldBe(Laterality.Right);
        changed.SafetyChecklist.ShouldAllBe(p => !p.IsComplete && p.Items.All(i => i.State == ChecklistItemState.Pending));
        changed.PreOpChecklist.Single(i => i.Key == SiteMarking.Item).State.ShouldBe(ChecklistItemState.Pending);
        var voided = changed.ChecklistVoids.ShouldNotBeNull().ShouldHaveSingleItem();
        (voided.Before, voided.After, voided.Reason).ShouldBe(("Inguinal hernia repair, left", "Inguinal hernia repair, right", "Consent form and MRI say right inguinal hernia"));
        voided.Phases.ShouldBe([SafetyPhase.SignIn, SafetyPhase.TimeOut]);
        voided.Voided.ShouldNotBeEmpty();
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Post, $"{c.Path}/start", null)).ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.checklist_incomplete"));

        var (summary, severity) = await cases.AuditAsync(c.PatientId, "surgery.site_changed_after_sign_in");
        severity.ShouldBe(AuditSeverity.Warning);
        summary.ShouldContain("Inguinal hernia repair, left → Inguinal hernia repair, right");

        // The surgeon is told; the anaesthetist, who changed it, is not told of his own change.
        (await doctor.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=50")).Items
            .ShouldContain(n => n.Link == $"/ot/bookings/{c.BookingId}" && n.Title.Contains("redo the WHO checklist", StringComparison.Ordinal));
        (await anaesthetist.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=50")).Items
            .ShouldNotContain(n => n.Link == $"/ot/bookings/{c.BookingId}");

        // The right side is marked, the Sign-in and Time-out done again, and the operation starts.
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);
        await doctor.PostAsync($"{c.Path}/start");

        // After the incision the side is not changed: what was found goes in the operation note.
        var underWay = await nurse.GetAsync<SurgeryDetail>(c.Path);
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Put, c.Path, TheatreCases.Plan(underWay) with { Laterality = Laterality.Bilateral, ChangeReason = "Found both sides" }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.site_after_incision"));
    }

    [Fact]
    public async Task Before_the_sign_in_the_plan_is_edited_as_before_and_a_mark_on_the_old_side_is_made_again()
    {
        var cases = new TheatreCases(server);
        var outsider = await OutsideDoctorAsync();
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.InPreOpAsync(Laterality.Left);
        await cases.PreOpDoneAsync(c);

        var before = await nurse.GetAsync<SurgeryDetail>(c.Path);
        await outsider.PutAsync(c.Path, TheatreCases.Plan(before) with { Laterality = Laterality.Right });

        var after = await nurse.GetAsync<SurgeryDetail>(c.Path);
        after.Laterality.ShouldBe(Laterality.Right);
        after.ChecklistVoids.ShouldNotBeNull().ShouldBeEmpty();
        after.PreOpChecklist.Single(i => i.Key == SiteMarking.Item).State.ShouldBe(ChecklistItemState.Pending);
        after.PreOpComplete.ShouldBeFalse();
    }

    /// <summary>A doctor who may book surgery but is on no team of these cases (an account with no practitioner record).</summary>
    private async Task<ApiClient> OutsideDoctorAsync()
    {
        var userName = "otdoc" + Guid.NewGuid().ToString("N")[..8];
        await server.CreateStaffAccountAsync(userName, HyFib.HealthCare360.Platform.Security.SystemRoles.Doctor);
        return await server.SignInAsync(userName);
    }
}
