using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// HC360 Desktop keeps a CA that IT's machine policy names through a rollover only on the word of that CA
/// (<see cref="HospitalCaPolicy"/>). Checked here against the server's own rollover ('certificates prepare-ca', then
/// 'switch-ca'), and against what the user, who can edit the settings file, could put in it. HC360 Desktop's own lines
/// around it are tested on Windows.
/// </summary>
public sealed class HospitalCaPolicyTests : IDisposable
{
    /// <summary>When the current CA was made: nine and a half years ago, so it is in its last year, as a CA is when it is rolled over.</summary>
    private static readonly DateTimeOffset Made = DateTimeOffset.UtcNow.AddYears(-9).AddMonths(-6);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_policy_that_names_the_old_CA_keeps_a_workstation_that_switched_on_that_CAs_word_and_no_other()
    {
        var ours = Rollover("primary");
        var theirs = Rollover("other-hospital");

        Decide(ours.Old.Fingerprint, ours.Next.Pem, previous: ours.Old.Pem, cross: ours.Cross).ShouldBe(HospitalCaPolicyOutcome.Keep, "the policy's CA vouched for the one it pins");
        Decide(ours.Next.Fingerprint, ours.Next.Pem, previous: ours.Old.Pem, cross: ours.Cross).ShouldBe(HospitalCaPolicyOutcome.Keep, "IT updated the policy to the new CA");
        Decide(ours.Old.Fingerprint, ours.Next.Pem, previous: ours.Old.Pem, cross: theirs.Cross).ShouldBe(HospitalCaPolicyOutcome.SetUpAgain, "another rollover's word");
        Decide(ours.Old.Fingerprint, ours.Next.Pem, previous: ours.Old.Pem, cross: null).ShouldBe(HospitalCaPolicyOutcome.SetUpAgain, "no word at all");
        Decide(ours.Old.Fingerprint, theirs.Next.Pem, previous: ours.Old.Pem, cross: ours.Cross).ShouldBe(HospitalCaPolicyOutcome.SetUpAgain, "a CA the policy's CA did not vouch for");
        Decide(ours.Old.Fingerprint, ours.Old.Pem).ShouldBe(HospitalCaPolicyOutcome.Keep, "as before any rollover");
        Decide(theirs.Old.Fingerprint, ours.Old.Pem).ShouldBe(HospitalCaPolicyOutcome.SetUpAgain, "another hospital's CA");
        Decide(ours.Old.Fingerprint, "-----BEGIN CERTIFICATE-----\nnot one\n-----END CERTIFICATE-----").ShouldBe(HospitalCaPolicyOutcome.SetUpAgain);
    }

    /// <summary>
    /// IT may name the next CA in the policy before a workstation switched: it pins that CA alone. Here also a settings file
    /// edited to pin some other CA, with that CA's own word for the hospital's next CA: the workstation pins the next CA, which
    /// the policy names, and nothing else.
    /// </summary>
    [Fact]
    public void A_policy_that_names_the_next_CA_has_the_workstation_pin_it_alone()
    {
        var ours = Rollover("primary");
        var theirs = Rollover("other-hospital");

        Decide(ours.Next.Fingerprint, ours.Old.Pem, next: ours.Next.Pem).ShouldBe(HospitalCaPolicyOutcome.PinNextAlone);
        Decide(ours.Old.Fingerprint, ours.Old.Pem, next: ours.Next.Pem).ShouldBe(HospitalCaPolicyOutcome.Keep, "a policy naming the CA it pins keeps the next CA it holds");
        Decide(ours.Next.Fingerprint, theirs.Old.Pem, next: ours.Next.Pem).ShouldBe(HospitalCaPolicyOutcome.PinNextAlone, "whatever else the file pins goes");
        Decide(theirs.Next.Fingerprint, ours.Old.Pem, next: ours.Next.Pem).ShouldBe(HospitalCaPolicyOutcome.SetUpAgain, "a CA it does not hold");
    }

    private static HospitalCaPolicyOutcome Decide(string policy, string ca, string? previous = null, string? cross = null, string? next = null) =>
        HospitalCaPolicy.Decide(policy, ca, previous, cross, next);

    /// <summary>A server's rollover as the commands make it: the CA made nine and a half years ago, the next one, and the first's word for it.</summary>
    private (PinnedCa Old, PinnedCa Next, string Cross) Rollover(string name)
    {
        var node = new HospitalCaTestServer(_root, name);
        var old = node.Ca(Made);
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, DateTimeOffset.UtcNow);
        using var server = node.Start(DateTimeOffset.UtcNow);
        return (old, new PinnedCa(server.NextCaFingerprint!, server.NextCaPem!, DateTimeOffset.MaxValue), server.NextCaCrossPem!);
    }
}
