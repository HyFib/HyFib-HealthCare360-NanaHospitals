using System.Diagnostics;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// A third party checks a signed prescription with OpenSSL alone (ERX-04): the .p7s and the signed bytes, as the
/// verification page and the chart download them, pass <c>openssl cms -verify -binary -inform DER -in prescription.p7s
/// -content prescription.json -noverify</c>, and changed bytes fail it. Runs where <c>openssl</c> is on the PATH.
/// </summary>
public sealed class PrescriptionSignatureOpensslTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hc360-dsc-").FullName;

    [Fact]
    public async Task Openssl_accepts_the_signature_over_the_signed_bytes_and_rejects_changed_ones()
    {
        Assert.SkipWhen(await RunAsync("version") is not { ExitCode: 0 }, "openssl is not installed here.");
        using var certificate = PrescriptionDigitalSignatureTests.Certificate();
        var content = PrescriptionCanonicalForm.Build(PrescriptionCanonicalFormTests.Signed(), PrescriptionCanonicalFormTests.Parties);
        var p7s = Path.Combine(_folder, "prescription.p7s");
        var json = Path.Combine(_folder, "prescription.json");
        var changed = Path.Combine(_folder, "changed.json");
        await File.WriteAllBytesAsync(p7s, DetachedCms.Sign(content, certificate, DateTimeOffset.UtcNow, silent: true), Ct);
        await File.WriteAllBytesAsync(json, content, Ct);
        await File.WriteAllBytesAsync(changed, [.. content.Take(content.Length - 2), (byte)' ', (byte)'}'], Ct);

        var verified = (await VerifyAsync(p7s, json, Path.Combine(_folder, "out.json")))!.Value;
        var refused = (await VerifyAsync(p7s, changed, Path.Combine(_folder, "out2.json")))!.Value;

        verified.ExitCode.ShouldBe(0, verified.Output);
        verified.Output.ShouldContain("Verification successful");
        (await File.ReadAllBytesAsync(Path.Combine(_folder, "out.json"), Ct)).ShouldBe(content);
        refused.ExitCode.ShouldNotBe(0, "OpenSSL must refuse bytes the doctor did not sign");
        refused.Output.ShouldContain("Verification failure");
    }

    [Fact]
    public async Task Openssl_follows_the_chain_to_the_root_and_refuses_another_root()
    {
        Assert.SkipWhen(await RunAsync("version") is not { ExitCode: 0 }, "openssl is not installed here.");
        using var pki = new TestPki();
        using var other = new TestPki("Some Other Root");
        using var doctor = pki.Doctor();
        var content = PrescriptionCanonicalForm.Build(PrescriptionCanonicalFormTests.Signed(), PrescriptionCanonicalFormTests.Parties);
        var p7s = Path.Combine(_folder, "prescription.p7s");
        var json = Path.Combine(_folder, "prescription.json");
        var root = Path.Combine(_folder, "cca-root.pem");
        var otherRoot = Path.Combine(_folder, "other-root.pem");
        await File.WriteAllBytesAsync(p7s, DetachedCms.Sign(content, doctor, DateTimeOffset.UtcNow, silent: true, issuers: [pki.Ca]), Ct);
        await File.WriteAllBytesAsync(json, content, Ct);
        await File.WriteAllTextAsync(root, pki.Root.ExportCertificatePem(), Ct);
        await File.WriteAllTextAsync(otherRoot, other.Root.ExportCertificatePem(), Ct);

        var verified = (await VerifyChainAsync(p7s, json, root, Path.Combine(_folder, "out.json")))!.Value;
        var refused = (await VerifyChainAsync(p7s, json, otherRoot, Path.Combine(_folder, "out2.json")))!.Value;

        verified.ExitCode.ShouldBe(0, verified.Output);
        refused.ExitCode.ShouldNotBe(0, "a DSC that does not chain to the given root must fail");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // As documented in docs/modules/clinical.md (Checking a signature with OpenSSL alone).
    private static Task<(int ExitCode, string Output)?> VerifyAsync(string p7s, string content, string output) =>
        RunAsync("cms", "-verify", "-binary", "-inform", "DER", "-in", p7s, "-content", content, "-noverify", "-out", output);

    // With the chain: the root from -CAfile, the licensed CA's certificate from the .p7s (docs/modules/clinical.md).
    private static Task<(int ExitCode, string Output)?> VerifyChainAsync(string p7s, string content, string root, string output) =>
        RunAsync("cms", "-verify", "-binary", "-inform", "DER", "-in", p7s, "-content", content, "-CAfile", root, "-purpose", "any", "-out", output);

    private static async Task<(int ExitCode, string Output)?> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("openssl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            var error = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            return (process.ExitCode, await output + await error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
