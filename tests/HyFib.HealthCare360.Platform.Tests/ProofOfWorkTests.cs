using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The portal's proof of work (SRS SEC-06): a challenge is signed, good for one kind of request, for a few minutes and for
/// one request only, and the work it asks for goes up for an address or a number that keeps failing.
/// </summary>
public sealed class ProofOfWorkTests
{
    private const int Bits = 8;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Clock _time = new(new DateTimeOffset(2026, 10, 16, 6, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProofOfWork Pow => new(_key, new MemoryProofOfWorkReplayCache(_cache, _time), _time);

    private string NewChallenge(string action = AntiAutomationActions.Otp, int bits = Bits) => Pow.Issue(action, bits, _time.GetUtcNow().AddMinutes(5));

    [Fact]
    public void A_solved_challenge_is_accepted()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);

        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.Accepted);
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
    public void A_proof_is_good_for_one_request()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);

        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.Accepted);
        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.Replayed);
    }

    [Fact]
    public void Another_nonce_for_a_spent_challenge_is_refused_too()
    {
        var challenge = NewChallenge(bits: 0);

        Pow.Verify(challenge + ":1", AntiAutomationActions.Otp, 0).ShouldBe(ProofOfWorkOutcome.Accepted);
        Pow.Verify(challenge + ":2", AntiAutomationActions.Otp, 0).ShouldBe(ProofOfWorkOutcome.Replayed);
    }

    [Fact]
    public void An_expired_challenge_is_refused()
    {
        var proof = ProofOfWork.Solve(NewChallenge(), Bits);
        _time.Advance(TimeSpan.FromMinutes(5));

        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.Expired);
    }

    [Fact]
    public void A_challenge_with_easier_bits_written_in_is_refused_as_tampered()
    {
        var challenge = NewChallenge(bits: 20);
        var parts = challenge.Split('.');
        parts[2] = "0";
        var forged = string.Join('.', parts);

        Pow.Verify(forged + ":0", AntiAutomationActions.Otp, 0).ShouldBe(ProofOfWorkOutcome.Tampered);
    }

    [Fact]
    public void A_challenge_signed_by_another_server_is_refused_as_tampered()
    {
        var other = new ProofOfWork(RandomNumberGenerator.GetBytes(32), new MemoryProofOfWorkReplayCache(_cache, _time), _time);
        var proof = ProofOfWork.Solve(other.Issue(AntiAutomationActions.Otp, Bits, _time.GetUtcNow().AddMinutes(5)), Bits);

        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.Tampered);
    }

    [Fact]
    public void A_challenge_for_another_kind_of_request_is_refused()
    {
        var proof = ProofOfWork.Solve(NewChallenge(AntiAutomationActions.Feedback), Bits);

        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.WrongAction);
    }

    [Fact]
    public void A_challenge_easier_than_the_request_now_needs_is_refused()
    {
        var proof = ProofOfWork.Solve(NewChallenge(bits: Bits), Bits);

        Pow.Verify(proof, AntiAutomationActions.Otp, Bits + 2).ShouldBe(ProofOfWorkOutcome.TooEasy);
    }

    [Fact]
    public void An_unsolved_challenge_easier_than_needed_is_refused_as_unsolved_not_too_easy()
    {
        var challenge = NewChallenge(bits: 24);

        // Too easy is kept for a browser that did the work it was asked for, which AntiAutomation does not hold against it.
        var outcomes = Enumerable.Range(0, 4).Select(n => Pow.Verify($"{challenge}:{n}", AntiAutomationActions.Otp, 26)).ToList();

        outcomes.ShouldContain(ProofOfWorkOutcome.Unsolved);
        outcomes.ShouldNotContain(ProofOfWorkOutcome.TooEasy);
    }

    [Fact]
    public void A_nonce_that_does_not_solve_the_challenge_is_refused()
    {
        var challenge = NewChallenge(bits: 24);

        // One nonce in 2^24 solves it; the first few are as good as certain not to.
        var outcomes = Enumerable.Range(0, 4).Select(n => Pow.Verify($"{challenge}:{n}", AntiAutomationActions.Otp, 24)).ToList();

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
    public void A_malformed_proof_is_refused(string? proof, ProofOfWorkOutcome expected) =>
        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(expected);

    [Fact]
    public void An_unsolved_or_refused_proof_does_not_use_the_challenge_up()
    {
        var challenge = NewChallenge();
        var proof = ProofOfWork.Solve(challenge, Bits);

        Pow.Verify(proof, AntiAutomationActions.Feedback, Bits).ShouldBe(ProofOfWorkOutcome.WrongAction);
        Pow.Verify(proof, AntiAutomationActions.Otp, Bits + 1).ShouldBe(ProofOfWorkOutcome.TooEasy);
        Pow.Verify(proof, AntiAutomationActions.Otp, Bits).ShouldBe(ProofOfWorkOutcome.Accepted);
    }

    [Fact]
    public void Every_three_failures_ask_for_two_more_bits_up_to_the_most()
    {
        var suspicion = new AutomationSuspicion(_cache, _time, _key);

        suspicion.RequiredBits(14, 18, "10.0.0.1", null).ShouldBe(14);
        RecordFailures(suspicion, 2, "10.0.0.1", null);
        suspicion.RequiredBits(14, 18, "10.0.0.1", null).ShouldBe(14);
        RecordFailures(suspicion, 1, "10.0.0.1", null);
        suspicion.RequiredBits(14, 18, "10.0.0.1", null).ShouldBe(16);
        RecordFailures(suspicion, 30, "10.0.0.1", null);
        suspicion.RequiredBits(14, 18, "10.0.0.1", null).ShouldBe(18);
        suspicion.RequiredBits(14, 18, "10.0.0.2", null).ShouldBe(14);
    }

    [Fact]
    public void Failures_for_a_number_raise_the_work_for_it_from_any_address()
    {
        var suspicion = new AutomationSuspicion(_cache, _time, _key);
        RecordFailures(suspicion, 3, "10.0.0.1", "98765 43210");

        // The number is matched however it is written.
        suspicion.RequiredBits(14, 18, "10.0.0.9", "+919876543210").ShouldBe(16);
        suspicion.RequiredBits(14, 18, "10.0.0.9", "9123456789").ShouldBe(14);
    }

    [Fact]
    public void Numbers_are_kept_only_as_hashes_keyed_by_the_server()
    {
        var suspicion = new AutomationSuspicion(_cache, _time, _key);
        var number = SharedKernel.Phone.Normalize("9876543210")!;
        var plain = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(number)), 0, 16);

        RecordFailures(suspicion, 1, "10.0.0.4", "98765 43210");

        // India's mobile numbers are few enough to hash every one: a plain hash would give the number back.
        var keys = _cache.Keys.OfType<string>().Where(k => k.StartsWith("pow:fail:subject:", StringComparison.Ordinal)).ToList();
        keys.ShouldHaveSingleItem().ShouldBe("pow:fail:subject:" + suspicion.SubjectHash(number));
        keys.ShouldAllBe(k => !k.Contains(plain) && !k.Contains("9876543210"));
        suspicion.SubjectHash("+91 98765 43210").ShouldBe(suspicion.SubjectHash(number), "the number however it is written");
        new AutomationSuspicion(_cache, _time, RandomNumberGenerator.GetBytes(32)).SubjectHash(number)
            .ShouldNotBe(suspicion.SubjectHash(number), "another server's key, another hash");
    }

    [Fact]
    public void The_count_falls_back_only_after_a_quiet_quarter_of_an_hour()
    {
        var suspicion = new AutomationSuspicion(_cache, _time, _key);
        RecordFailures(suspicion, 2, "10.0.0.3", null);
        _time.Advance(TimeSpan.FromMinutes(10));
        RecordFailures(suspicion, 1, "10.0.0.3", null);
        suspicion.RequiredBits(14, 18, "10.0.0.3", null).ShouldBe(16);

        // 20 minutes after the first failure, 10 after the last: still counted.
        _time.Advance(TimeSpan.FromMinutes(10));
        suspicion.Failures("10.0.0.3", null).ShouldBe(3);
        suspicion.RequiredBits(14, 18, "10.0.0.3", null).ShouldBe(16);

        // A failure now starts the quarter of an hour again, from the count so far.
        RecordFailures(suspicion, 1, "10.0.0.3", null);
        _time.Advance(AutomationSuspicion.Window - TimeSpan.FromSeconds(1));
        suspicion.Failures("10.0.0.3", null).ShouldBe(4);

        _time.Advance(TimeSpan.FromSeconds(1));
        suspicion.Failures("10.0.0.3", null).ShouldBe(0);
        suspicion.RequiredBits(14, 18, "10.0.0.3", null).ShouldBe(14);

        // And the next failure counts from nothing.
        RecordFailures(suspicion, 1, "10.0.0.3", null);
        suspicion.Failures("10.0.0.3", null).ShouldBe(1);
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
    public async Task A_challenge_issued_before_the_work_went_up_is_refused_and_a_new_one_asks_for_more()
    {
        var antiAutomation = AntiAutomationWith(new() { [AntiAutomationSettings.Bits] = 4 }, out _);
        var early = await antiAutomation.IssueAsync(AntiAutomationActions.Otp, "9876543210", "10.0.0.8", Ct);

        for (var i = 0; i < 3; i++)
        {
            antiAutomation.RecordFailure("10.0.0.9", "9876543210");
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
            antiAutomation.RecordFailure("10.0.0.11", "9876501234");
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

    private static void RecordFailures(AutomationSuspicion suspicion, int count, string address, string? subject)
    {
        for (var i = 0; i < count; i++)
        {
            suspicion.RecordFailure(address, subject);
        }
    }

    private AntiAutomation AntiAutomationWith(Dictionary<string, object> values, out IAuditLogger audit)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<JsonElement?>(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => values.TryGetValue(call.Arg<string>(), out var value) ? JsonSerializer.SerializeToElement(value) : (JsonElement?)null);
        audit = Substitute.For<IAuditLogger>();
        return new AntiAutomation(Pow, new AutomationSuspicion(_cache, _time, _key), settings, audit, _cache, _time);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
