using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Health identifiers (SRS EHR-08). The Verhoeff check digit, which Aadhaar numbers end in. The 14-digit ABHA number,
/// which has no published check digit, so any 14 digits are taken (the ABDM implementation guide's own example numbers
/// fail a Verhoeff check); it is shown as xx-xxxx-xxxx-xxxx, and masked as XX-XXXX-XXXX-1234 where the full number is
/// not needed. The ABHA address is a handle at @abdm (@sbx on the ABDM sandbox).
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
    [InlineData("91123456789012")] // fails a Verhoeff check, which ABHA numbers do not have
    public void An_abha_number_is_14_digits_grouped_or_run_together(string value) => Abha.IsValidNumber(value).ShouldBeTrue();

    /// <summary>
    /// The example patient of the NRCeS "FHIR Implementation Guide for ABDM" (ndhm.in 6.5.0, the package the ABDM
    /// documents are validated against) carries ABHA number 22-7225-4829-5255, and its example doctors the HPR ids
    /// 21-1521-3828-3227 and 25-1531-3528-3228, which ABDM issues in the same 14-digit form. None ends in a Verhoeff check
    /// digit, nor does about 9 in 10 of any 14 digits, so a check-digit rule would turn away real ABHA cards at the desk.
    /// </summary>
    [Theory]
    [InlineData("22-7225-4829-5255")] // the example patient's ABHA number
    [InlineData("21-1521-3828-3227")] // an example doctor's HPR id
    [InlineData("25-1531-3528-3228")] // another example doctor's HPR id
    public void The_ids_of_the_abdm_guides_examples_are_accepted_as_abha_numbers(string value)
    {
        Verhoeff.IsValid(Abha.Digits(value)).ShouldBeFalse();
        Abha.IsValidNumber(value).ShouldBeTrue();
    }

    [Fact]
    public void Any_last_digit_is_accepted()
    {
        for (var last = '0'; last <= '9'; last++)
        {
            Abha.IsValidNumber("91-1234-5678-901" + last).ShouldBeTrue();
        }
    }

    [Theory]
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
    [InlineData("22-7225-4829-5255", null, true)]
    [InlineData("91-1234-5678-901", null, false)]
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
