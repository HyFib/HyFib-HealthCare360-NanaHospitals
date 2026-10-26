using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Checking a patient's ABHA with ABDM at the desk (ABDM M1) against a fake ABHA service: it publishes a public key,
/// decrypts the login id and code HC360 encrypts with it (RSA-OAEP, SHA-1), sends a "code" for the ABHA number on the
/// record, and answers the right code with the ABHA account. The account matches the record (verified) or not (what
/// differs is listed); a wrong code is counted and three end the check; staff without the identity permission cannot
/// start one; a patient without an ABHA number cannot be checked by it.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbhaVerificationTests(HospitalServer server)
{
    private const string Code = "246810";

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task The_abha_number_on_record_is_checked_with_a_code_and_compared_with_the_record()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var abha = AbdmExportTests.NewAbha();
        var registration = AbdmExportTests.Registration(abha, null);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", registration);
        var noAbha = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), null) with { AbhaNumber = null });
        using var key = RSA.Create(2048);
        var seen = new List<(string Path, string LoginId)>();
        var accountName = $"Meera {registration.LastName}";
        var accountYear = registration.DateOfBirth!.Value.Year;
        Gateway.Answer(call => AbhaService(call, key, seen, abha, () => (accountName, accountYear)));

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // Only staff who check identities may start a check; a patient without an ABHA number cannot be checked by it.
            using (var forbidden = await doctor.SendRawAsync(HttpMethod.Post, Checks(patient.Id), new AbhaVerificationStart(AbhaLoginHint.AbhaNumber)))
            {
                forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var none = await frontOffice.SendRawAsync(HttpMethod.Post, Checks(noAbha.Id), new AbhaVerificationStart(AbhaLoginHint.AbhaNumber)))
            {
                none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            }

            // ABDM is asked for a code for the ABHA number on the record, encrypted with its key.
            var started = await frontOffice.PostAsync<AbhaVerificationView>(Checks(patient.Id), new AbhaVerificationStart(AbhaLoginHint.AbhaNumber));
            started.Status.ShouldBe(AbhaVerificationStatus.CodeSent);
            started.SentMessage.ShouldNotBeNull().ShouldContain("******3210");
            seen.ShouldContain((AbhaClient.RequestOtpPath, abha));
            var request = Gateway.CallsTo("/abha" + AbhaClient.RequestOtpPath).Last();
            request.Header("Authorization").ShouldBe("Bearer it-session");
            request.Header("X-CM-ID").ShouldBe("sbx");
            var body = request.Json();
            body.GetProperty("loginHint").GetString().ShouldBe("abha-number");
            body.GetProperty("otpSystem").GetString().ShouldBe("abdm");
            body.GetProperty("scope").EnumerateArray().Select(s => s.GetString()).ShouldBe(["abha-login", "mobile-verify"]);

            // A wrong code is refused and counted; the right one opens the ABHA, which is the patient on record.
            using (var wrong = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/abha-checks/{started.Id}/code", new AbhaVerificationCode("111111")))
            {
                wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("2 tries left");
            }

            var verified = await frontOffice.PostAsync<AbhaVerificationView>($"/api/v1/abdm/abha-checks/{started.Id}/code", new AbhaVerificationCode(Code));
            verified.Status.ShouldBe(AbhaVerificationStatus.Verified);
            verified.Differences.ShouldBeEmpty();
            verified.AbhaNumberMasked.ShouldBe(Abha.Mask(abha));
            verified.AbhaAddress.ShouldBe("meera.k@sbx");
            verified.KycVerified.ShouldBe(true);
            seen.ShouldContain((AbhaClient.VerifyPath, Code));
            using (var again = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/abha-checks/{started.Id}/code", new AbhaVerificationCode(Code)))
            {
                again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            // An ABHA whose name and year of birth differ from the record: what differs is listed, the record unchanged.
            accountName = "Lakshmi Iyer";
            accountYear -= 7;
            var second = await frontOffice.PostAsync<AbhaVerificationView>(Checks(patient.Id), new AbhaVerificationStart(AbhaLoginHint.AbhaNumber));
            var mismatch = await frontOffice.PostAsync<AbhaVerificationView>($"/api/v1/abdm/abha-checks/{second.Id}/code", new AbhaVerificationCode(Code));
            mismatch.Status.ShouldBe(AbhaVerificationStatus.Mismatch);
            mismatch.Differences.ShouldBe([$"The name on the ABHA is Lakshmi Iyer.", $"The year of birth on the ABHA is {accountYear}."]);

            // Three wrong codes end a check.
            var third = await frontOffice.PostAsync<AbhaVerificationView>(Checks(patient.Id), new AbhaVerificationStart(AbhaLoginHint.AbhaNumber));
            for (var i = 0; i < AbdmAbhaVerification.MaxAttempts; i++)
            {
                using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/abha-checks/{third.Id}/code", new AbhaVerificationCode("000000"));
                refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            }

            // A fourth code within fifteen minutes is not sent.
            using (var tooMany = await frontOffice.SendRawAsync(HttpMethod.Post, Checks(patient.Id), new AbhaVerificationStart(AbhaLoginHint.AbhaNumber)))
            {
                tooMany.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            }

            var history = await doctor.GetAsync<List<AbhaVerificationView>>(Checks(patient.Id));
            history.Select(h => h.Status).ShouldBe([AbhaVerificationStatus.Failed, AbhaVerificationStatus.Mismatch, AbhaVerificationStatus.Verified]);
        }
        finally
        {
            Gateway.Answer(null);
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    private static string Checks(Guid patientId) => $"/api/v1/abdm/patients/{patientId}/abha-checks";

    /// <summary>The fake ABHA service under <c>/abha</c>: its key, a code for any ABHA, and the account for the right code.</summary>
    private static HttpResponseMessage? AbhaService(
        GatewayCall call, RSA key, List<(string Path, string LoginId)> seen, string abha, Func<(string Name, int Year)> account)
    {
        if (!call.Path.StartsWith("/abha/", StringComparison.Ordinal))
        {
            return null;
        }

        var path = call.Path["/abha".Length..];
        string Decrypt(string value) => Encoding.UTF8.GetString(key.Decrypt(Convert.FromBase64String(value), RSAEncryptionPadding.OaepSHA1));

        switch (path)
        {
            case AbhaClient.CertificatePath:
                return FakeAbdmGateway.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }));
            case AbhaClient.RequestOtpPath:
                seen.Add((path, Decrypt(call.Json().GetProperty("loginId").GetString()!)));
                return FakeAbdmGateway.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    txnId = Guid.NewGuid().ToString(),
                    message = "OTP sent to mobile number ending with ******3210",
                }));
            case AbhaClient.VerifyPath:
                var code = Decrypt(call.Json().GetProperty("authData").GetProperty("otp").GetProperty("otpValue").GetString()!);
                seen.Add((path, code));
                if (code != Code)
                {
                    return FakeAbdmGateway.Json(HttpStatusCode.OK, """{"authResult":"failed","message":"Entered OTP is incorrect."}""");
                }

                var (name, year) = account();
                return FakeAbdmGateway.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    txnId = Guid.NewGuid().ToString(),
                    authResult = "success",
                    message = "OTP verified successfully",
                    token = "x-token-not-kept",
                    accounts = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["ABHANumber"] = Abha.Format(abha),
                            ["preferredAbhaAddress"] = "meera.k@sbx",
                            ["name"] = name,
                            ["gender"] = "F",
                            ["dob"] = $"01-01-{year}",
                            ["status"] = "ACTIVE",
                            ["kycVerified"] = true,
                        },
                    },
                }));
            default:
                return null;
        }
    }

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }
}
