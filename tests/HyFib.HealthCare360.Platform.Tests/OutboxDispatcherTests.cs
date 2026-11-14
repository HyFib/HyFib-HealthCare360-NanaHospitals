using System.Collections.Concurrent;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The outbox through an internet outage. Alerts that stay inside the hospital never wait behind SMS and WhatsApp; a
/// provider that does not answer cools down alone; a failed message's next try is counted from when it failed; each
/// message's outcome is committed on its own; and held messages wait, while still expiring, until they are released.
/// </summary>
public sealed class OutboxDispatcherTests
{
    private const string Sms = "notify.sms";
    private const string Email = "notify.email";
    private const string FridgeAlert = "supplychain.cold_chain_alert";

    private static readonly DateTimeOffset Start = new(2026, 11, 14, 4, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hanging_SMS_gateway_does_not_hold_up_a_fridge_alert_in_the_app()
    {
        var hanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms)
        {
            Behaviour = async (_, ct) =>
            {
                hanging.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        var fridge = new Handler(FridgeAlert, OutboxLane.Local);
        using var outbox = new TestOutbox(sms, fridge);

        // The SMS fell due first, an hour into the outage: it is taken first, and its gateway never answers.
        outbox.Table.Add(Sms, Start - TimeSpan.FromHours(1));
        var alert = outbox.Table.Add(FridgeAlert, Start);

        await outbox.Dispatcher.StartAsync(Ct);
        try
        {
            await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await fridge.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            outbox.Table.Committed(alert.Id).Status.ShouldBe(OutboxStatus.Succeeded);
        }
        finally
        {
            await outbox.Dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_SMS_gateway_that_cannot_be_reached_cools_down_alone_while_the_e_mails_go()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms)
        {
            Behaviour = (_, _) => throw new ProviderUnreachableException("MSG91 did not respond in time."),
        };
        var email = new Handler(Email, OutboxLane.Internet, OutboxProviders.Email);
        using var outbox = new TestOutbox(sms, email);
        var texts = Enumerable.Range(0, 6).Select(i => outbox.Table.Add(Sms, Start - TimeSpan.FromMinutes(10 - i))).ToList();
        var mails = Enumerable.Range(0, 3).Select(i => outbox.Table.Add(Email, Start - TimeSpan.FromMinutes(10 - i))).ToList();

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        // Three SMS got no answer, and the gateway cools down: the other three were not tried, nor even taken.
        sms.Seen.Count.ShouldBe(OutboxCoolDowns.FailuresBeforeCoolDown);
        texts.Select(t => outbox.Table.Committed(t.Id).Attempts).ShouldBe([1, 1, 1, 0, 0, 0]);
        texts.Skip(3).ShouldAllBe(t => outbox.Table.Committed(t.Id).NextAttemptAt == t.NextAttemptAt);
        outbox.Dispatcher.CoolDowns.CoolingUntil(OutboxProviders.Sms, Start).ShouldBe(Start + OutboxCoolDowns.FirstCoolDown);

        // The e-mails went all the same: their provider answers.
        mails.ShouldAllBe(m => outbox.Table.Committed(m.Id).Status == OutboxStatus.Succeeded);
        outbox.Dispatcher.CoolDowns.CoolingUntil(OutboxProviders.Email, Start).ShouldBeNull();

        // While it cools down, no SMS is tried...
        outbox.Clock.Advance(OutboxCoolDowns.FirstCoolDown - TimeSpan.FromSeconds(1));
        await outbox.Dispatcher.DispatchOnceAsync(Ct);
        sms.Seen.Count.ShouldBe(3);

        // ...after it, one is, and gets no answer again: the next cool-down lasts twice as long.
        outbox.Clock.Advance(TimeSpan.FromSeconds(2));
        await outbox.Dispatcher.DispatchOnceAsync(Ct);
        sms.Seen.Count.ShouldBe(4);
        var now = outbox.Clock.GetUtcNow();
        outbox.Dispatcher.CoolDowns.CoolingUntil(OutboxProviders.Sms, now).ShouldBe(now + (2 * OutboxCoolDowns.FirstCoolDown));

        // Once the gateway answers again, the first try after the cool-down is followed by every SMS waiting.
        sms.Behaviour = null;
        outbox.Clock.Advance(2 * OutboxCoolDowns.FirstCoolDown);
        await outbox.Dispatcher.DispatchOnceAsync(Ct);
        texts.ShouldAllBe(t => outbox.Table.Committed(t.Id).Status == OutboxStatus.Succeeded);
        outbox.Dispatcher.CoolDowns.CoolingUntil(OutboxProviders.Sms, outbox.Clock.GetUtcNow()).ShouldBeNull();
    }

    [Fact]
    public async Task A_provider_that_answers_with_a_refusal_does_not_cool_down()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms)
        {
            Behaviour = (_, _) => throw new InvalidOperationException("MSG91: the DLT template is not approved."),
        };
        using var outbox = new TestOutbox(sms);
        var texts = Enumerable.Range(0, 5).Select(i => outbox.Table.Add(Sms, Start - TimeSpan.FromMinutes(5 - i))).ToList();

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        // Each was tried, and each will be tried again: a refusal is an answer, which says nothing of the link.
        texts.ShouldAllBe(t => outbox.Table.Committed(t.Id).Attempts == 1 && outbox.Table.Committed(t.Id).Status == OutboxStatus.Pending);
        outbox.Dispatcher.CoolDowns.CoolingUntil(OutboxProviders.Sms, Start).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_message_is_tried_again_counted_from_when_it_failed_not_from_when_it_was_taken()
    {
        var clock = new ManualClock(Start);

        // WhatsApp takes 25 s to fail, as a provider behind a link that drops packets does.
        var whatsApp = new Handler("notify.whatsapp", OutboxLane.Internet, OutboxProviders.WhatsApp)
        {
            Behaviour = (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(25));
                throw new InvalidOperationException("WhatsApp error 131026: the message could not be delivered.");
            },
        };
        using var outbox = new TestOutbox(clock, whatsApp);
        var message = outbox.Table.Add(whatsApp.Type, Start);

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        // Ten seconds after the failure (and up to 3 s of jitter); ten seconds after it was taken had passed already.
        var failedAt = Start + TimeSpan.FromSeconds(25);
        var tried = outbox.Table.Committed(message.Id);
        tried.Attempts.ShouldBe(1);
        tried.Status.ShouldBe(OutboxStatus.Pending);
        tried.NextAttemptAt.ShouldBeInRange(failedAt + OutboxRetryPolicy.WaitAfter(1), failedAt + OutboxRetryPolicy.WaitAfter(1) + TimeSpan.FromSeconds(3));
        tried.LastError.ShouldBe("WhatsApp error 131026: the message could not be delivered.");
    }

