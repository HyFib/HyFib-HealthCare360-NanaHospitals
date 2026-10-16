using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Portal;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Signing a prescription with the doctor's DSC over the real API (ERX-04): HC360 Desktop's part (choosing the
/// certificate, the token's PIN) is played by signing the bytes the server hands out with a throwaway DSC, as
/// <see cref="DetachedCms"/> does on the desktop. The DSC comes from a throwaway root and licensed CA
/// (<see cref="DscTestPki"/>) whose root is installed in the server's DSC trust folder, as IT installs the CCA India
/// roots, and is issued in the doctor's name. The server checks the signature, the chain and the name, keeps it, prints
/// it while it checks out, re-checks it on the public verification page and offers it as a .p7s; the hospital setting
/// decides whether it is required, and when the patient is told the prescription is ready.
/// </summary>
public sealed class PrescriptionDigitalSignatureTests(HospitalServer server) : IAsyncLifetime
{
    private const string PrescriptionShared = "prescription.shared";

    private readonly DscTestPki _pki = new();
    private Guid _facilityId;
    private string _doctorName = string.Empty;
    private string _rootFile = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var me = await (await server.SignInAsync(HospitalServer.Users.Doctor)).GetAsync<AuthUser>("/api/v1/auth/me");
        _facilityId = me.FacilityId!.Value;
        _doctorName = await DscTestPki.CertificateNameAsync(server, me.PractitionerId!.Value, Ct);
        _rootFile = await _pki.InstallAsync(server, Ct);
    }

    // The setting is the facility's, the trust folder the server's; put both back for the tests that follow.
    public async ValueTask DisposeAsync()
    {
        await SetPolicyAsync(null);
        File.Delete(_rootFile);
        _pki.Dispose();
    }

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
        status.FinalCopyAvailable.ShouldBeTrue();
        rx.FinalCopyAvailable.ShouldBeTrue();
        var document = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document");
        document.Html.ShouldContain("Signed electronically on", Case.Sensitive, "an unsigned prescription prints as before, without claiming a digital signature");
    }

    [Fact]
    public async Task The_doctor_signs_with_a_dsc_and_anyone_can_verify_it_without_seeing_the_patient()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = _pki.Doctor(_doctorName);

        // HC360 Desktop fetches the bytes and signs them with the DSC the doctor picks. The bytes name the patient, so
        // fetching them is in the PHI access audit.
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var content = Convert.FromBase64String(toSign.Content);
        toSign.Format.ShouldBe(PrescriptionCanonicalForm.Format);
        toSign.ContentHash.ShouldBe(PrescriptionCanonicalForm.Sha256(content));
        toSign.PrescriptionNumber.ShouldBe(rx.PrescriptionNumber);
        (await AuditCountAsync(rx.PatientId, $"Fetched prescription {rx.PrescriptionNumber} to sign with a DSC")).ShouldBe(1);
        var request = PrescriptionSigningRequest.Parse(toSign.Content);
        request.Medicines.ShouldBe(["Azithromycin 500 mg"], "the desktop shows the doctor what the bytes contain");
        var signature = DetachedCms.Sign(content, certificate, DateTimeOffset.UtcNow, silent: true, issuers: [_pki.Ca]);

        var attached = await doctor.PostAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature)));

        attached.State.ShouldBe(DigitalSignatureState.Valid, attached.Message);
        attached.SignerName.ShouldBe(_doctorName);
        attached.CertificateSerialNumber.ShouldBe(certificate.SerialNumber);
        attached.CertificateThumbprint.ShouldBe(certificate.Thumbprint);
        attached.ContentHash.ShouldBe(toSign.ContentHash);
        attached.ContentMatchesRecord.ShouldBeTrue();
        attached.ChainStatus.ShouldBe(CertificateChainStatus.TrustedSummary);
        attached.ChainTrusted.ShouldBeTrue();
        (await doctor.GetAsync<PrescriptionDto>($"/api/v1/clinical/prescriptions/{rx.Id}")).IsDigitallySigned.ShouldBeTrue();

        // The data audit records that the signature and the signed bytes were stored, not the bytes, which name the patient.
        var dataAudit = await DataAuditAsync(rx.Id);
        dataAudit.ShouldContain(PrescriptionCanonicalForm.Sha256(content));
        dataAudit.ShouldNotContain(Convert.ToBase64String(content));
        dataAudit.ShouldNotContain(Convert.ToBase64String(signature));

        // Signed once: a second signature is refused, and the prescription itself never changes.
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature))), HttpStatusCode.Conflict, "prescription.already_digitally_signed");
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Put, $"/api/v1/clinical/prescriptions/{rx.Id}",
            new UpdatePrescriptionRequest(null, "Changed advice", 0, [Line()], rx.Version)), HttpStatusCode.UnprocessableEntity, "prescription.not_draft");

        // The printed copy says who signed, with which certificate.
        var document = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document");
        document.Html.ShouldContain($"Digitally signed by {_doctorName}");
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
        verification.DigitalSignature.ChainTrusted.ShouldBeTrue();
        verification.DigitalSignature.ContentHash.ShouldBe(toSign.ContentHash);
        verification.DigitalSignature.CertificateSerialNumber.ShouldBe(certificate.SerialNumber);
        verification.SignaturePath.ShouldBe($"/api/v1/public/prescriptions/verify/{token}/signature.p7s");
        json.ShouldNotContain(toSign.PatientName);
        json.ShouldNotContain(toSign.PatientUhid);
        json.ShouldNotContain("Azithromycin");
        using var publicP7s = await anonymous.SendRawAsync(HttpMethod.Get, verification.SignaturePath!);
        publicP7s.Content.Headers.ContentType!.MediaType.ShouldBe("application/pkcs7-signature");
        (await publicP7s.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(signature);

        // A change behind HC360's back (straight in the database) shows on the next check, and the printed copy no
        // longer claims the DSC signature.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PrescriptionItem>().Where(i => i.PrescriptionId == rx.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Quantity, 300m), Ct);
        }

        var tampered = await anonymous.GetAsync<PrescriptionVerification>($"/api/v1/public/prescriptions/verify/{token}");
        tampered.DigitalSignature!.State.ShouldBe(DigitalSignatureState.Invalid);
        (await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature")).ContentMatchesRecord.ShouldBeFalse();
        var reprinted = (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document")).Html;
        reprinted.ShouldNotContain("Digitally signed by");
        reprinted.ShouldContain("Signed electronically on");
    }

    [Fact]
    public async Task A_signature_over_other_bytes_or_from_another_doctor_is_refused()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = _pki.Doctor(_doctorName);
        var other = DetachedCms.Sign(Encoding.UTF8.GetBytes("{\"format\":\"hc360.prescription.v1\"}"), certificate, DateTimeOffset.UtcNow, silent: true);

        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(other))), HttpStatusCode.UnprocessableEntity, "prescription.signature_invalid");

        // Signed long ago (a replayed signature, or a workstation clock far off).
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var stale = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), certificate, DateTimeOffset.UtcNow.AddHours(-2), silent: true, issuers: [_pki.Ca]);
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(stale))), HttpStatusCode.UnprocessableEntity, "prescription.signature_time");

        // A colleague's DSC, or the hospital's own Class 3 certificate on a shared workstation: a real DSC, not this doctor's.
        foreach (var name in new[] { "SOME OTHER DOCTOR", "NANA HOSPITALS PRIVATE LIMITED" })
        {
            using var notTheirs = _pki.Doctor(name);
            var lent = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), notTheirs, DateTimeOffset.UtcNow, silent: true, issuers: [_pki.Ca]);
            await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
                new AttachDigitalSignatureRequest(Convert.ToBase64String(lent))), HttpStatusCode.UnprocessableEntity, "prescription.signer_mismatch");
        }

        // A certificate whose subject is longer than HC360 keeps is refused with a reason, not a server error.
        using var oversized = _pki.Doctor(_doctorName, "OU=" + new string('A', Prescription.SignerDistinguishedNameMaxLength));
        var tooLong = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), oversized, DateTimeOffset.UtcNow, silent: true, issuers: [_pki.Ca]);
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(tooLong))), HttpStatusCode.UnprocessableEntity, "prescription.certificate_unsupported");

        (await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature")).State
            .ShouldBe(DigitalSignatureState.NotSigned);
    }

    [Fact]
    public async Task Only_the_prescribing_doctor_signs_with_their_dsc_not_another_doctor_who_may_sign_prescriptions()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);

        // A second doctor, with clinical.prescriptions.sign, but not this prescription's prescriber.
        var colleague = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var me = await colleague.GetAsync<AuthUser>("/api/v1/auth/me");
        me.Permissions.ShouldContain("clinical.prescriptions.sign");
        me.PractitionerId.ShouldNotBeNull().ShouldNotBe(rx.PractitionerId);
        await ShouldRefuseAsync(colleague.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/signing-content"),
            HttpStatusCode.Forbidden, "prescription.not_prescriber");
        using var colleaguesDsc = _pki.Doctor(await DscTestPki.CertificateNameAsync(server, me.PractitionerId.Value, Ct));
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var signature = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), colleaguesDsc, DateTimeOffset.UtcNow, silent: true, issuers: [_pki.Ca]);
        await ShouldRefuseAsync(colleague.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature))), HttpStatusCode.Forbidden, "prescription.not_prescriber");

        // A nurse cannot even ask: signing is not her permission.
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
        var phone = NewMobile();
        var patient = await RegisterPatientAsync(phone);
        var rx = await SignedPrescriptionAsync(doctor, patient.Id);
        rx.FinalCopyAvailable.ShouldBeFalse();

        foreach (var path in new[] { "print", "document", "pdf" })
        {
            await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/{path}"),
                HttpStatusCode.UnprocessableEntity, PrescriptionFinalCopies.RequiredCode);
        }

        // The patient is not told yet, and the portal neither prints it nor shows its QR code; its PDF is refused in
        // words for the patient.
        (await NoticesAsync(patient.Id)).ShouldBeEmpty("the patient hears of it once the doctor has signed it with their DSC");
        var portal = await server.SignInToPortalAsync(phone);
        var onPortal = await portal.GetAsync<PortalPrescriptionDetail>($"/api/v1/portal/prescriptions/{rx.Id}");
        onPortal.FinalCopyAvailable.ShouldBeFalse();
        onPortal.DigitalSignatureState.ShouldBe(DigitalSignatureState.NotSigned);
        onPortal.VerificationToken.ShouldBeNull();
        onPortal.VerificationUrl.ShouldBeNull();
        using (var pdf = await portal.SendRawAsync(HttpMethod.Get, $"/api/v1/portal/prescriptions/{rx.Id}/pdf"))
        {
            var body = await pdf.Content.ReadAsStringAsync(Ct);
            pdf.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
            body.ShouldContain(PrescriptionFinalCopies.RequiredCode);
            body.ShouldContain("Your doctor has not digitally signed this prescription yet.");
            body.ShouldNotContain("HC360 Desktop");
        }

        using var certificate = _pki.Doctor(_doctorName);
        (await _pki.SignAsync(doctor, rx.Id, certificate)).Policy.ShouldBe(PrescriptionSignaturePolicy.Required);

        (await doctor.GetAsync<PrescriptionDto>($"/api/v1/clinical/prescriptions/{rx.Id}")).FinalCopyAvailable.ShouldBeTrue();
        (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document")).Html.ShouldContain("Digitally signed by");
        (await NoticesAsync(patient.Id)).ShouldBe([NotificationChannel.WhatsApp], "told once, now, with the signed copy");
        var issued = await portal.GetAsync<PortalPrescriptionDetail>($"/api/v1/portal/prescriptions/{rx.Id}");
        issued.FinalCopyAvailable.ShouldBeTrue();
        issued.VerificationToken.ShouldNotBeNull();
        using (var pdf = await portal.SendRawAsync(HttpMethod.Get, $"/api/v1/portal/prescriptions/{rx.Id}/pdf"))
        {
            pdf.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable); // 503 where this machine cannot make PDFs
        }
    }

    [Fact]
    public async Task A_patient_told_at_signing_is_not_told_again_when_the_doctor_adds_a_dsc()
    {
        // Told at signing where the DSC is optional; the hospital then requires it; the doctor signs with it.
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterPatientAsync(NewMobile());
        var rx = await SignedPrescriptionAsync(doctor, patient.Id);
        (await NoticesAsync(patient.Id)).ShouldBe([NotificationChannel.WhatsApp]);

        await SetPolicyAsync("required");
        using var certificate = _pki.Doctor(_doctorName);
        (await _pki.SignAsync(doctor, rx.Id, certificate)).State.ShouldBe(DigitalSignatureState.Valid);

        (await NoticesAsync(patient.Id)).ShouldBe([NotificationChannel.WhatsApp], "once only");

        // The other way round: not told at signing where it is required, and told when the DSC is added, even though the
        // hospital has made it optional since.
        var later = await SignedPrescriptionAsync(doctor, patient.Id);
        await SetPolicyAsync("optional");
        (await _pki.SignAsync(doctor, later.Id, certificate)).State.ShouldBe(DigitalSignatureState.Valid);
        (await NoticesAsync(patient.Id)).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public async Task A_certificate_that_is_not_a_dsc_is_refused_and_does_not_unlock_the_print(string policy)
    {
        await SetPolicyAsync(policy);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);

        // Anyone signed in as the doctor can make one: New-SelfSignedCertificate -KeyUsage DigitalSignature -Subject 'CN=…'.
        using var selfSigned = Certificate(_doctorName);
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var signature = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), selfSigned, DateTimeOffset.UtcNow, silent: true);
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(signature))), HttpStatusCode.UnprocessableEntity, "prescription.signature_untrusted");

        // A certificate from another hierarchy (a domain's enterprise CA, a public CA) is no better.
        using var otherPki = new DscTestPki("Enterprise Root CA");
        using var enterprise = otherPki.Doctor(_doctorName);
        var enterpriseSignature = DetachedCms.Sign(Convert.FromBase64String(toSign.Content), enterprise, DateTimeOffset.UtcNow, silent: true,
            issuers: [otherPki.Ca, otherPki.Root]);
        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature",
            new AttachDigitalSignatureRequest(Convert.ToBase64String(enterpriseSignature))), HttpStatusCode.UnprocessableEntity, "prescription.signature_untrusted");

        (await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature")).State
            .ShouldBe(DigitalSignatureState.NotSigned);
        if (policy == "required")
        {
            await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/document"),
                HttpStatusCode.UnprocessableEntity, PrescriptionFinalCopies.RequiredCode);
        }
    }

    [Fact]
    public async Task Without_the_cca_roots_installed_dsc_signing_is_refused_before_the_doctor_enters_a_pin()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        File.Delete(_rootFile);

        await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/signing-content"),
            HttpStatusCode.UnprocessableEntity, "prescription.signature_not_set_up");

        // System health says so, as it would after a restore or a failover that did not bring the roots back.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var warning = (await admin.GetAsync<HealthWarnings>("/api/v1/admin/system/health")).Warnings.Where(w => w.Code == "prescriptions.dsc_trust_empty")
            .ShouldHaveSingleItem();
        warning.Severity.ShouldBe("warning");
        await SetPolicyAsync("required");
        (await admin.GetAsync<HealthWarnings>("/api/v1/admin/system/health")).Warnings.Single(w => w.Code == "prescriptions.dsc_trust_empty").Severity
            .ShouldBe("critical");

        await _pki.InstallAsync(server, Ct);
        (await admin.GetAsync<HealthWarnings>("/api/v1/admin/system/health")).Warnings.ShouldNotContain(w => w.Code == "prescriptions.dsc_trust_empty");
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public async Task A_signature_whose_root_was_removed_since_is_no_longer_shown_or_printed_as_a_valid_dsc(string policy)
    {
        await SetPolicyAsync(policy);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = _pki.Doctor(_doctorName);
        (await _pki.SignAsync(doctor, rx.Id, certificate)).State.ShouldBe(DigitalSignatureState.Valid);

        File.Delete(_rootFile);

        var status = await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature");
        status.State.ShouldBe(DigitalSignatureState.Untrusted);
        status.ChainTrusted.ShouldBeFalse();
        var verification = await server.Anonymous(ownAddress: true).GetAsync<PrescriptionVerification>($"/api/v1/public/prescriptions/verify/{await TokenAsync(rx.Id)}");
        verification.DigitalSignature!.State.ShouldBe(DigitalSignatureState.Untrusted);
        verification.DigitalSignature.ChainTrusted.ShouldBeFalse();

        // Where it is optional the prescription prints as signed in HC360, not as signed with a DSC; where it is
        // required, an unconfirmed DSC counts as none and nothing is issued.
        if (policy == "optional")
        {
            var html = (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document")).Html;
            html.ShouldNotContain("Digitally signed by");
            html.ShouldNotContain(certificate.SerialNumber);
            html.ShouldContain("Signed electronically on");
        }
        else
        {
            status.FinalCopyAvailable.ShouldBeFalse();
            foreach (var path in new[] { "print", "document", "pdf" })
            {
                await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/{path}"),
                    HttpStatusCode.UnprocessableEntity, PrescriptionFinalCopies.UntrustedCode);
            }
        }
    }

    [Fact]
    public async Task Where_the_dsc_is_required_a_prescription_changed_after_signing_is_not_issued()
    {
        await SetPolicyAsync("required");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = _pki.Doctor(_doctorName);
        (await _pki.SignAsync(doctor, rx.Id, certificate)).State.ShouldBe(DigitalSignatureState.Valid);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PrescriptionItem>().Where(i => i.PrescriptionId == rx.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Quantity, 300m), Ct);
        }

        foreach (var path in new[] { "print", "document", "pdf" })
        {
            await ShouldRefuseAsync(doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/clinical/prescriptions/{rx.Id}/{path}"),
                HttpStatusCode.UnprocessableEntity, PrescriptionFinalCopies.InvalidCode);
        }

        var status = await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature");
        status.State.ShouldBe(DigitalSignatureState.Invalid);
        status.ContentMatchesRecord.ShouldBeFalse();
        status.FinalCopyAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task The_printed_copy_names_the_registration_number_the_doctor_signed()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var rx = await SignedPrescriptionAsync(doctor);
        using var certificate = _pki.Doctor(_doctorName);
        var toSign = await doctor.GetAsync<PrescriptionSigningContent>($"/api/v1/clinical/prescriptions/{rx.Id}/signing-content");
        var signed = PrescriptionCanonicalForm.ReadParties(Convert.FromBase64String(toSign.Content));
        (await _pki.SignAsync(doctor, rx.Id, certificate)).State.ShouldBe(DigitalSignatureState.Valid);

        // The doctor's registration is changed in the master afterwards.
        string? before;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            before = await db.Set<Practitioner>().Where(p => p.Id == rx.PractitionerId).Select(p => p.RegistrationNumber).SingleAsync(Ct);
            await db.Set<Practitioner>().Where(p => p.Id == rx.PractitionerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RegistrationNumber, "CHANGED-0001"), Ct);
        }

        try
        {
            var html = (await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/clinical/prescriptions/{rx.Id}/document")).Html;
            html.ShouldNotContain("CHANGED-0001");
            if (signed.RegistrationNumber is { Length: > 0 } registration)
            {
                html.ShouldContain($"Digitally signed by {_doctorName} ({registration})");
            }
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Practitioner>().Where(p => p.Id == rx.PractitionerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RegistrationNumber, before), Ct);
        }
    }

    [Fact]
    public async Task Merging_the_patients_duplicate_record_leaves_the_signature_valid()
    {
        await SetPolicyAsync("optional");
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var duplicate = await RegisterPatientAsync(NewMobile());
        var kept = await RegisterPatientAsync(NewMobile());
        var rx = await SignedPrescriptionAsync(doctor, duplicate.Id);
        using var certificate = _pki.Doctor(_doctorName);
        (await _pki.SignAsync(doctor, rx.Id, certificate)).State.ShouldBe(DigitalSignatureState.Valid);

        // Front office finds the patient was registered twice; the merge moves the prescription to the kept record.
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        await mrd.PostAsync($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(kept.Id, "Registered twice"));
        (await doctor.GetAsync<PrescriptionDto>($"/api/v1/clinical/prescriptions/{rx.Id}")).PatientId.ShouldBe(kept.Id);

        var status = await doctor.GetAsync<PrescriptionDigitalSignatureDto>($"/api/v1/clinical/prescriptions/{rx.Id}/digital-signature");
        status.State.ShouldBe(DigitalSignatureState.Valid, status.Message);
        status.ContentMatchesRecord.ShouldBeTrue();
        var token = await TokenAsync(rx.Id);
        var anonymous = server.Anonymous(ownAddress: true);
        (await anonymous.GetAsync<PrescriptionVerification>($"/api/v1/public/prescriptions/verify/{token}")).DigitalSignature!.State
            .ShouldBe(DigitalSignatureState.Valid);

        // Moving it to someone else's record behind HC360's back is a change.
        var stranger = await RegisterPatientAsync(NewMobile());
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Prescription>().IgnoreQueryFilters().Where(p => p.Id == rx.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PatientId, stranger.Id), Ct);
        }

        (await anonymous.GetAsync<PrescriptionVerification>($"/api/v1/public/prescriptions/verify/{token}")).DigitalSignature!.State
            .ShouldBe(DigitalSignatureState.Invalid);
    }

    private Task SetPolicyAsync(string? value) => DscTestPki.SetPolicyAsync(server, _facilityId, value, Ct);

    private async Task<PrescriptionDto> SignedPrescriptionAsync(ApiClient doctor, Guid? patientId = null)
    {
        patientId ??= (await RegisterPatientAsync(NewMobile())).Id;
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId.Value, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null,
            Advice: "Complete the course.", RefillsAllowed: 0, [Line()]));
        return (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version))).Prescription;
    }

    private async Task<PatientCreated> RegisterPatientAsync(string phone)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Lata", MiddleName: null, LastName: "Dsc" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 3, 14), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));

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

    /// <summary>The channel of each "prescription shared" message queued for the patient.</summary>
    private async Task<List<NotificationChannel>> NoticesAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .Where(l => l.PatientId == patientId && l.TemplateKey == PrescriptionShared)
            .Select(l => l.Channel)
            .ToListAsync(Ct);
    }

    private async Task<int> AuditCountAsync(Guid patientId, string summary)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .CountAsync(e => e.PatientId == patientId && e.Category == AuditCategories.PhiAccess && e.Summary == summary, Ct);
    }

    /// <summary>The details of the data audit's updates of the prescription.</summary>
    private async Task<string> DataAuditAsync(Guid prescriptionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var details = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Data && e.EntityId == prescriptionId.ToString() && e.Action == "Prescription.update")
            .Select(e => e.Details)
            .ToListAsync(Ct);
        return string.Join('\n', details);
    }

    private static async Task ShouldRefuseAsync(Task<HttpResponseMessage> call, HttpStatusCode status, string code)
    {
        using var response = await call;
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        body.ShouldContain(code);
    }

    /// <summary>A self-signed certificate that may sign: what anyone can make on a Windows PC, and not a DSC.</summary>
    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, O=Personal, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private sealed record HealthWarnings(IReadOnlyList<HealthWarning> Warnings);
}
