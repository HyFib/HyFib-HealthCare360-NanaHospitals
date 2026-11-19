using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A merge keeps a restricted (VIP) record restricted (SECURITY.md, Restricted records). Lifting a restriction waits for
/// someone who may open restricted records, but a merge needs only <c>patients.merge</c>: merging a VIP's duplicate into an
/// unrestricted record carried the duplicate's categories and identifiers over and left the restriction behind, so the
/// VIP's whole record became open to every desk. Now the record kept takes the restriction, audited, and a merge with a
/// restricted record is asked for and approved only by someone who may open it.
/// </summary>
public sealed class RestrictedMergeTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_restricted_duplicate_merged_into_an_unrestricted_record_leaves_it_restricted()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var hospitalAdmin = await server.SignInAsync(Users.HospitalAdmin);
        var lastName = "Merged" + Letters(8);
        var kept = await VisitAndQueuePrivacyTests.RegisterAsync(frontOffice, "Rajiv", lastName, isVip: false);
        var duplicate = await VisitAndQueuePrivacyTests.RegisterAsync(frontOffice, "Rajiv", lastName, isVip: true);
        var visit = await new VisitAndQueuePrivacyTests(server).CheckInAsync(frontOffice, kept.Id, "Fever since yesterday");
        try
        {
            // Not restricted yet: the desk reads the record kept and its visits.
            (await frontOffice.GetAsync<PagedResult<VisitListItem>>($"/api/v1/patients/{kept.Id}/visits")).Items.ShouldHaveSingleItem().Id.ShouldBe(visit.Id);

            // The medical records officer may merge records but not open the restricted duplicate, so may not ask for its merge.
            var merge = new MergePatientRequest(kept.Id, "Registered twice at the desk");
            using (var refused = await (await server.SignInAsync(Users.MrdOfficer)).SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{duplicate.Id}/merge", merge))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await CodeAsync(refused)).ShouldBe("patient.restricted");
            }

            // The hospital administrator may. Another officer who may merge records, but not open restricted ones, is refused
            // the approval, and the request waits for someone who may.
            var asked = await hospitalAdmin.PostAsync<ApprovalRequestDto>($"/api/v1/patients/{duplicate.Id}/merge", merge);
            using (var notTheVerifier = await (await server.SignInAsync(Users.MrdVerifier)).SendRawAsync(HttpMethod.Post,
                       $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Same person", asked.Version)))
            {
                notTheVerifier.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await CodeAsync(notTheVerifier)).ShouldBe("patient.restricted");
            }

            var superAdmin = await server.SignInAsync(Users.Admin);
            var waiting = await superAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
            waiting.Status.ShouldBe(ApprovalStatus.Pending);

            // The Super Administrator approves: the record kept is restricted now, and the trail says the merge carried it.
            await MakerChecker.ApproveAsync(superAdmin, waiting);
            (await hospitalAdmin.GetAsync<PatientDetail>($"/api/v1/patients/{kept.Id}")).IsVip.ShouldBeTrue();
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var carried = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                    .Where(e => e.PatientId == kept.Id && e.Action == "patient.restriction_carried")
                    .ToListAsync(Ct);
                carried.ShouldHaveSingleItem().Summary.ShouldBe($"VIP restriction carried over to {kept.Uhid} by the merge of {duplicate.Uhid}");
            }

            // Its history is masked for the desk now: search and the visit list name him by initials, and his record and
            // his visits are closed to them.
            (await frontOffice.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={lastName}")).Items.Single(p => p.Id == kept.Id)
                .FullName.ShouldEndWith("(restricted)");
            (await frontOffice.GetAsync<PagedResult<VisitListItem>>($"/api/v1/visits?search={lastName}")).Items.Single(v => v.Id == visit.Id)
                .PatientName.ShouldEndWith("(restricted)");
            foreach (var path in new[] { $"/api/v1/patients/{kept.Id}", $"/api/v1/patients/{kept.Id}/visits", $"/api/v1/visits/{visit.Id}" })
            {
                using var closed = await frontOffice.SendRawAsync(HttpMethod.Get, path);
                closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            }
        }
        finally
        {
            await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/complete");
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
