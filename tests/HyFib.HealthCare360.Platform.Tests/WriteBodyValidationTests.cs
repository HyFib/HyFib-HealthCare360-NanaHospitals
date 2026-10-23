using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SEC-06: the sign-in, device and administration requests are checked before their handlers run, against what the
/// value can be: a password no longer than one can be set, a code of six digits, a PIN of the shape one is set in, and
/// text no longer than the column that keeps it (a longer one was a database error, a 500, before).
/// </summary>
public sealed class WriteBodyValidationTests
{
    [Fact]
    public void A_sign_in_needs_a_user_name_and_a_password_no_longer_than_one_can_be_set()
    {
        var validator = new LoginRequestValidator();

        validator.Validate(new LoginRequest("nurse.priya", new string('p', PasswordPolicy.MaxLength))).IsValid.ShouldBeTrue();
        Fields(validator.Validate(new LoginRequest("", ""))).ShouldBe(["Password", "UserName"]);
        Fields(validator.Validate(new LoginRequest(new string('u', 65), new string('p', PasswordPolicy.MaxLength + 1))))
            .ShouldBe(["Password", "UserName"]);
        Fields(validator.Validate(new LoginRequest("nurse.priya", "secret", Guid.Empty, Guid.Empty, new string('s', 129))))
            .ShouldBe(["DeviceId", "DeviceSecret", "FacilityId"]);
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData(" 123456 ", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("123 456", false)]
    [InlineData("", false)]
    public void A_code_is_six_digits_as_it_is_checked(string code, bool valid)
    {
        new MfaVerifyRequestValidator().Validate(new MfaVerifyRequest(Guid.NewGuid(), code)).IsValid.ShouldBe(valid);
        new OtpVerifyRequestValidator().Validate(new OtpVerifyRequest(Guid.NewGuid(), code)).IsValid.ShouldBe(valid);
        new MfaEnableRequestValidator().Validate(new MfaEnableRequest(code)).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void A_code_names_its_challenge() =>
        Fields(new OtpVerifyRequestValidator().Validate(new OtpVerifyRequest(Guid.Empty, "123456"))).ShouldBe(["ChallengeId"]);

    [Theory]
    [InlineData("2580", true)]
    [InlineData("135792", true)]
    [InlineData("1111", false)]
    [InlineData("1234", false)]
    [InlineData("9876", false)]
    [InlineData("456789", false)]
    [InlineData("123", false)]
    [InlineData("1357924", false)]
    [InlineData("25a0", false)]
    [InlineData("", false)]
    public void A_pin_is_four_to_six_digits_not_all_the_same_or_in_a_run(string pin, bool valid)
    {
        IdentityRules.IsAcceptablePin(pin).ShouldBe(valid);
        new SetPinRequestValidator().Validate(new SetPinRequest(pin, "the-password")).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void The_screen_is_unlocked_with_a_pin_of_its_shape_or_the_password()
    {
        var validator = new UnlockRequestValidator();

        validator.Validate(new UnlockRequest("2580", null)).IsValid.ShouldBeTrue();
        validator.Validate(new UnlockRequest(null, "the-password")).IsValid.ShouldBeTrue();
        Fields(validator.Validate(new UnlockRequest(null, null))).ShouldBe(["Password"]);
        Fields(validator.Validate(new UnlockRequest("25", null))).ShouldBe(["Pin"]);
        Fields(validator.Validate(new UnlockRequest(null, new string('p', PasswordPolicy.MaxLength + 1)))).ShouldBe(["Password"]);
    }

    [Fact]
    public void Every_password_typed_again_is_held_to_what_can_be_set()
    {
        var tooLong = new string('p', PasswordPolicy.MaxLength + 1);

        new ReauthenticateRequestValidator().Validate(new ReauthenticateRequest(tooLong)).IsValid.ShouldBeFalse();
        new ReauthenticateRequestValidator().Validate(new ReauthenticateRequest("")).IsValid.ShouldBeFalse();
        new MfaSetupRequestValidator().Validate(new MfaSetupRequest(tooLong)).IsValid.ShouldBeFalse();
        new MfaDisableRequestValidator().Validate(new MfaDisableRequest(tooLong)).IsValid.ShouldBeFalse();
        new WebAuthnRegistrationRequestValidator().Validate(new WebAuthnRegistrationRequest(tooLong)).IsValid.ShouldBeFalse();
        Fields(new ChangePasswordRequestValidator().Validate(new ChangePasswordRequest(tooLong, tooLong))).ShouldBe(["CurrentPassword", "NewPassword"]);

        // So no password can be set that sign-in would then refuse.
        new CreateUserRequestValidator().Validate(new CreateUserRequest("asha.k", "Asha K", null, null, [Guid.NewGuid()], null, null, null, tooLong))
            .Errors.Select(e => e.PropertyName).ShouldBe(["TemporaryPassword"]);
    }

    [Fact]
    public void A_devices_answer_must_be_there_to_be_checked()
    {
        var answer = JsonDocument.Parse("""{"id":"abc","response":{}}""").RootElement;

        new WebAuthnUnlockRequestValidator().Validate(new WebAuthnUnlockRequest(Guid.NewGuid(), answer)).IsValid.ShouldBeTrue();
        Fields(new WebAuthnUnlockRequestValidator().Validate(new WebAuthnUnlockRequest(Guid.Empty, default))).ShouldBe(["ChallengeId", "Credential"]);
        Fields(new WebAuthnRegisterRequestValidator().Validate(new WebAuthnRegisterRequest(Guid.NewGuid(), new string('n', 65), answer)))
            .ShouldBe(["Name"]);
    }

    [Fact]
    public void A_heartbeat_reports_the_signal_as_windows_gives_it_and_any_network_name()
    {
        var validator = new DeviceHeartbeatRequestValidator();
        DeviceHeartbeatRequest Beat(string? ssid, int? signal) => new(Guid.NewGuid(), "a-device-secret", "1.4.2", ssid, signal);

        // A hidden network's name can be zero bytes: it is held to its length only.
        validator.Validate(Beat("\0\0\0\0", 0)).IsValid.ShouldBeTrue();
        validator.Validate(Beat("Ward 4 WiFi", 100)).IsValid.ShouldBeTrue();
        validator.Validate(Beat(null, null)).IsValid.ShouldBeTrue();
        Fields(validator.Validate(Beat(new string('w', 65), 101))).ShouldBe(["WifiSignal", "WifiSsid"]);
        Fields(validator.Validate(new DeviceHeartbeatRequest(Guid.Empty, "", null, null, -1))).ShouldBe(["DeviceId", "DeviceSecret", "WifiSignal"]);
    }

    [Fact]
    public void An_enrolment_and_a_device_are_held_to_their_columns()
    {
        Fields(new CreateEnrollmentCodeRequestValidator().Validate(new CreateEnrollmentCodeRequest((DeviceKind)99, Guid.Empty, new string('n', 257), 4)))
            .ShouldBe(["FacilityId", "Kind", "Note", "ValidMinutes"]);
        new CreateEnrollmentCodeRequestValidator().Validate(new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, null, null)).IsValid.ShouldBeTrue();
        new CreateEnrollmentCodeRequestValidator().Validate(new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, null, 24 * 60 + 1)).IsValid.ShouldBeFalse();

        Fields(new EnrollDeviceRequestValidator().Validate(new EnrollDeviceRequest("", new string('n', 129), null, new string('o', 129), new string('v', 33))))
            .ShouldBe(["AppVersion", "Code", "Name", "OsVersion"]);

        var update = new UpdateDeviceRequestValidator().Validate(new UpdateDeviceRequest(new string('n', 129), null, new string('l', 129), 0));
        Fields(update).ShouldBe(["Location", "Name", "Version"]);
        update.Errors.Single(e => e.PropertyName == "Version").ErrorMessage.ShouldBe(ConcurrencyExtensions.VersionMissing);
    }

    [Fact]
    public void A_setting_holds_a_bounded_value_and_a_description_that_fits()
    {
        var validator = new SaveSettingRequestValidator();

        validator.Validate(new SaveSettingRequest(JsonValue.Create("Nana Hospitals"), null, false, "Hospital name on the letterhead")).IsValid.ShouldBeTrue();
        Fields(validator.Validate(new SaveSettingRequest(JsonValue.Create(new string('v', SaveSettingRequestValidator.MaxValueLength)), Guid.Empty, false,
            new string('d', 301)))).ShouldBe(["Description", "FacilityId", "Value"]);
    }

    [Fact]
    public void A_print_template_is_sent() =>
        Fields(new SavePrintTemplateRequestValidator().Validate(new SavePrintTemplateRequest(null, false))).ShouldBe(["Template"]);

    [Fact]
    public void A_proof_is_asked_for_a_known_action_and_a_short_subject()
    {
        var validator = new AntiAutomationChallengeRequestValidator();

        validator.Validate(new AntiAutomationChallengeRequest(AntiAutomationActions.Otp, "+91 98765 43210")).IsValid.ShouldBeTrue();
        Fields(validator.Validate(new AntiAutomationChallengeRequest("payments", new string('9', 33)))).ShouldBe(["Action", "Subject"]);
    }

    private static List<string> Fields(FluentValidation.Results.ValidationResult result) =>
        [.. result.Errors.Select(e => e.PropertyName).Distinct().Order()];
}
