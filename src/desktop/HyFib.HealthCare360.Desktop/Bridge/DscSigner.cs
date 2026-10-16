using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Interop;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// Signs a prescription with the doctor's Digital Signature Certificate (ERX-04, bridge method
/// <c>dsc.signPrescription</c>). A DSC on a USB token (ePass, Proxkey, SafeNet…) registers its certificate in the
/// current user's Windows "My" store through the vendor's CSP/KSP, so no vendor code is needed: HC360 Desktop lists
/// the certificates that can sign now (<see cref="SigningCertificates"/>), the doctor picks one in the Windows
/// certificate dialog, which shows what is being signed, read from the bytes themselves
/// (<see cref="PrescriptionSigningRequest"/>), and the token asks for its PIN. The result is a detached CMS/PKCS#7
/// signature (<see cref="DetachedCms"/>) carrying the certificate and the CA certificates above it that this computer
/// has; the server checks it again, including that the certificate chains to a CCA India root, before keeping it. The
/// private key never leaves the token, and nothing is stored on this computer.
/// </summary>
internal static class DscSigner
{
    // One certificate dialog at a time: a page cannot stack prompts.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static object Sign(string? content)
    {
        PrescriptionSigningRequest request;
        try
        {
            request = PrescriptionSigningRequest.Parse(content);
        }
        catch (FormatException ex)
        {
            throw new BridgeException("bad_request", ex.Message);
        }

        if (!Gate.Wait(0))
        {
            throw new BridgeException("busy", "HC360 Desktop is already signing a prescription.");
        }

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var all = store.Certificates;
            try
            {
                var usable = SigningCertificates.Usable(all, DateTimeOffset.UtcNow);
                if (usable.Count == 0)
                {
                    throw new BridgeException("no_certificate",
                        "No Digital Signature Certificate that can sign was found. Insert your DSC token and try again.");
                }

                var chosen = X509Certificate2UI.SelectFromCollection(
                    new X509Certificate2Collection(usable.ToArray()),
                    "Sign the prescription with your DSC",
                    request.Summary,
                    X509SelectionFlag.SingleSelection,
                    OwnerWindow());
                if (chosen.Count == 0)
                {
                    throw new BridgeException("cancelled", "No certificate was chosen. The prescription was not signed.");
                }

                var certificate = chosen[0];
                var issuers = SigningCertificates.Issuers(certificate);
                try
                {
                    var signature = DetachedCms.Sign(request.Content, certificate, DateTimeOffset.UtcNow, issuers: issuers);
                    Log.Information("Signed prescription {Number} with certificate {Thumbprint}", request.PrescriptionNumber, certificate.Thumbprint);
                    return new { signature = Convert.ToBase64String(signature), certificate = SignerCertificate.Of(certificate) };
                }
                catch (CryptographicException ex)
                {
                    // A wrong or cancelled PIN, a token removed mid-way, a key the token will not use for signing.
                    Log.Warning(ex, "Signing prescription {Number} with certificate {Thumbprint} failed", request.PrescriptionNumber, certificate.Thumbprint);
                    throw new BridgeException("signing_failed", "The DSC could not sign. Check that the token is inserted and the PIN is right.");
                }
                finally
                {
                    foreach (var issuer in issuers)
                    {
                        issuer.Dispose();
                    }
                }
            }
            finally
            {
                foreach (var certificate in all)
                {
                    certificate.Dispose();
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IntPtr OwnerWindow()
    {
        var window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }
}
