using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A throwaway DSC hierarchy shaped like India's (ERX-04): a root, which <see cref="InstallAsync"/> puts in the test
/// server's DSC trust folder as IT installs the CCA India roots, a licensed CA under it, and doctors' DSCs under that.
/// HC360 Desktop's part (choosing the certificate, the token's PIN) is played by <see cref="SignAsync"/>, which signs the
/// bytes the server hands out as <see cref="DetachedCms"/> does on the desktop.
/// </summary>
internal sealed class DscTestPki : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public DscTestPki(string rootName = "Test CCA India Root")
    {
        using var rootKey = RSA.Create(2048);
        var root = new CertificateRequest($"CN={rootName}, O=Test, C=IN", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = root.CreateSelfSigned(Now.AddYears(-1), Now.AddYears(10));

        using var caKey = RSA.Create(2048);
        var ca = new CertificateRequest($"CN=Test Licensed CA under {rootName}, O=Test, C=IN", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        ca.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var caPublic = ca.Create(Root, Now.AddMonths(-11), Now.AddYears(5), Serial());
        Ca = caPublic.CopyWithPrivateKey(caKey);
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Ca { get; }

    /// <summary>
    /// A DSC issued to <paramref name="name"/>, as the common name of its subject; <paramref name="moreSubject"/> adds
    /// to the subject after it ("OU=…").
    /// </summary>
    public X509Certificate2 Doctor(string name, string? moreSubject = null)
    {
        using var key = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={name}, {(moreSubject is null ? string.Empty : moreSubject + ", ")}O=Personal, C=IN");
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var publicOnly = request.Create(Ca, Now.AddDays(-1), Now.AddYears(2), Serial());
        return publicOnly.CopyWithPrivateKey(key);
    }

    /// <summary>Puts <see cref="Root"/> in the server's DSC trust folder; the file's path, to take it out again.</summary>
    public async Task<string> InstallAsync(HospitalServer server, CancellationToken ct)
    {
        var folder = server.Services.GetRequiredService<DscTrustStore>().Folder;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"test-root-{Root.Thumbprint}.cer");
        await File.WriteAllBytesAsync(file, Root.RawData, ct);
        return file;
    }

    /// <summary>Signs the prescription with <paramref name="certificate"/> as HC360 Desktop does, and sends the signature.</summary>
    public async Task<PrescriptionDigitalSignatureDto> SignAsync(ApiClient doctor, Guid prescriptionId, X509Certificate2 certificate)
    {
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{prescriptionId}/signing-content");
        var signature = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), certificate, DateTimeOffset.UtcNow, silent: true, issuers: [Ca]);
        return await doctor.PostAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{prescriptionId}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature)));
    }

    /// <summary>The name a DSC is issued in for a doctor HC360 calls "Dr. Asha Menon": "ASHA MENON".</summary>
    public static async Task<string> CertificateNameAsync(HospitalServer server, Guid practitionerId, CancellationToken ct)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var practitioner = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == practitionerId).Select(p => new { p.FirstName, p.LastName }).SingleAsync(ct);
        return $"{practitioner.FirstName} {practitioner.LastName}".Trim().ToUpperInvariant();
    }

    /// <summary>Sets the facility's <c>clinical.prescriptions.signature</c>, or removes it (null).</summary>
    public static async Task SetPolicyAsync(HospitalServer server, Guid facilityId, string? value, CancellationToken ct)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (value is null)
        {
            await settings.RemoveAsync(PrescriptionSignatureSettings.Key, facilityId, ct);
        }
        else
        {
            await settings.SetAsync(PrescriptionSignatureSettings.Key, value, facilityId, cancellationToken: ct);
        }
    }

    public void Dispose()
    {
        Root.Dispose();
        Ca.Dispose();
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        return serial;
    }
}