    [Fact]
    public async Task A_restart_part_way_through_the_messages_sends_again_none_that_went_before_it()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms);
        using var outbox = new TestOutbox(sms);
        var texts = Enumerable.Range(0, 3).Select(i => outbox.Table.Add(Sms, Start - TimeSpan.FromMinutes(3 - i))).ToList();

        // The server stops while the third is being sent.
        sms.Behaviour = async (message, ct) =>
        {
            if (message.Id == texts[2].Id && stopped.TrySetResult())
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        await outbox.Dispatcher.StartAsync(Ct);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await outbox.Dispatcher.StopAsync(CancellationToken.None);

        // Each outcome was committed as the message went: the first two are sent, the third still waits, untried.
        outbox.Table.Committed(texts[0].Id).Status.ShouldBe(OutboxStatus.Succeeded);
        outbox.Table.Committed(texts[1].Id).Status.ShouldBe(OutboxStatus.Succeeded);
        outbox.Table.Committed(texts[2].Id).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(OutboxStatus.Pending),
            m => m.Attempts.ShouldBe(0));

        // After the restart, the third alone is sent.
        using var restarted = outbox.NewDispatcher();
        await restarted.DispatchOnceAsync(Ct);
        sms.Seen.ShouldBe([texts[0].Id, texts[1].Id, texts[2].Id, texts[2].Id]);
        outbox.Table.Committed(texts[2].Id).Status.ShouldBe(OutboxStatus.Succeeded);
    }

    [Fact]
    public async Task A_delivery_whose_changes_cannot_be_saved_is_recorded_on_its_own_and_tried_again()
    {
        var orders = new Handler("lab.analyser.order", OutboxLane.Local);
        using var outbox = new TestOutbox(orders);
        var order = outbox.Table.Add(orders.Type, Start);
        outbox.Table.CommitFailsOnce.Add(order.Id);

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        // What the handler changed was undone and the outcome saved alone: not lost, and not taken as delivered.
        outbox.Table.Undone.ShouldBe([order.Id]);
        var saved = outbox.Table.Committed(order.Id);
        saved.Status.ShouldBe(OutboxStatus.Pending);
        saved.Attempts.ShouldBe(1);
        saved.ProcessedAt.ShouldBeNull();
        saved.LastError.ShouldNotBeNull().ShouldStartWith("It was delivered, but what the delivery changed could not be saved");
        saved.NextAttemptAt.ShouldBeGreaterThan(Start);
    }

    [Fact]
    public async Task After_a_restore_the_messages_queued_before_it_wait_while_new_ones_and_the_local_lane_go_and_releasing_lets_them_go()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms);
        var fridge = new Handler(FridgeAlert, OutboxLane.Local);
        using var outbox = new TestOutbox(sms, fridge);
        var restoredAt = Start - TimeSpan.FromMinutes(10);
        outbox.Settings.Hold = new OutboxHold(OutboxHold.RestoreReason, restoredAt, Start - TimeSpan.FromHours(2));

        // Waiting in the backup: it may have gone already after the backup was made.
        var fromTheBackup = outbox.Table.Add(Sms, Start - TimeSpan.FromHours(3));

        // Queued since the restore, such as the SMS telling IT on-call of it: new, and safe to send.
        var sinceTheRestore = outbox.Table.Add(Sms, Start, queued: restoredAt + TimeSpan.FromSeconds(1));
        var alert = outbox.Table.Add(FridgeAlert, Start);

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        // The alert and the new SMS went; the SMS from the backup was not tried, and waits as it was.
        outbox.Table.Committed(alert.Id).Status.ShouldBe(OutboxStatus.Succeeded);
        sms.Seen.ShouldBe([sinceTheRestore.Id]);
        outbox.Table.Committed(sinceTheRestore.Id).Status.ShouldBe(OutboxStatus.Succeeded);
        outbox.Table.Committed(fromTheBackup.Id).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(OutboxStatus.Pending),
            m => m.Attempts.ShouldBe(0),
            m => m.NextAttemptAt.ShouldBe(fromTheBackup.NextAttemptAt));

        // Released, it goes at the next round.
        outbox.Settings.Hold = null;
        await outbox.Dispatcher.DispatchOnceAsync(Ct);
        sms.Seen.ShouldBe([sinceTheRestore.Id, fromTheBackup.Id]);
        outbox.Table.Committed(fromTheBackup.Id).Status.ShouldBe(OutboxStatus.Succeeded);
    }

    [Fact]
    public async Task A_message_queued_at_the_moment_the_hold_began_is_held()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms);
        using var outbox = new TestOutbox(sms);
        outbox.Settings.Hold = new OutboxHold(OutboxHold.RestoreReason, Start, null);
        var atTheMoment = outbox.Table.Add(Sms, Start);

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        sms.Seen.ShouldBeEmpty();
        outbox.Table.Committed(atTheMoment.Id).Status.ShouldBe(OutboxStatus.Pending);
    }

    [Fact]
    public async Task A_hold_that_cannot_be_read_holds_every_message_on_the_internet_lane()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms);
        var fridge = new Handler(FridgeAlert, OutboxLane.Local);
        using var outbox = new TestOutbox(sms, fridge);
        outbox.Settings.Hold = new OutboxHold("The setting messaging.outbox.hold holds a value HC360 cannot read.", default, null);
        var text = outbox.Table.Add(Sms, Start);
        var alert = outbox.Table.Add(FridgeAlert, Start);

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        // Since when is not known, so even a message queued a moment ago waits; the local lane carries on.
        sms.Seen.ShouldBeEmpty();
        outbox.Table.Committed(text.Id).Status.ShouldBe(OutboxStatus.Pending);
        outbox.Table.Committed(alert.Id).Status.ShouldBe(OutboxStatus.Succeeded);
    }

    [Fact]
    public async Task A_hold_that_comes_on_while_messages_are_going_stops_the_next_one()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms);
        using var outbox = new TestOutbox(sms);
        var first = outbox.Table.Add(Sms, Start - TimeSpan.FromMinutes(2));
        var second = outbox.Table.Add(Sms, Start - TimeSpan.FromMinutes(1));
        sms.Behaviour = (_, _) =>
        {
            outbox.Settings.Hold = new OutboxHold(OutboxHold.RestoreReason, Start, null);
            return Task.CompletedTask;
        };

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        sms.Seen.ShouldBe([first.Id]);
        outbox.Table.Committed(second.Id).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(OutboxStatus.Pending),
            m => m.Attempts.ShouldBe(0));
    }

    [Fact]
    public async Task A_held_message_still_expires_when_its_time_is_up()
    {
        var sms = new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms);
        using var outbox = new TestOutbox(sms);
        outbox.Settings.Hold = new OutboxHold(OutboxHold.RestoreReason, Start, null);

        // A sign-in code's SMS, worth sending for ten minutes.
        var code = outbox.Table.Add(Sms, Start, lifetime: TimeSpan.FromMinutes(10));
        await outbox.Dispatcher.DispatchOnceAsync(Ct);
        outbox.Table.Committed(code.Id).Status.ShouldBe(OutboxStatus.Pending);

        outbox.Clock.Advance(TimeSpan.FromMinutes(11));
        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        sms.Seen.ShouldBeEmpty();
        outbox.Table.Committed(code.Id).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(OutboxStatus.Expired),
            m => m.ProcessedAt.ShouldBe(Start + TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public async Task A_message_no_handler_delivers_fails_at_once_and_holds_up_nothing()
    {
        var fridge = new Handler(FridgeAlert, OutboxLane.Local);
        using var outbox = new TestOutbox(fridge);
        var fax = outbox.Table.Add("notify.fax", Start - TimeSpan.FromMinutes(1));
        var alert = outbox.Table.Add(FridgeAlert, Start);

        await outbox.Dispatcher.DispatchOnceAsync(Ct);

        outbox.Table.Committed(fax.Id).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(OutboxStatus.Failed),
            m => m.LastError.ShouldBe("No handler registered for 'notify.fax'."));
        outbox.Table.Committed(alert.Id).Status.ShouldBe(OutboxStatus.Succeeded);
    }

    [Fact]
    public void A_provider_cools_down_after_three_failures_in_a_row_for_twice_as_long_each_time_up_to_five_minutes()
    {
        var coolDowns = new OutboxCoolDowns();

        coolDowns.Unreachable(OutboxProviders.Sms, Start).ShouldBeNull();
        coolDowns.Unreachable(OutboxProviders.Sms, Start).ShouldBeNull();
        coolDowns.Unreachable(OutboxProviders.Sms, Start).ShouldBe(Start + TimeSpan.FromSeconds(30));
        Enumerable.Range(0, 6).Select(_ => coolDowns.Unreachable(OutboxProviders.Sms, Start)!.Value - Start).ShouldBe(
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)]);
        coolDowns.CoolingUntil(OutboxProviders.WhatsApp, Start).ShouldBeNull();

        // An answer ends the run: three failures again before the next cool-down, which starts again at 30 s.
        coolDowns.Answered(OutboxProviders.Sms);
        coolDowns.CoolingUntil(OutboxProviders.Sms, Start).ShouldBeNull();
        coolDowns.Unreachable(OutboxProviders.Sms, Start).ShouldBeNull();
        coolDowns.Unreachable(OutboxProviders.Sms, Start).ShouldBeNull();
        coolDowns.Unreachable(OutboxProviders.Sms, Start).ShouldBe(Start + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Failures_that_got_no_answer_count_towards_a_cool_down()
    {
        Exception[] noAnswer =
        [
            new ProviderUnreachableException("MSG91 did not respond in time."),
            new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made."),
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
            new TaskCanceledException("The request was canceled.", new TimeoutException()),
            new InvalidOperationException("The Tally post failed.", new System.Net.Sockets.SocketException(10060)),
            new TimeoutException("The Tally post of 2026-11-13 did not finish within 30 s."),
        ];

        noAnswer.ShouldAllBe(failure => OutboxFailures.IsUnreachable(failure));
    }

    [Fact]
    public void Answers_and_failures_of_the_message_s_own_do_not_count()
    {
        Exception[] answered =
        [
            new InvalidOperationException("MSG91: the DLT template is not approved."),
            new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden),
            new HttpRequestException("The data-push address 10.0.0.5 does not lead outside the hospital's network."),
            new InvalidOperationException("No SMS provider is configured. Set one up in Administration → Integrations."),
        ];

        answered.ShouldAllBe(failure => !OutboxFailures.IsUnreachable(failure));
    }

    [Fact]
    public void The_handlers_of_one_provider_go_together_and_a_type_no_handler_delivers_goes_alone_on_the_internet_lane()
    {
        var handlers = new Dictionary<string, OutboxHandlerInfo>(StringComparer.Ordinal)
        {
            ["billing.tally.post"] = new("billing.tally.post", OutboxLane.Local, "tally"),
            ["billing.tally.check"] = new("billing.tally.check", OutboxLane.Local, "tally"),
            ["notify.whatsapp"] = new("notify.whatsapp", OutboxLane.Internet, OutboxProviders.WhatsApp),
            ["whatsapp.reply_confirmation"] = new("whatsapp.reply_confirmation", OutboxLane.Internet, OutboxProviders.WhatsApp),
            ["abdm.link_record"] = new("abdm.link_record", OutboxLane.Local, "abdm"),
            ["abdm.transfer"] = new("abdm.transfer", OutboxLane.Internet, "abdm"),
        };

        var providers = OutboxProvider.Of(["billing.tally.check", "whatsapp.reply_confirmation", "abdm.link_record", "notify.fax"], handlers)
            .ToDictionary(p => p.Name);

        providers.Keys.ShouldBe(["tally", OutboxProviders.WhatsApp, "abdm", "notify.fax"], ignoreOrder: true);
        providers["tally"].Lane.ShouldBe(OutboxLane.Local);
        providers["tally"].Types.ShouldBe(["billing.tally.post", "billing.tally.check"], ignoreOrder: true);
        providers[OutboxProviders.WhatsApp].Lane.ShouldBe(OutboxLane.Internet);
        providers[OutboxProviders.WhatsApp].Types.ShouldBe(["notify.whatsapp", "whatsapp.reply_confirmation"], ignoreOrder: true);

        // A provider is local only when every handler of it is.
        providers["abdm"].Lane.ShouldBe(OutboxLane.Internet);
        providers["notify.fax"].Lane.ShouldBe(OutboxLane.Internet);
        providers["notify.fax"].Types.ShouldBe(["notify.fax"]);
    }

    [Fact]
    public void The_local_types_are_those_of_providers_whose_every_handler_is_local()
    {
        IOutboxHandler[] handlers =
        [
            new Handler(FridgeAlert, OutboxLane.Local),
            new Handler("billing.tally.post", OutboxLane.Local, "tally"),
            new Handler("billing.tally.check", OutboxLane.Local, "tally"),
            new Handler(Sms, OutboxLane.Internet, OutboxProviders.Sms),
            new Handler("abdm.link_record", OutboxLane.Local, "abdm"),
            new Handler("abdm.transfer", OutboxLane.Internet, "abdm"),
            new UndeclaredHandler(),
        ];

        OutboxProvider.LocalTypes(handlers).ShouldBe([FridgeAlert, "billing.tally.post", "billing.tally.check"], ignoreOrder: true);
    }

    [Fact]
    public void A_handler_that_declares_nothing_goes_to_the_internet_as_a_provider_of_its_own()
    {
        IOutboxHandler handler = new UndeclaredHandler();

        handler.Lane.ShouldBe(OutboxLane.Internet);
        handler.Provider.ShouldBe("billing.einvoice.generate");
    }

    /// <summary>The outbox table in memory, as committed: what a unit changes counts only once it commits.</summary>
    private sealed class MemoryOutbox(IServiceProvider services) : IOutboxStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, OutboxMessage> _rows = [];
        private readonly HashSet<Guid> _taken = [];

        /// <summary>Messages whose next commit fails, as when a handler leaves changes that cannot be saved.</summary>
        public HashSet<Guid> CommitFailsOnce { get; } = [];

        /// <summary>The messages whose handler's changes were undone, in order.</summary>
        public List<Guid> Undone { get; } = [];

        /// <param name="queued">When it was queued: when it fell due, unless said otherwise.</param>
        public OutboxMessage Add(string type, DateTimeOffset due, TimeSpan? lifetime = null, DateTimeOffset? queued = null)
        {
            var message = new OutboxMessage
            {
                Type = type,
                CreatedAt = queued ?? due,
                NextAttemptAt = due,
                ExpiresAt = due + (lifetime ?? OutboxRetryPolicy.DefaultLifetime),
                Status = OutboxStatus.Pending,
            };
            lock (_gate)
            {
                _rows[message.Id] = Copy(message);
            }

            return message;
        }

        public OutboxMessage Committed(Guid id)
        {
            lock (_gate)
            {
                return Copy(_rows[id]);
            }
        }

        public Task<IReadOnlyCollection<string>> DueTypesAsync(DateTimeOffset now, DateTimeOffset? queuedAfter, CancellationToken ct)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyCollection<string>>(
                    [.. _rows.Values.Where(m => Due(m, now, queuedAfter)).Select(m => m.Type).Distinct()]);
            }
        }

        public Task<IOutboxUnit?> TakeAsync(IReadOnlyCollection<string> types, DateTimeOffset now, DateTimeOffset? queuedAfter, CancellationToken ct)
        {
            lock (_gate)
            {
                var next = _rows.Values
                    .Where(m => Due(m, now, queuedAfter) && types.Contains(m.Type) && !_taken.Contains(m.Id))
                    .OrderBy(m => m.NextAttemptAt)
                    .FirstOrDefault();
                if (next is null)
                {
                    return Task.FromResult<IOutboxUnit?>(null);
                }

                _taken.Add(next.Id);
                return Task.FromResult<IOutboxUnit?>(new Unit(this, Copy(next), services.CreateAsyncScope()));
            }
        }

        public Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
        {
            lock (_gate)
            {
                var ended = _rows.Values.Where(m => m.Status == OutboxStatus.Pending && !_taken.Contains(m.Id) && OutboxRetryPolicy.ExpiryOf(m) <= now).ToList();
                foreach (var message in ended)
                {
                    message.Status = message.Attempts == 0 ? OutboxStatus.Expired : OutboxStatus.Failed;
                    message.ProcessedAt = now;
                }

                return Task.FromResult(ended.Count);
            }
        }

        private static bool Due(OutboxMessage m, DateTimeOffset now, DateTimeOffset? queuedAfter) =>
            m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now && m.CreatedAt > (queuedAfter ?? DateTimeOffset.MinValue);

        private static OutboxMessage Copy(OutboxMessage m) => new()
        {
            Id = m.Id,
            Type = m.Type,
            Payload = m.Payload,
            Status = m.Status,
            Attempts = m.Attempts,
            CreatedAt = m.CreatedAt,
            NextAttemptAt = m.NextAttemptAt,
            ProcessedAt = m.ProcessedAt,
            ExpiresAt = m.ExpiresAt,
            LastError = m.LastError,
            FacilityId = m.FacilityId,
            CorrelationId = m.CorrelationId,
        };

        private void Commit(OutboxMessage message)
        {
            lock (_gate)
            {
                if (CommitFailsOnce.Remove(message.Id))
                {
                    throw new InvalidOperationException("23505: duplicate key value violates unique constraint \"ix_lab_order_downloads_source\"");
                }

                _rows[message.Id] = Copy(message);
            }
        }

        private void Release(Guid id)
        {
            lock (_gate)
            {
                _taken.Remove(id);
            }
        }

        private sealed class Unit(MemoryOutbox table, OutboxMessage message, AsyncServiceScope scope) : IOutboxUnit
        {
            public OutboxMessage Message => message;

            public IServiceProvider Services => scope.ServiceProvider;

            public Task CommitAsync(CancellationToken ct)
            {
                table.Commit(message);
                return Task.CompletedTask;
            }

            public Task UndoHandlerAsync(CancellationToken ct)
            {
                lock (table._gate)
                {
                    table.Undone.Add(message.Id);
                }

                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                table.Release(message.Id);
                await scope.DisposeAsync();
            }
        }
    }

    /// <summary>A handler that delivers, unless told otherwise, and keeps the messages it was handed.</summary>
    private sealed class Handler(string type, OutboxLane lane = OutboxLane.Internet, string? provider = null) : IOutboxHandler
    {
        private readonly ConcurrentQueue<Guid> _seen = new();

        public string Type => type;

        public OutboxLane Lane => lane;

        public string Provider => provider ?? type;

        /// <summary>What it does with a message: null to deliver it at once.</summary>
        public Func<OutboxMessage, CancellationToken, Task>? Behaviour { get; set; }

        public IReadOnlyList<Guid> Seen => [.. _seen];

        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            _seen.Enqueue(message.Id);
            if (Behaviour is { } behaviour)
            {
                await behaviour(message, cancellationToken);
            }

            Delivered.TrySetResult();
        }
    }

    /// <summary>A handler that says nothing of its lane or provider, as the e-invoice one.</summary>
    private sealed class UndeclaredHandler : IOutboxHandler
    {
        public string Type => "billing.einvoice.generate";

        public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>The hold setting, as the settings service reads it.</summary>
    private sealed class HoldSetting : ISettingsService
    {
        private OutboxHold? _hold;

        public OutboxHold? Hold
        {
            get => Volatile.Read(ref _hold);
            set => Volatile.Write(ref _hold, value);
        }

        public Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == OutboxHold.SettingKey && facilityId is null && Hold is T hold ? hold : default);

        public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A dispatcher over an outbox in memory, with these handlers and the hold setting, on a clock the test moves.</summary>
    private sealed class TestOutbox : IDisposable
    {
        private readonly ServiceProvider _services;

        public TestOutbox(params Handler[] handlers)
            : this(new ManualClock(Start), handlers)
        {
        }

        public TestOutbox(ManualClock clock, params Handler[] handlers)
        {
            Clock = clock;
            var services = new ServiceCollection();
            foreach (var handler in handlers)
            {
                services.AddSingleton<IOutboxHandler>(handler);
            }

            services.AddSingleton<ISettingsService>(Settings);
            _services = services.BuildServiceProvider();
            Table = new MemoryOutbox(_services);
            Dispatcher = NewDispatcher();
        }

        public ManualClock Clock { get; }

        public HoldSetting Settings { get; } = new();

        public MemoryOutbox Table { get; }

        public OutboxDispatcher Dispatcher { get; }

        /// <summary>Another dispatcher on the same table, as after a restart.</summary>
        public OutboxDispatcher NewDispatcher() =>
            new(_services.GetRequiredService<IServiceScopeFactory>(), Table, Clock, Options.Create(new PlatformOptions()), NullLogger<OutboxDispatcher>.Instance);

        public void Dispose()
        {
            Dispatcher.Dispose();
            _services.Dispose();
        }
    }
}
