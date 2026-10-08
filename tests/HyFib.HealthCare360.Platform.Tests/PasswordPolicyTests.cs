using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SEC-01 (ASVS V6.2.4, V6.2.11, V6.2.12): new passwords are checked against the passwords published from data
/// breaches, offline, and may not hold the hospital's or HC360's name.
/// </summary>
public sealed class PasswordPolicyTests
{
    private const string BreachedMessage = "This password has been published from a data breach, so attackers try it. Choose another.";

    private static PasswordPolicy Policy(int minLength = 12) =>
        new(Options.Create(new PlatformOptions { Security = { MinPasswordLength = minLength } }));

    [Theory]
    [InlineData("justinbieber")]
    [InlineData("JustinBieber")]
    [InlineData("Telechargement")]
    [InlineData("correcthorsebatterystaple")]
    public void A_password_published_from_a_breach_is_refused_in_any_case(string password)
    {
        Policy().Validate(password).ShouldBe([BreachedMessage]);
        PasswordPolicy.IsKnownToAttackers(password).ShouldBeTrue();
    }

    [Theory]
    [InlineData("skittles")]
    [InlineData("darkangel")]
    public void Shorter_breached_passwords_are_refused_where_the_minimum_is_lowered(string password) =>
        Policy(minLength: 8).Validate(password).ShouldBe([BreachedMessage]);

    [Theory]
    [InlineData("Ward 7 kettle sings at 4am")]
    [InlineData("correct horse battery staple, ward 9")]
    [InlineData("Theatre-lights-dim-after-ten")]
    public void A_password_of_its_own_is_accepted(string password)
    {
        Policy().Validate(password).ShouldBeEmpty();
        PasswordPolicy.IsKnownToAttackers(password).ShouldBeFalse();
    }

    [Fact]
    public void Passwords_that_are_not_in_the_set_almost_never_match_it()
    {
        // About one password in a million matches by chance; these 20,000 are fixed, so this never flickers.
        var matched = Enumerable.Range(0, 20_000)
            .Select(i => $"ward-{i}-kettle-{i * 7919 % 10_007}-sings")
            .Count(BreachedPasswords.Embedded.Contains);

        matched.ShouldBe(0);
    }

    [Fact]
    public void The_built_in_set_holds_the_breached_passwords()
    {
        BreachedPasswords.Embedded.Count.ShouldBeInRange(790_000, 810_000);
        BreachedPasswords.Embedded.Contains("qwerty123456").ShouldBeTrue();
    }

    [Fact]
    public void A_set_reads_back_exactly_what_was_written()
    {
        string[] passwords = ["Kettle-Sings-At-Four", "theatre lights dim", "WARD 7 NIGHT SHIFT", "kettle-sings-at-four"];
        using var stream = new MemoryStream();
        BreachedPasswords.Write(stream, passwords);
        stream.Position = 0;

        var set = BreachedPasswords.Read(stream);

        set.Count.ShouldBe(3, "letter case does not make a different password");
        passwords.ShouldAllBe(p => set.Contains(p));
        set.Contains("Ward 7 Night Shift").ShouldBeTrue();
        set.Contains("ward 8 night shift").ShouldBeFalse();
        Enumerable.Range(0, 1_000).Count(i => set.Contains($"not-written-{i}")).ShouldBe(0);
    }

    [Fact]
    public void Anything_else_is_not_read_as_a_set()
    {
        using var stream = new MemoryStream("HC360XX1 not a set"u8.ToArray());

        Should.Throw<InvalidDataException>(() => BreachedPasswords.Read(stream));
    }

    [Theory]
    [InlineData("NanaHospitals@Ward7", true)]
    [InlineData("nana hospitals ward seven", true)]
    [InlineData("MyHc360-login-2026", true)]
    [InlineData("healthcare360 night shift", true)]
    [InlineData("Mainly domain words at ward 7", false)]
    public void The_hospital_and_hc360_names_are_refused_anywhere(string password, bool refused)
    {
        var errors = Policy().Validate(password, contextWords: ["Nana Hospitals", "Main Campus", null]);

        errors.Contains("Password must not contain the hospital's name or HC360.").ShouldBe(refused);
    }
}
