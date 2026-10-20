using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The portal's proof of work (SRS SEC-06): a challenge is signed, good for one kind of request, for a few minutes and for
/// one request only, and the work it asks for goes up for an address or a number that keeps failing. Several servers
/// share what it remembers through one store (SCAL-01); the database's is tested in ScaleOutTests, and here the single
/// server's memory stands in for it.
/// </summary>
public sealed class ProofOfWorkTests
{
    private const int Bits = 8;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Clock _time = new(new DateTimeOffset(2026, 10, 16, 6, 0, 0, TimeSpan.Zero));
    private readonly RecordingStore _store;

    public ProofOfWorkTests() => _store = new RecordingStore(new MemoryClusterStore(new MemoryCache(new MemoryCacheOptions()), _time));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProofOfWork Pow => new(_key, _store, _time);

    /// <summary>The failure counts of a server whose memory is <paramref name="cache"/> (this one's by default), on the shared store.</summary>
    private AutomationSuspicion Suspicion(IMemoryCache? cache = null, byte[]? key = null) =>
        new(_store, cache ?? _cache, _time, key ?? _key, NullLogger<AutomationSuspicion>.Instance);

    private string NewChallenge(string action = AntiAutomationActions.Otp, int bits = Bits) => Pow.Issue(action, bits, _time.GetUtcNow().AddMinutes(5));

