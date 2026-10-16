using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// ABHA identifiers (SRS EHR-08): the 14-digit ABHA number ends in a Verhoeff check digit, so a mistyped or swapped
/// digit is caught at the desk; it is shown as xx-xxxx-xxxx-xxxx, and masked as XX-XXXX-XXXX-1234 where the full number
/// is not needed. The ABHA address is a handle at @abdm (@sbx on the ABDM sandbox).
/// </summary>
public sealed class HealthIdTests
{
    [Theory]
    [InlineData("234567890124")] // a valid Aadhaar number
    [InlineData("91123456789010")]
    [InlineData("22334455667783")]
    [InlineData("2363")] // the Verhoeff paper's example: 236 with check digit 3
    public void Verhoeff_accepts_numbers_with_their_check_digit(string digits) => Verhoeff.IsValid(digits).ShouldBeTrue();

    [Theory]
    [InlineData("91123456789012")] // wrong check digit
    [InlineData("91123456789001")] // last two digits swapped
    [InlineData("19123456789010")] // first two digits swapped
    [InlineData("9112345678901O")] // a letter O for the zero
    [InlineData("0")]
    [InlineData("")]
    public void Verhoeff_rejects_mistyped_numbers(string digits) => Verhoeff.IsValid(digits).ShouldBeFalse();

    [Theory]
    [InlineData("236", '3')]
    [InlineData("9112345678901", '0')]
    [InlineData("2233445566778", '3')]
    public void Verhoeff_computes_the_check_digit(string digits, char check)
    {
        Verhoeff.CheckDigit(digits).ShouldBe(check);
        Verhoeff.IsValid(digits + check).ShouldBeTrue();
    }

    [Fact]
    public void Every_single_digit_error_is_caught()
    {
        const string Valid = "91123456789010";
        for (var position = 0; position < Valid.Length; position++)
        {
            for (var digit = '0'; digit <= '9'; digit++)
            {
                if (digit != Valid[position])
                {
                    Verhoeff.IsValid(string.Concat(Valid.AsSpan(0, position), digit.ToString(), Valid.AsSpan(position + 1))).ShouldBeFalse();
                }
            }
        }
    }

    [Theory]
    [InlineData("91123456789010")]
    [InlineData("91-1234-5678-9010")]
    [InlineData("91 1234 5678 9010")]
    [InlineData(" 91-1234-5678-9010 ")]
    public void An_abha_number_is_14_digits_with_a_valid_check_digit(string value) => Abha.IsValidNumber(value).ShouldBeTrue();

    [Theory]
    [InlineData("91123456789012")] // fails the check digit
    [InlineData("9112345678901")] // 13 digits
    [InlineData("911234567890100")] // 15 digits
    [InlineData("91/1234/5678/9010")] // other separators
    [InlineData("91-1234-5678-901O")] // a letter
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_an_abha_number(string? value) => Abha.IsValidNumber(value).ShouldBeFalse();

    [Fact]
    public void An_abha_number_is_shown_grouped_and_masked()
    {
        Abha.Format("91123456789010").ShouldBe("91-1234-5678-9010");
        Abha.Format("91 1234 5678 9010").ShouldBe("91-1234-5678-9010");
        Abha.Mask("91-1234-5678-9010").ShouldBe("XX-XXXX-XXXX-9010");
        Abha.Digits("91-1234 5678-9010").ShouldBe("91123456789010");
    }

    [Theory]
    [InlineData("meera.k@abdm")]
    [InlineData("Meera_K@ABDM")]
    [InlineData("meera1984@sbx")]
    [InlineData("rk7@abdm")]
    public void An_abha_address_is_a_handle_at_abdm_or_the_sandbox(string value) => Abha.IsValidAddress(value).ShouldBeTrue();

    [Theory]
    [InlineData("meera.k@gmail.com")] // an e-mail address
    [InlineData("meera.k@ndhm")]
    [InlineData(".meera@abdm")] // starts with a dot
    [InlineData("meera.@abdm")] // ends with a dot
    [InlineData("meera..k@abdm")]
    [InlineData("me@abdm")] // too short
    [InlineData("meera k@abdm")]
    [InlineData("meera.k")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_an_abha_address(string? value) => Abha.IsValidAddress(value).ShouldBeFalse();

    [Fact]
    public void An_abha_address_is_stored_in_lower_case()
    {
        Abha.NormalizeAddress("  Meera_K@ABDM ").ShouldBe("meera_k@abdm");
        Abha.NormalizeAddress(" ").ShouldBeNull();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("91-1234-5678-9010", "meera.k@abdm", true)]
    [InlineData("91-1234-5678-9012", null, false)]
    [InlineData(null, "meera.k@gmail.com", false)]
    public void The_shared_validation_rules_check_both_identifiers(string? number, string? address, bool valid)
    {
        var result = new HealthIdValidator().Validate(new HealthIds(number, address));

        result.IsValid.ShouldBe(valid);
        result.Errors.ShouldAllBe(e => e.ErrorMessage.Contains("ABHA"));
    }

    private sealed record HealthIds(string? Number, string? Address);

    private sealed class HealthIdValidator : AbstractValidator<HealthIds>
    {
        public HealthIdValidator()
        {
            RuleFor(x => x.Number).AbhaNumber();
            RuleFor(x => x.Address).AbhaAddress();
        }
    }
}
