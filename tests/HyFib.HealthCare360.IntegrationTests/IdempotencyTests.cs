using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// DED-08: a state-changing request retried with the same <c>Idempotency-Key</c>, as the apps retry after a response
/// lost on the WiFi, gets the first answer back and changes nothing again. Patient registration stands for every unsafe
/// endpoint: the store sits in front of them all (<see cref="IdempotencyMiddleware"/>). The outcome is recorded before
/// the answer is sent, so a client that has gone gets it on its retry; a first attempt that never finished (the server
/// stopped between the save and the record) is reported as possibly saved, never run again.
/// </summary>
public sealed class IdempotencyTests(HospitalServer server)
{
    private const string Path = "/api/v1/patients";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retried_registration_returns_the_first_answer_and_registers_no_one_again()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Retry" + Letters(8));

        using var first = await SendAsync(frontOffice, request, key);
        using var retry = await SendAsync(frontOffice, request, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(first.StatusCode);
        retry.Headers.Location.ShouldBe(first.Headers.Location);
        var created = await first.Content.ReadFromJsonAsync<PatientCreated>(ApiClient.Json, Ct);
        (await retry.Content.ReadFromJsonAsync<PatientCreated>(ApiClient.Json, Ct)).ShouldBe(created);
        (await CountAsync(request.LastName)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_key_with_another_request_is_refused_and_changes_nothing()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Reuse" + Letters(8));
        var other = Registration("Other" + Letters(8));

        using var first = await SendAsync(frontOffice, request, key);
        using var reused = await SendAsync(frontOffice, other, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(other.LastName)).ShouldBe(0);
    }

    [Fact]
    public async Task A_registration_whose_answer_was_lost_is_answered_on_its_retry_and_made_once()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Lost" + Letters(8));
        var body = JsonSerializer.SerializeToUtf8Bytes(request, ApiClient.Json);

        // The patient is registered, and the WiFi drops as the answer is sent: the screen never hears so.
        var lost = await SendWithLostAnswerAsync(frontOffice, body, key);
        using var retry = await SendAsync(frontOffice, body, key);

        lost.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await retry.Content.ReadFromJsonAsync<PatientCreated>(ApiClient.Json, Ct)).ShouldNotBeNull();
        (await CountAsync(request.LastName)).ShouldBe(1, "The retry must not register the patient a second time.");
        (await RecordAsync(key)).State.ShouldBe(IdempotencyState.Completed);
    }

    [Fact]
    public async Task A_registration_that_did_not_finish_is_reported_as_possibly_made_and_never_made_again()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Halted" + Letters(8));
        using (var first = await SendAsync(frontOffice, request, key))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // The server stopped after the patient was saved and before the outcome was recorded, three minutes ago.
        await MarkInProgressAsync(key, begunAgo: TimeSpan.FromMinutes(3));

        using var retry = await SendAsync(frontOffice, request, key);
        using var later = await SendAsync(frontOffice, request, key);

        foreach (var answer in new[] { retry, later })
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOfAsync(answer)).ShouldBe(IdempotencyMiddleware.OutcomeUnknownCode);
        }

        (await CountAsync(request.LastName)).ShouldBe(1, "Whether the first attempt saved is not known, so it is not run again.");
        (await RecordAsync(key)).State.ShouldBe(IdempotencyState.Abandoned);
    }

    [Fact]
    public async Task A_retry_while_the_registration_still_runs_is_asked_to_wait()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Waiting" + Letters(8));
        using (var first = await SendAsync(frontOffice, request, key))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // As though the first attempt had only just begun.
        await MarkInProgressAsync(key, begunAgo: TimeSpan.Zero);

        using var retry = await SendAsync(frontOffice, request, key);

        retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        retry.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(2));
        (await CodeOfAsync(retry)).ShouldBe(IdempotencyMiddleware.InProgressCode);
        (await CountAsync(request.LastName)).ShouldBe(1);
    }

    private static async Task<HttpResponseMessage> SendAsync(ApiClient client, RegisterPatientRequest request, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(request, options: ApiClient.Json) };
        message.Headers.Add(IdempotencyMiddleware.HeaderName, key);
        return await client.Http.SendAsync(message, Ct);
    }

    /// <summary>The registration exactly as <paramref name="body"/> has it, so a retry carries the same bytes.</summary>
    private static async Task<HttpResponseMessage> SendAsync(ApiClient client, byte[] body, string key)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, Path) { Content = content };
        message.Headers.Add(IdempotencyMiddleware.HeaderName, key);
        return await client.Http.SendAsync(message, Ct);
    }

    /// <summary>
    /// Sends the registration as <paramref name="client"/> does, over a connection that drops as the answer arrives: the
    /// server's write to it fails, as Kestrel's does once the client has gone.
    /// </summary>
    private async Task<HttpContext> SendWithLostAnswerAsync(ApiClient client, byte[] body, string key)
    {
        var testServer = (TestServer)server.Services.GetRequiredService<IServer>();
        var connection = new CancellationTokenSource();
        return await testServer.SendAsync(
            context =>
            {
                context.Request.Method = HttpMethods.Post;
                context.Request.Path = Path;
                context.Request.Headers.Authorization = client.Http.DefaultRequestHeaders.Authorization?.ToString();
                context.Request.Headers["X-HC360-Client"] = "web";
                context.Request.Headers[IdempotencyMiddleware.HeaderName] = key;
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = body.Length;
                context.Request.Body = new MemoryStream(body);
                context.Features.Set<IHttpRequestLifetimeFeature>(new DroppingConnection(connection));
                context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new AnswerLostOnTheWay(connection)));
            },
            Ct);
    }

    /// <summary>Puts the key's record back to "in progress", as begun <paramref name="begunAgo"/> by the server's clock.</summary>
    private async Task MarkInProgressAsync(string key, TimeSpan begunAgo)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var begun = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow() - begunAgo;
        var updated = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IdempotencyRecord>()
            .Where(r => r.Key == key)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.State, IdempotencyState.InProgress)
                    .SetProperty(r => r.StatusCode, (int?)null)
                    .SetProperty(r => r.ResponseBody, (byte[]?)null)
                    .SetProperty(r => r.CreatedAt, begun),
                Ct);
        updated.ShouldBe(1);
    }

    private async Task<IdempotencyRecord> RecordAsync(string key)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IdempotencyRecord>().AsNoTracking()
            .SingleAsync(r => r.Key == key, Ct);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<int> CountAsync(string? lastName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>()
            .IgnoreQueryFilters()
            .CountAsync(p => p.LastName == lastName, Ct);
    }

    // A name, number and birth date of its own: another test's patient with the same ones would be taken as a duplicate.
    private static RegisterPatientRequest Registration(string lastName) => new(
        Title: "Mr.", FirstName: "Ravi", MiddleName: null, LastName: lastName, Gender: Gender.Male,
        DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 40)), AgeYears: null, AgeMonths: null,
        Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture), AlternatePhone: null, Email: null,
        Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
        EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
        PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
        Notes: null, ConfirmedNotDuplicate: true);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    /// <summary>The request's connection to the client, which drops (<see cref="Abort"/>) as the answer arrives.</summary>
    private sealed class DroppingConnection(CancellationTokenSource connection) : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = connection.Token;

        public void Abort() => connection.Cancel();
    }

    /// <summary>The client's end of the connection: the answer never arrives, and writing it fails once the connection drops.</summary>
    private sealed class AnswerLostOnTheWay(CancellationTokenSource connection) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw Dropped();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(Dropped());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(Dropped());

        private OperationCanceledException Dropped()
        {
            connection.Cancel();
            return new OperationCanceledException("The client's connection dropped.", connection.Token);
        }
    }
}
