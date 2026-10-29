using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Copies of medical records within 72 hours: a request from the patient, their attendant or a legal authority is
/// acknowledged, and the documents are issued within 72 hours (Indian Medical Council (Professional Conduct, Etiquette and
/// Ethics) Regulations 2002, reg. 1.3.2). Each such request carries the time its copy is due, and medical records sees the
/// ones past it.
/// </summary>
public sealed class RecordCopyDeadlineTests(HospitalServer server)
{
    private static readonly TimeSpan Within = TimeSpan.FromHours(72);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_copy_is_due_72_hours_after_the_request_and_one_not_issued_by_then_is_late()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var patient = (await LabVerificationTests.RegisterPatientAsync(server)).Id;

        // The patient's daughter asks for a copy, and a court for the file; a ward borrows it. All were asked 73 hours ago,
        // and none is issued yet. A second copy asked for then was withdrawn.
        var copy = await RequestAsync(mrd, patient, "Lakshmi (daughter)", RecordRequestPurpose.PatientCopy);
        var court = await RequestAsync(mrd, patient, "Additional District Judge, Chennai", RecordRequestPurpose.Legal);
        var ward = await RequestAsync(mrd, patient, "Dr. Meera Iyer", RecordRequestPurpose.PatientCare);
        var withdrawn = await RequestAsync(mrd, patient, "Lakshmi (daughter)", RecordRequestPurpose.PatientCopy);
        await AgeAsync([copy, court, ward, withdrawn], TimeSpan.FromHours(73));
        await mrd.PostAsync<JsonElement>($"/api/v1/mrd/record-requests/{withdrawn}/cancel", new CancelRecordRequest("Withdrawn by the daughter"));

        // A copy asked for an hour ago is due in 71 hours, and is not late.
        var recent = await RequestAsync(mrd, patient, "Lakshmi (daughter)", RecordRequestPurpose.PatientCopy);
        await AgeAsync([recent], TimeSpan.FromHours(1));

        var mine = await RequestsAsync(mrd, "/api/v1/mrd/record-requests?pageSize=100", patient);
        foreach (var id in new[] { copy, court, recent })
        {
            var request = mine[id];
            var requestedAt = request.GetProperty("requestedAt").GetDateTimeOffset();
            request.GetProperty("copyDueAt").GetDateTimeOffset().ShouldBe(requestedAt + Within);
            request.GetProperty("copyLate").GetBoolean().ShouldBe(id != recent);
        }

        // A file lent to a ward is no copy: no 72 hours. A withdrawn copy is owed to no one, and is not late.
        mine[ward].GetProperty("copyDueAt").ValueKind.ShouldBe(JsonValueKind.Null);
        mine[ward].GetProperty("copyLate").GetBoolean().ShouldBeFalse();
        mine[withdrawn].GetProperty("copyLate").GetBoolean().ShouldBeFalse();

        // Medical records lists the copies still waiting past their 72 hours.
        var late = await RequestsAsync(mrd, "/api/v1/mrd/record-requests?copyOverdue=true&pageSize=100", patient);
        late.Keys.ShouldBe([copy, court], ignoreOrder: true);

        // Issued, a copy is no longer waiting, but stays late: it went out after its 72 hours.
        await mrd.PostAsync<JsonElement>($"/api/v1/mrd/record-requests/{copy}/issue", new IssueRecordRequest(null));
        (await RequestsAsync(mrd, "/api/v1/mrd/record-requests?copyOverdue=true&pageSize=100", patient)).Keys.ShouldBe([court]);
        (await RequestsAsync(mrd, "/api/v1/mrd/record-requests?pageSize=100", patient))[copy].GetProperty("copyLate").GetBoolean().ShouldBeTrue();
    }

    private static async Task<Guid> RequestAsync(ApiClient mrd, Guid patient, string by, RecordRequestPurpose purpose) =>
        (await mrd.PostAsync<JsonElement>("/api/v1/mrd/record-requests", new CreateRecordRequestRequest(patient, null, by, null, purpose, null)))
        .GetProperty("id").GetGuid();

    /// <summary>The patient's requests in the list, by id.</summary>
    private static async Task<Dictionary<Guid, JsonElement>> RequestsAsync(ApiClient mrd, string path, Guid patient) =>
        (await mrd.GetAsync<JsonElement>(path)).GetProperty("items").EnumerateArray()
        .Where(r => r.GetProperty("patientId").GetGuid() == patient)
        .ToDictionary(r => r.GetProperty("id").GetGuid());

    /// <summary>Moves the requests back in time, as if asked <paramref name="age"/> ago.</summary>
    private async Task AgeAsync(Guid[] ids, TimeSpan age)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var asked = DateTimeOffset.UtcNow - age;
        await db.Set<RecordRequest>().IgnoreQueryFilters().Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.RequestedAt, asked), Ct);
    }
}