    [Fact]
    public async Task A_solved_challenge_is_accepted()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Accepted);
    }

    [Fact]
    public void The_hash_of_an_accepted_proof_starts_with_the_bits_asked_for()
    {
        var proof = ProofOfWork.Solve(NewChallenge(bits: 12), 12);
        var colon = proof.LastIndexOf(':');

        ProofOfWork.LeadingZeroBits(SHA256.HashData(Encoding.UTF8.GetBytes(proof[..colon] + proof[(colon + 1)..]))).ShouldBeGreaterThanOrEqualTo(12);
    }

    [Theory]
    [InlineData(new byte[] { 0xff }, 0)]
    [InlineData(new byte[] { 0x80 }, 0)]
    [InlineData(new byte[] { 0x7f }, 1)]
    [InlineData(new byte[] { 0x01 }, 7)]
    [InlineData(new byte[] { 0x00, 0x10 }, 11)]
    [InlineData(new byte[] { 0x00, 0x00 }, 16)]
    public void Leading_zero_bits_are_counted_from_the_first_byte(byte[] hash, int expected) =>
        ProofOfWork.LeadingZeroBits(hash).ShouldBe(expected);

    [Fact]
    public async Task A_proof_is_good_for_one_request()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Accepted);
        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Replayed);
    }

    [Fact]
    public async Task A_challenge_spent_on_one_server_is_refused_on_another()
    {
        // Two servers of several: the same key from the shared folder, the same store.
        var first = new ProofOfWork(_key, _store, _time);
        var second = new ProofOfWork(_key, _store, _time);
        var proof = ProofOfWork.Solve(first.Issue(AntiAutomationActions.Otp, Bits, _time.GetUtcNow().AddMinutes(5)), Bits);

        (await second.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Accepted);
        (await first.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Replayed);
    }

    [Fact]
    public async Task A_spent_challenge_is_remembered_until_a_minute_past_its_expiry()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Accepted);

        var spent = _store.Written.ShouldHaveSingleItem();
        spent.Key.ShouldStartWith(ProofOfWork.SpentPrefix);
        spent.Lifetime.ShouldBe(TimeSpan.FromMinutes(6), "five minutes left on the challenge, and one");
    }

    [Fact]
    public async Task Another_nonce_for_a_spent_challenge_is_refused_too()
    {
        var challenge = NewChallenge(bits: 0);

        (await Pow.VerifyAsync(challenge + ":1", AntiAutomationActions.Otp, 0, Ct)).ShouldBe(ProofOfWorkOutcome.Accepted);
        (await Pow.VerifyAsync(challenge + ":2", AntiAutomationActions.Otp, 0, Ct)).ShouldBe(ProofOfWorkOutcome.Replayed);
    }

    [Fact]
    public async Task An_expired_challenge_is_refused()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);
        _time.Advance(TimeSpan.FromMinutes(5));

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Expired);
    }

    [Fact]
    public async Task A_challenge_with_easier_bits_written_in_is_refused_as_tampered()
    {
        var challenge = NewChallenge(bits: 20);
        var parts = challenge.Split('.');
        parts[2] = "0";
        var forged = string.Join('.', parts);

        (await Pow.VerifyAsync(forged + ":0", AntiAutomationActions.Otp, 0, Ct)).ShouldBe(ProofOfWorkOutcome.Tampered);
    }

    [Fact]
    public async Task A_challenge_signed_by_another_server_is_refused_as_tampered()
    {
        var other = new ProofOfWork(RandomNumberGenerator.GetBytes(32), _store, _time);
        var proof = ProofOfWork.Solve(other.Issue(AntiAutomationActions.Otp, Bits, _time.GetUtcNow().AddMinutes(5)), Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Tampered);
    }

    [Fact]
    public async Task A_challenge_for_another_kind_of_request_is_refused()
    {
        var proof = ProofOfWork.Solve(NewChallenge(AntiAutomationActions.Feedback), Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.WrongAction);
    }

    [Fact]
    public async Task A_challenge_easier_than_the_request_now_needs_is_refused()
    {
        var proof = ProofOfWork.Solve(NewChallenge(bits: Bits), Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits + 2, Ct)).ShouldBe(ProofOfWorkOutcome.TooEasy);
    }

    [Fact]
    public async Task An_unsolved_challenge_easier_than_needed_is_refused_as_unsolved_not_too_easy()
    {
        var challenge = NewChallenge(bits: 24);

        // Too easy is kept for a browser that did the work it was asked for, which AntiAutomation does not hold against it.
        var outcomes = await OutcomesAsync(challenge, requiredBits: 26);

        outcomes.ShouldContain(ProofOfWorkOutcome.Unsolved);
        outcomes.ShouldNotContain(ProofOfWorkOutcome.TooEasy);
    }

    [Fact]
    public async Task A_nonce_that_does_not_solve_the_challenge_is_refused()
    {
        var challenge = NewChallenge(bits: 24);

        // One nonce in 2^24 solves it; the first few are as good as certain not to.
        var outcomes = await OutcomesAsync(challenge, requiredBits: 24);

        outcomes.ShouldContain(ProofOfWorkOutcome.Unsolved);
        outcomes.ShouldNotContain(ProofOfWorkOutcome.Replayed);
    }

    [Theory]
    [InlineData(null, ProofOfWorkOutcome.Missing)]
    [InlineData("", ProofOfWorkOutcome.Missing)]
    [InlineData("no-colon", ProofOfWorkOutcome.Malformed)]
    [InlineData("1.otp.8.99999999999.00.sig:12", ProofOfWorkOutcome.Tampered)]
    [InlineData("1.otp.8.99999999999.00:12", ProofOfWorkOutcome.Malformed)]
    [InlineData("2.otp.8.99999999999.00.sig:12", ProofOfWorkOutcome.Malformed)]
    [InlineData("1.otp.x.99999999999.00.sig:12", ProofOfWorkOutcome.Malformed)]
    [InlineData("1.otp.8.99999999999.00.sig:-1", ProofOfWorkOutcome.Malformed)]
    [InlineData("1.otp.8.99999999999.00.sig:12345678901234567", ProofOfWorkOutcome.Malformed)]
    public async Task A_malformed_proof_is_refused(string? proof, ProofOfWorkOutcome expected) =>
        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(expected);

    [Fact]
    public async Task An_unsolved_or_refused_proof_does_not_use_the_challenge_up()
    {
        var challenge = NewChallenge();
        var proof = ProofOfWork.Solve(challenge, Bits);

        (await Pow.VerifyAsync(proof, AntiAutomationActions.Feedback, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.WrongAction);
        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits + 1, Ct)).ShouldBe(ProofOfWorkOutcome.TooEasy);
        _store.Written.ShouldBeEmpty("only a proof good in every other way is spent");
        (await Pow.VerifyAsync(proof, AntiAutomationActions.Otp, Bits, Ct)).ShouldBe(ProofOfWorkOutcome.Accepted);
    }

    [Fact]
    public async Task Every_three_failures_ask_for_two_more_bits_up_to_the_most()
    {
        var suspicion = Suspicion();

        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.1", null, Ct)).ShouldBe(14);
        await RecordFailuresAsync(suspicion, 2, "10.0.0.1", null);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.1", null, Ct)).ShouldBe(14);
        await RecordFailuresAsync(suspicion, 1, "10.0.0.1", null);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.1", null, Ct)).ShouldBe(16);
        await RecordFailuresAsync(suspicion, 30, "10.0.0.1", null);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.1", null, Ct)).ShouldBe(18);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.2", null, Ct)).ShouldBe(14);
    }

    [Fact]
    public async Task Failures_for_a_number_raise_the_work_for_it_from_any_address()
    {
        var suspicion = Suspicion();
        await RecordFailuresAsync(suspicion, 3, "10.0.0.1", "98765 43210");

        // The number is matched however it is written.
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.9", "+919876543210", Ct)).ShouldBe(16);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.9", "9123456789", Ct)).ShouldBe(14);
    }

    [Fact]
    public async Task Failures_counted_by_one_server_raise_the_work_another_asks_for()
    {
        // Two servers of several: a memory each, one store.
        var first = Suspicion();
        var second = Suspicion(new MemoryCache(new MemoryCacheOptions()));

        await RecordFailuresAsync(first, 2, "10.0.0.20", "9876500001");
        await RecordFailuresAsync(second, 1, "10.0.0.20", "9876500001");

        (await first.RequiredBitsAsync(14, 18, "10.0.0.20", null, Ct)).ShouldBe(16);
        (await second.RequiredBitsAsync(14, 18, "10.0.0.21", "9876500001", Ct)).ShouldBe(16);
    }

    [Fact]
    public async Task Numbers_and_addresses_are_kept_only_as_hashes_keyed_by_the_server()
    {
        var suspicion = Suspicion();
        var number = SharedKernel.Phone.Normalize("9876543210")!;
        const string Address = "203.0.113.45";

        await RecordFailuresAsync(suspicion, 1, Address, "98765 43210");

        // Every IPv4 address, and every one of India's mobile numbers, can be hashed in minutes: a plain hash would give
        // them back.
        var keys = _store.Written.Select(w => w.Key).ToList();
        keys.ShouldBe(new[] { "pow:fail:ip:" + suspicion.AddressHash(Address), "pow:fail:subject:" + suspicion.SubjectHash(number) });
        foreach (var plain in new[] { number, "9876543210", Address })
        {
            keys.ShouldAllBe(k => !k.Contains(plain) && !k.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain)), 0, 16)));
        }

        suspicion.SubjectHash("+91 98765 43210").ShouldBe(suspicion.SubjectHash(number), "the number however it is written");
        var otherKey = Suspicion(key: RandomNumberGenerator.GetBytes(32));
        otherKey.SubjectHash(number).ShouldNotBe(suspicion.SubjectHash(number), "another key, another hash");
        otherKey.AddressHash(Address).ShouldNotBe(suspicion.AddressHash(Address), "another key, another hash");
    }

    [Fact]
    public async Task The_count_falls_back_only_after_a_quiet_quarter_of_an_hour()
    {
        var suspicion = Suspicion();
        await RecordFailuresAsync(suspicion, 2, "10.0.0.3", null);
        _time.Advance(TimeSpan.FromMinutes(10));
        await RecordFailuresAsync(suspicion, 1, "10.0.0.3", null);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.3", null, Ct)).ShouldBe(16);

        // 20 minutes after the first failure, 10 after the last: still counted.
        _time.Advance(TimeSpan.FromMinutes(10));
        (await suspicion.FailuresAsync("10.0.0.3", null, Ct)).ShouldBe(3);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.3", null, Ct)).ShouldBe(16);

        // A failure now starts the quarter of an hour again, from the count so far.
        await RecordFailuresAsync(suspicion, 1, "10.0.0.3", null);
        _time.Advance(AutomationSuspicion.Window - TimeSpan.FromSeconds(1));
        (await suspicion.FailuresAsync("10.0.0.3", null, Ct)).ShouldBe(4);

        _time.Advance(TimeSpan.FromSeconds(1));
        (await suspicion.FailuresAsync("10.0.0.3", null, Ct)).ShouldBe(0);
        (await suspicion.RequiredBitsAsync(14, 18, "10.0.0.3", null, Ct)).ShouldBe(14);

        // And the next failure counts from nothing.
        await RecordFailuresAsync(suspicion, 1, "10.0.0.3", null);
        (await suspicion.FailuresAsync("10.0.0.3", null, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_flood_of_failures_past_the_most_work_is_counted_once_a_minute()
    {
        // Rate-limit refusals come as fast as a script sends them; on a node of several each count is a statement on the
        // hospital's database.
        var suspicion = Suspicion();

        await RecordFailuresAsync(suspicion, 1000, "10.0.0.30", null);

        _store.Written.Count.ShouldBe(AutomationSuspicion.SaturatedFailures);
        (await suspicion.RequiredBitsAsync(0, AntiAutomationSettings.Ceiling, "10.0.0.30", null, Ct)).ShouldBe(AntiAutomationSettings.Ceiling);

        // A flood that goes on keeps the count up: one more a minute, well within the quarter of an hour.
        for (var minute = 1; minute <= 20; minute++)
        {
            _time.Advance(AutomationSuspicion.SaturatedInterval);
            await RecordFailuresAsync(suspicion, 100, "10.0.0.30", null);
        }

        _store.Written.Count.ShouldBe(AutomationSuspicion.SaturatedFailures + 20);
        (await suspicion.FailuresAsync("10.0.0.30", null, Ct)).ShouldBe(AutomationSuspicion.SaturatedFailures + 20);
    }

    [Fact]
    public async Task The_count_written_last_asks_for_the_most_work_under_any_settings()
    {
        // No base and the highest most a hospital can set: the count past which a node stops writing still asks for it.
        var suspicion = Suspicion();

        await RecordFailuresAsync(suspicion, AutomationSuspicion.SaturatedFailures, "10.0.0.33", null);

        (await suspicion.RequiredBitsAsync(0, AntiAutomationSettings.Ceiling, "10.0.0.33", null, Ct)).ShouldBe(AntiAutomationSettings.Ceiling);
        (await suspicion.RequiredBitsAsync(0, AntiAutomationSettings.Ceiling, "10.0.0.34", null, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_count_the_store_cannot_take_leaves_the_answer_alone_and_is_tried_again_a_minute_later()
    {
        var suspicion = Suspicion();
        _store.Fails = true;

        await RecordFailuresAsync(suspicion, 50, "10.0.0.31", null);

        _store.Attempts.ShouldBe(1, "a store that is down is not asked once per refusal");
        _time.Advance(AutomationSuspicion.SaturatedInterval);
        _store.Fails = false;
        await RecordFailuresAsync(suspicion, 1, "10.0.0.31", null);
        (await suspicion.FailuresAsync("10.0.0.31", null, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task While_turned_off_nothing_is_asked_and_everything_passes()
    {
        var antiAutomation = AntiAutomationWith(new() { [AntiAutomationSettings.Enabled] = "false" }, out var audit);

        var challenge = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, null, "10.0.0.1", Ct);
        var outcome = await antiAutomation.CheckAsync(null, AntiAutomationActions.Otp, null, "10.0.0.1", Ct);

        challenge.Required.ShouldBeFalse();
        challenge.Challenge.ShouldBeNull();
        outcome.ShouldBe(ProofOfWorkOutcome.Accepted);
        await audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, Ct);
    }

    [Fact]
    public async Task By_default_a_light_challenge_is_asked_for()
    {
        var antiAutomation = AntiAutomationWith([], out _);

        var challenge = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, null, "10.0.0.1", Ct);

        challenge.Required.ShouldBeTrue();
        challenge.Bits.ShouldBe(AntiAutomationSettings.DefaultBits);
        challenge.Algorithm.ShouldBe("SHA-256");
        challenge.ExpiresAt.ShouldBe(_time.GetUtcNow() + AntiAutomation.ChallengeLifetime);
    }

    [Fact]
    public async Task The_settings_tune_the_work_within_bounds()
    {
        var antiAutomation = AntiAutomationWith(new() { [AntiAutomationSettings.Bits] = 40, [AntiAutomationSettings.MaxBits] = 2 }, out _);

        var challenge = await antiAutomation.IssueAsync(AntiAutomationActions.Feedback, null, "10.0.0.1", Ct);

        challenge.Bits.ShouldBe(AntiAutomationSettings.Ceiling);
    }

    [Fact]
    public void The_most_work_ever_asked_is_done_on_a_slow_phone_within_the_challenges_life()
    {
        // WebCrypto SHA-256 in a worker on a mid-range phone: 20,000 to 50,000 a second (AntiAutomationSettings.DefaultBits).
        const double SlowPhoneHashesPerSecond = 20_000;

        // The tries a solution takes are geometric: one in twenty takes ln 20 (some three) times the average.
        var unlucky = TimeSpan.FromSeconds(Math.Pow(2, AntiAutomationSettings.Ceiling) / SlowPhoneHashesPerSecond * Math.Log(20));

        unlucky.ShouldBeLessThan(AntiAutomation.ChallengeLifetime);
        AntiAutomationSettings.DefaultMaxBits.ShouldBeLessThanOrEqualTo(AntiAutomationSettings.Ceiling);
    }

    [Theory]
    [InlineData(AntiAutomationSettings.MaxBits, "20", null)]
    [InlineData(AntiAutomationSettings.MaxBits, "\"18\"", null)]
    [InlineData(AntiAutomationSettings.MaxBits, "21", "Enter a whole number of bits from 0 to 20.")]
    [InlineData(AntiAutomationSettings.MaxBits, "24", "Enter a whole number of bits from 0 to 20.")]
    [InlineData(AntiAutomationSettings.Bits, "0", null)]
    [InlineData(AntiAutomationSettings.Bits, "-1", "Enter a whole number of bits from 0 to 20.")]
    [InlineData(AntiAutomationSettings.Bits, "14.5", "Enter a whole number of bits from 0 to 20.")]
    [InlineData(AntiAutomationSettings.Bits, "\"lots\"", "Enter a whole number of bits from 0 to 20.")]
    [InlineData(AntiAutomationSettings.Bits, "{\"bits\":14}", "Enter a whole number of bits from 0 to 20.")]
    [InlineData(AntiAutomationSettings.Bits, "null", null)]
    [InlineData(AntiAutomationSettings.Enabled, "false", null)]
    [InlineData(AntiAutomationSettings.Enabled, "\"True\"", null)]
    [InlineData(AntiAutomationSettings.Enabled, "\"yes\"", "Enter true or false.")]
    [InlineData("ui.idleLockMinutes", "999", null)]
    public void The_settings_are_checked_before_they_are_saved(string key, string json, string? problem) =>
        AntiAutomationSettings.Problem(key, JsonNode.Parse(json)).ShouldBe(problem);

    [Fact]
    public async Task A_refusal_is_audited_once_per_address_and_reason_and_raises_the_work()
    {
        var antiAutomation = AntiAutomationWith(new() { [AntiAutomationSettings.Bits] = 4 }, out var audit);

        for (var i = 0; i < 3; i++)
        {
            (await antiAutomation.CheckAsync("junk", AntiAutomationActions.Otp, null, "10.0.0.7", Ct)).ShouldBe(ProofOfWorkOutcome.Malformed);
        }

        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == "security.anti_automation_rejected" && e.Category == AuditCategories.Security && e.Severity == AuditSeverity.Warning),
            Arg.Any<CancellationToken>());
        (await antiAutomation.IssueAsync(AntiAutomationActions.Otp, null, "10.0.0.7", Ct)).Bits.ShouldBe(6);
    }

    [Fact]
    public async Task A_refusal_is_audited_once_whichever_server_refuses_it()
    {
        var first = AntiAutomationWith([], out var firstAudit);
        var second = AntiAutomationWith([], out var secondAudit, new MemoryCache(new MemoryCacheOptions()));

        (await first.CheckAsync("junk", AntiAutomationActions.Otp, null, "10.0.0.32", Ct)).ShouldBe(ProofOfWorkOutcome.Malformed);
        (await second.CheckAsync("junk", AntiAutomationActions.Otp, null, "10.0.0.32", Ct)).ShouldBe(ProofOfWorkOutcome.Malformed);
        (await second.CheckAsync(null, AntiAutomationActions.Otp, null, "10.0.0.32", Ct)).ShouldBe(ProofOfWorkOutcome.Missing);

        await firstAudit.ReceivedWithAnyArgs(1).WriteAsync(default!, Ct);
        await secondAudit.ReceivedWithAnyArgs(1).WriteAsync(default!, Ct);
        await secondAudit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Summary!.Contains("(missing)")), Arg.Any<CancellationToken>());

        // Five minutes on, the next is audited again.
        _time.Advance(AutomationSuspicion.AuditQuietPeriod);
        (await second.CheckAsync("junk", AntiAutomationActions.Otp, null, "10.0.0.32", Ct)).ShouldBe(ProofOfWorkOutcome.Malformed);
        await secondAudit.ReceivedWithAnyArgs(2).WriteAsync(default!, Ct);
    }

    [Fact]
    public async Task A_challenge_issued_before_the_work_went_up_is_refused_and_a_new_one_asks_for_more()
    {
        var antiAutomation = AntiAutomationWith(new() { [AntiAutomationSettings.Bits] = 4 }, out _);
        var early = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, "9876543210", "10.0.0.8", Ct);

        for (var i = 0; i < 3; i++)
        {
            await antiAutomation.RecordFailureAsync("10.0.0.9", "9876543210");
        }

        var proof = ProofOfWork.Solve(early.Challenge!, early.Bits);
        (await antiAutomation.CheckAsync(proof, AntiAutomationActions.Otp, "9876543210", "10.0.0.8", Ct)).ShouldBe(ProofOfWorkOutcome.TooEasy);
        var next = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, "9876543210", "10.0.0.8", Ct);
        next.Bits.ShouldBe(6);
        (await antiAutomation.CheckAsync(ProofOfWork.Solve(next.Challenge!, next.Bits), AntiAutomationActions.Otp, "9876543210", "10.0.0.8", Ct))
            .ShouldBe(ProofOfWorkOutcome.Accepted);
    }

    [Fact]
    public async Task A_too_easy_proof_is_refused_without_counting_as_a_failure_or_being_audited()
    {
        // The portal solves its sign-in challenge before the number is typed: for a number that already failed three times
        // its first proof is too easy. Retrying must not raise the work asked of the patient's address or number further.
        var antiAutomation = AntiAutomationWith(new() { [AntiAutomationSettings.Bits] = 4 }, out var audit);
        var prepared = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, null, "10.0.0.10", Ct);
        for (var i = 0; i < 3; i++)
        {
            await antiAutomation.RecordFailureAsync("10.0.0.11", "9876501234");
        }

        var proof = ProofOfWork.Solve(prepared.Challenge!, prepared.Bits);
        for (var i = 0; i < 3; i++)
        {
            (await antiAutomation.CheckAsync(proof, AntiAutomationActions.Otp, "9876501234", "10.0.0.10", Ct)).ShouldBe(ProofOfWorkOutcome.TooEasy);
        }

        (await antiAutomation.IssueAsync(AntiAutomationActions.Otp, null, "10.0.0.10", Ct)).Bits.ShouldBe(4);
        var forTheNumber = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, "9876501234", "10.0.0.10", Ct);
        forTheNumber.Bits.ShouldBe(6);
        await audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, Ct);
        (await antiAutomation.CheckAsync(ProofOfWork.Solve(forTheNumber.Challenge!, forTheNumber.Bits), AntiAutomationActions.Otp, "9876501234", "10.0.0.10", Ct))
            .ShouldBe(ProofOfWorkOutcome.Accepted);
    }

    [Theory]
    [InlineData(ProofOfWorkOutcome.Accepted, false)]
    [InlineData(ProofOfWorkOutcome.TooEasy, false)]
    [InlineData(ProofOfWorkOutcome.Missing, true)]
    [InlineData(ProofOfWorkOutcome.Malformed, true)]
    [InlineData(ProofOfWorkOutcome.Tampered, true)]
    [InlineData(ProofOfWorkOutcome.WrongAction, true)]
    [InlineData(ProofOfWorkOutcome.Expired, true)]
    [InlineData(ProofOfWorkOutcome.Unsolved, true)]
    [InlineData(ProofOfWorkOutcome.Replayed, true)]
    public void Every_refusal_but_a_too_easy_proof_is_a_failure(ProofOfWorkOutcome outcome, bool failure) =>
        AntiAutomation.IsFailure(outcome).ShouldBe(failure);

    private static async Task RecordFailuresAsync(AutomationSuspicion suspicion, int count, string address, string? subject)
    {
        for (var i = 0; i < count; i++)
        {
            await suspicion.RecordFailureAsync(address, subject);
        }
    }

    private async Task<List<ProofOfWorkOutcome>> OutcomesAsync(string challenge, int requiredBits)
    {
        var outcomes = new List<ProofOfWorkOutcome>();
        for (var nonce = 0; nonce < 4; nonce++)
        {
            outcomes.Add(await Pow.VerifyAsync($"{challenge}:{nonce}", AntiAutomationActions.Otp, requiredBits, Ct));
        }

        return outcomes;
    }

    /// <summary>The check as a server whose memory is <paramref name="cache"/> (this one's by default) makes it, on the shared store.</summary>
    private AntiAutomation AntiAutomationWith(Dictionary<string, object> values, out IAuditLogger audit, IMemoryCache? cache = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<JsonElement?>(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => values.TryGetValue(call.Arg<string>(), out var value) ? JsonSerializer.SerializeToElement(value) : (JsonElement?)null);
        audit = Substitute.For<IAuditLogger>();
        return new AntiAutomation(Pow, Suspicion(cache), settings, audit, _time);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The store several servers share, recording what is written to it; <see cref="Fails"/> makes it unreachable.</summary>
    private sealed class RecordingStore(IClusterStore inner) : IClusterStore
    {
        private int _attempts;

        public ConcurrentQueue<(string Key, TimeSpan Lifetime)> Written { get; } = new();

        public bool Fails { get; set; }

        /// <summary>Writes tried while the store <see cref="Fails"/>.</summary>
        public int Attempts => _attempts;

        public Task<bool> TryAddAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            Write(key, lifetime);
            return inner.TryAddAsync(key, lifetime, cancellationToken);
        }

        public Task<ClusterCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
        {
            Write(key, window);
            return inner.IncrementAsync(key, window, cancellationToken);
        }

        public Task<ClusterCount> IncrementUntilQuietAsync(string key, TimeSpan quiet, CancellationToken cancellationToken = default)
        {
            Write(key, quiet);
            return inner.IncrementUntilQuietAsync(key, quiet, cancellationToken);
        }

        public Task<long> GetCountAsync(string key, CancellationToken cancellationToken = default) => inner.GetCountAsync(key, cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);

        private void Write(string key, TimeSpan lifetime)
        {
            if (Fails)
            {
                Interlocked.Increment(ref _attempts);
                throw new TimeoutException("The store cannot be reached.");
            }

            if (!key.StartsWith("pow:audited:", StringComparison.Ordinal))
            {
                Written.Enqueue((key, lifetime));
            }
        }
    }
}
