using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// What the automatic link does when the ABDM gateway cannot be reached (the hospital's internet is down): the record is
/// tried again with doubling waits, and only when the tries are used up are the people who handle ABDM told, once.
/// </summary>
public sealed class AbdmAutoLinkRetryTests
{
    private static readonly Guid Record = Guid.Parse("01933a4b-0000-7000-8000-0000000000a1");
    private static readonly Guid Patient = Guid.Parse("01933a4b-0000-7000-8000-0000000000b2");
    private static readonly Guid Facility = Guid.Parse("01933a4b-0000-7000-8000-0000000000c3");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly IOutbox _outbox = Substitute.For<IOutbox>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly Linker _linker = new();
    private readonly AbdmAutoLinkHandler _handler;

    public AbdmAutoLinkRetryTests()
    {
        var features = Substitute.For<IFeatureFlags>();
        features.IsEnabledAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        var services = new ServiceCollection()
            .AddSingleton<IAbdmAutoLinker>(_linker)
            .AddSingleton(_notifications)
            .BuildServiceProvider();
        _handler = new AbdmAutoLinkHandler(
            services.GetRequiredService<IServiceScopeFactory>(), _outbox, features, new Clock(Now), NullLogger<AbdmAutoLinkHandler>.Instance);
    }

    private static OutboxMessage Queued(AbdmAutoLinkJob job) => new()
    {
        Type = AbdmAutoLinking.MessageType,
        Payload = System.Text.Json.JsonSerializer.Serialize(job, System.Text.Json.JsonSerializerOptions.Web),
        CreatedAt = Now,
        NextAttemptAt = Now,
        ExpiresAt = Now + AbdmAutoLinking.Lifetime,
        FacilityId = Facility,
    };

    private static AbdmAutoLinkJob Job(int unreachable = 0) => new(AbdmRecordType.DischargeSummary, Record, Patient, Unreachable: unreachable);

    private void GatewayAnswers(params AbdmAutoLinkResult[] results) => _linker.Answers = new Queue<AbdmAutoLinkResult>(results);

    private Task AlertsSentAsync(int count) =>
        _notifications.Received(count).NotifyPermissionHoldersAsync(
            InteropPermissions.AbdmExport, Facility, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), "abdm", "warning", Arg.Any<CancellationToken>());

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_is_tried_again_later_without_telling_anyone()
    {
        GatewayAnswers(AbdmAutoLinkResult.GatewayUnreachable("The ABDM gateway could not be reached."));

        await _handler.HandleAsync(Queued(Job()), CancellationToken.None);

        _outbox.Received(1).Enqueue(
            AbdmAutoLinking.MessageType, Arg.Is<AbdmAutoLinkJob>(j => j.Unreachable == 1 && j.RecordId == Record), Facility,
            AbdmAutoLinking.FirstRetry, Arg.Any<TimeSpan?>());
        await AlertsSentAsync(0);
    }

    [Fact]
    public async Task A_transient_failure_followed_by_a_success_links_the_record_and_alerts_no_one()
    {
        GatewayAnswers(AbdmAutoLinkResult.GatewayUnreachable("down"), new AbdmAutoLinkResult(AbdmAutoLinkOutcome.Requested, "Request linked."));

        await _handler.HandleAsync(Queued(Job()), CancellationToken.None);
        await _handler.HandleAsync(Queued(Job(unreachable: 1)), CancellationToken.None);

        // Only the first try queued another; the second one ended the job.
        _outbox.ReceivedWithAnyArgs(1).Enqueue<AbdmAutoLinkJob>(default!, default!);
        await AlertsSentAsync(0);
    }

    [Fact]
    public async Task Each_retry_waits_twice_as_long_as_the_one_before_up_to_two_hours()
    {
        GatewayAnswers([.. Enumerable.Repeat(AbdmAutoLinkResult.GatewayUnreachable("down"), AbdmAutoLinking.MaxTries)]);

        for (var failed = 0; failed < AbdmAutoLinking.MaxTries - 1; failed++)
        {
            await _handler.HandleAsync(Queued(Job(failed)), CancellationToken.None);
        }

        var waits = _outbox.ReceivedCalls().Select(c => (TimeSpan)c.GetArguments()[3]!).ToList();
        waits.ShouldBe([
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16),
            TimeSpan.FromMinutes(32), TimeSpan.FromMinutes(64), TimeSpan.FromHours(2)]);
    }

    [Fact]
    public async Task When_the_last_try_fails_the_holders_of_the_abdm_permission_are_told_once_and_nothing_is_queued()
    {
        GatewayAnswers(AbdmAutoLinkResult.GatewayUnreachable("down"));

        await _handler.HandleAsync(Queued(Job(AbdmAutoLinking.MaxTries - 1)), CancellationToken.None);

        _outbox.ReceivedWithAnyArgs(0).Enqueue<AbdmAutoLinkJob>(default!, default!);
        await AlertsSentAsync(1);
        await _notifications.Received(1).NotifyPermissionHoldersAsync(
            InteropPermissions.AbdmExport, Facility, Arg.Any<string>(),
            Arg.Is<string?>(body => body!.Contains("discharge summary") && !body.Contains(Patient.ToString())),
            $"/patients/{Patient:D}", "abdm", "warning", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_job_whose_lifetime_ends_before_its_next_retry_is_alerted_instead_of_queued()
    {
        GatewayAnswers(AbdmAutoLinkResult.GatewayUnreachable("down"));
        var message = Queued(Job(2));
        message.ExpiresAt = Now + TimeSpan.FromMinutes(5);

        await _handler.HandleAsync(message, CancellationToken.None);

        _outbox.ReceivedWithAnyArgs(0).Enqueue<AbdmAutoLinkJob>(default!, default!);
        await AlertsSentAsync(1);
    }

    [Fact]
    public async Task A_refusal_by_the_gateway_is_not_retried_or_alerted_as_the_request_shows_it_to_staff()
    {
        GatewayAnswers(new AbdmAutoLinkResult(AbdmAutoLinkOutcome.Requested, "Request: Failed."));

        await _handler.HandleAsync(Queued(Job()), CancellationToken.None);

        _outbox.ReceivedWithAnyArgs(0).Enqueue<AbdmAutoLinkJob>(default!, default!);
        await AlertsSentAsync(0);
    }

    /// <summary>The linking service as the handler sees it: answers as told, one per call, in order.</summary>
    private sealed class Linker : IAbdmAutoLinker
    {
        public Queue<AbdmAutoLinkResult> Answers { get; set; } = new();

        public Task<AbdmAutoLinkResult> LinkAutomaticallyAsync(AbdmRecordType type, Guid id, CancellationToken ct) => Task.FromResult(Answers.Dequeue());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
