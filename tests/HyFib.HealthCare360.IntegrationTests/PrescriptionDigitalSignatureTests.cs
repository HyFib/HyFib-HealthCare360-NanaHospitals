using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Signing a prescription with the doctor's DSC over the real API (ERX-04): HC360 Desktop's part (choosing the
/// certificate, the token's PIN) is played by signing the bytes the server hands out with a throwaway certificate, as
/// <see cref="DetachedCms"/> does on the desktop. The server checks the signature, keeps it, prints it, re-checks it on
/// the public verification page and offers it as a .p7s; the hospital setting decides whether it is required.
/// </summary>
public sealed class PrescriptionDigitalSignatureTests(HospitalServer server) : IAsyncLifetime
{
    private Guid _facilityId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _facilityId = (await (await server.SignInAsync(HospitalServer.Users.Doctor)).GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId!.Value;

    // The setting is the facility's; put it back to the default for the tests that follow.
    public async ValueTask DisposeAsync() => await SetPolicyAsync(null);

    [Fact]
    public async Task Without_the_setting_nothing_changes_and_no_dsc_signing_is_offered()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);

        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/signing-content"),
            HttpStatusCode.UnprocessableEntity, "prescription.signature_off");
        var status = await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature");
        status.State.ShouldBe(DigitalSignatureState.NotSigned);
        status.Policy.ShouldBe(PrescriptionSignaturePolicy.Off);
        var document = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document");
        document.Html.ShouldContain("Signed electronically on", Case.Sensitive, "an unsigned prescription prints as before, without claiming a digital signature");
    }

    [Fact]
    public async Task The_doctor_signs_with_a_dsc_and_anyone_can_verify_it_without_seeing_the_patient()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = Certificate();

        // HC360 Desktop fetches the bytes and signs them with the DSC the doctor picks.
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var content = Convert.FromBase64String(toSign.Content);
        toSign.Format.ShouldBe(PrescriptionCanonicalForm.Format);
        toSign.ContentHash.ShouldBe(PrescriptionCanonicalForm.Sha256(content));
        toSign.PrescriptionNumber.ShouldBe(rx.PrescriptionNumber);
        var request = PrescriptionSigningRequest.Parse(toSign.Content);
        request.Medicines.ShouldBe(["Azithromycin 500 mg"], "the desktop shows the doctor what the bytes contain");
        var signature = DetachedCms.Sign(content, certificate, DateTimeOffset.UtcNow, silent: true);

        var attached = await doctor.PostAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature)));

        attached.State.ShouldBe(DigitalSignatureState.Valid, attached.Message);
        attached.SignerName.ShouldBe("Dr Test Prescriber");
        attached.CertificateSerialNumber.ShouldBe(certificate.SerialNumber);
        attached.CertificateThumbprint.ShouldBe(certificate.Thumbprint);
        attached.ContentHash.ShouldBe(toSign.ContentHash);
        attached.ContentMatchesRecord.ShouldBeTrue();
        attached.ChainStatus.ShouldNotBe("Trusted", "a self-signed certificate is reported as untrusted, and still accepted");
        (await doctor.GetAsync<PrescriptionDto>($"/api/v1/clinical/prescriptions/{rx.Id}")).IsDigitallySigned.ShouldBeTrue();

        // Signed once: a second signature is refused, and the prescription itself never changes.
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature))), HttpStatusCode.Conflict, "prescription.already_digitally_signed");
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Put, $"/api/v1/clinical/prescriptions/{rx.Id}",
            new UpdatePrescriptionRequest(null, "Changed advice", 0, [Line()], rx.Version)), HttpStatusCode.UnprocessableEntity, "prescription.not_draft");

        // The printed copy says who signed, with which certificate.
        var document = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document");
        document.Html.ShouldContain($"Digitally signed by Dr Test Prescriber");
        document.Html.ShouldContain($"certificate {certificate.SerialNumber}");
        document.Html.ShouldContain(toSign.ContentHash);

        // The chart offers the .p7s and the signed bytes, which check out on their own.
        var p7s = await (await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/signature.p7s")).Content.ReadAsByteArrayAsync(Ct);
        var signedContent = await (await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/signed-content")).Content.ReadAsByteArrayAsync(Ct);
        p7s.ShouldBe(signature);
        signedContent.ShouldBe(content);
        DetachedCms.Verify(signedContent, p7s).IsValid.ShouldBeTrue();

        // The QR page: the prescription and the signature, re-checked, and nothing about the patient.
        var token = await TokenAsync(rx.Id);
        var anonymous = server.Anonymous(ownAddress: true);
        using var answer = await anonymous.SendRawAsync(HttpMethod.Get, $"/api/v1/public/prescriptions/verify/{token}");
        var json = await answer.Content.ReadAsStringAsync(Ct);
        answer.StatusCode.ShouldBe(HttpStatusCode.OK, json);
        var verification = System.Text.Json.JsonSerializer.Deserialize<PrescriptionVerification>(json, ApiClient.Json)!;
        verification.PrescriptionNumber.ShouldBe(rx.PrescriptionNumber);
        verification.DigitalSignature.ShouldNotBeNull().State.ShouldBe(DigitalSignatureState.Valid);
        verification.DigitalSignature.ContentHash.ShouldBe(toSign.ContentHash);
        verification.DigitalSignature.CertificateSerialNumber.ShouldBe(certificate.SerialNumber);
        verification.SignaturePath.ShouldBe($"/api/v1/public/prescriptions/verify/{token}/signature.p7s");
        json.ShouldNotContain(toSign.PatientName);
        json.ShouldNotContain(toSign.PatientUhid);
        json.ShouldNotContain("Azithromycin");
        using var publicP7s = await anonymous.SendRawAsync(HttpMethod.Get, verification.SignaturePath!);
        publicP7s.Content.Headers.ContentType!.MediaType.ShouldBe("application/pkcs7-signature");
        (await publicP7s.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(signature);

        // A change behind HC360's back (straight in the database) shows on the next check.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PrescriptionItem>().Where(i => i.PrescriptionId == rx.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Quantity, 300m), Ct);
        }

        var tampered = await anonymous.GetAsync<PrescriptionVerification>($"/api/v1/public/prescriptions/verify/{token}");
        tampered.DigitalSignature!.State.ShouldBe(DigitalSignatureState.Invalid);
        (await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature")).ContentMatchesRecord.ShouldBeFalse();
    }

    [Fact]
    public async Task A_signature_over_other_bytes_or_from_another_doctor_is_refused()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = Certificate();
        var other = DetachedCms.Sign(Encoding.UTF8.GetBytes("{\"format\":\"hc360.prescription.v1\"}"), certificate, DateTimeOffset.UtcNow, silent: true);

        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(other))), HttpStatusCode.UnprocessableEntity, "prescription.signature_invalid");

        // Signed long ago (a replayed signature, or a workstation clock far off).
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var stale = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), certificate, DateTimeOffset.UtcNow.AddHours(-2), silent: true);
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(stale))), HttpStatusCode.UnprocessableEntity, "prescription.signature_time");

        // Only a prescriber signs; a nurse cannot even fetch the bytes.
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        using var forbidden = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature")).State
            .ShouldBe(DigitalSignatureState.NotSigned);
    }

    [Fact]
    public async Task Where_the_dsc_is_required_the_prescription_is_printed_and_shared_only_once_signed_with_it()
    {
        await SetPolicyAsync("required");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);

        foreach (var path in new[] { "print", "document", "pdf" })
        {
            await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/{path}"),
                HttpStatusCode.UnprocessableEntity, "prescription.digital_signature_required");
        }

        using var certificate = Certificate();
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var signature = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), certificate, DateTimeOffset.UtcNow, silent: true);
        (await doctor.PostAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature)))).Policy.ShouldBe(PrescriptionSignaturePolicy.Required);

        (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document")).Html.ShouldContain("Digitally signed by");
    }

    private async Task SetPolicyAsync(string? value)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (value is null)
        {
            await settings.RemoveAsync(PrescriptionSignatureSettings.Key, _facilityId, Ct);
        }
        else
        {
            await settings.SetAsync(PrescriptionSignatureSettings.Key, value, _facilityId, cancellationToken: Ct);
        }
    }

    private async Task<PrescriptionDto> SignedPrescriptionAsync(ApiClient doctor)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Lata", MiddleName: null, LastName: "Dsc" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 3, 14), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null,
            Advice: "Complete the course.", RefillsAllowed: 0, [Line()]));
        return (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version))).Prescription;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static PrescriptionItemRequest Line() => new(
        DrugItemId: null, "Azithromycin", "Azithromycin", "500 mg", "Tablet", "Oral", "1 tablet", DoseFrequency.OD, CustomFrequency: null,
        DosesPerDay: null, DurationDays: 3, Quantity: 3m, Instructions: "One hour before food", IsControlledDrug: false, ControlledSchedule: null);

    private async Task<string> TokenAsync(Guid prescriptionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Prescription>().IgnoreQueryFilters()
            .Where(p => p.Id == prescriptionId).Select(p => p.VerificationToken!).SingleAsync(Ct);
    }

    private static async Task ShouldRefuseAsync(Task<HttpResponseMessage> call, HttpStatusCode status, string code)
    {
        using var response = await call;
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        body.ShouldContain(code);
    }

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dr Test Prescriber, O=Personal, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
