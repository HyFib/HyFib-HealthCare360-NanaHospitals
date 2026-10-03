using HyFib.HealthCare360.Integrations.Labels;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

public sealed class Code128PlanTests
{
    [Fact]
    public void Digits_alone_go_in_code_set_c_two_to_a_symbol()
    {
        var plan = Code128Plan.For("26000001");

        plan.Runs.ShouldBe([new Code128Run(Code128Set.C, "26000001")]);
        plan.Modules.ShouldBe((11 * (4 + 2)) + 13);
        plan.ZplData().ShouldBe(">;26000001");
        plan.TsplData().ShouldBe("!10526000001");
    }

    [Fact]
    public void A_prefix_in_set_b_then_the_digits_in_set_c()
    {
        var plan = Code128Plan.For("LS2600000123");

        plan.Runs.ShouldBe([new Code128Run(Code128Set.B, "LS"), new Code128Run(Code128Set.C, "2600000123")]);
        plan.Modules.ShouldBe((11 * (2 + 1 + 5 + 2)) + 13);
        plan.ZplData().ShouldBe(">:LS>52600000123");
        plan.TsplData().ShouldBe("!104LS!0992600000123");
    }

    [Fact]
    public void An_odd_run_of_digits_leaves_one_digit_in_set_b()
    {
        var plan = Code128Plan.For("NH-BU-2600123");

        plan.Runs.ShouldBe([new Code128Run(Code128Set.B, "NH-BU-2"), new Code128Run(Code128Set.C, "600123")]);
        plan.Modules.ShouldBe((11 * (7 + 1 + 3 + 2)) + 13);
    }

    [Fact]
    public void Leading_digits_start_in_set_c_and_switch_back_for_letters()
    {
        var plan = Code128Plan.For("1234AB");

        plan.Runs.ShouldBe([new Code128Run(Code128Set.C, "1234"), new Code128Run(Code128Set.B, "AB")]);
        plan.ZplData().ShouldBe(">;1234>6AB");
        plan.TsplData().ShouldBe("!1051234!100AB");
    }

    [Theory]
    [InlineData("AB12CD")]
    [InlineData("HC360-TEST")]
    [InlineData("A1")]
    public void Short_runs_of_digits_stay_in_set_b(string value)
    {
        var plan = Code128Plan.For(value);

        plan.Runs.ShouldBe([new Code128Run(Code128Set.B, value)]);
        plan.Modules.ShouldBe((11 * (value.Length + 2)) + 13);
    }

    [Theory]
    [InlineData("NH00012345")]
    [InlineData("CS2600000123")]
    [InlineData("AST-2600123")]
    [InlineData("12345")]
    [InlineData("A1B22C333D4444E55555")]
    [InlineData("UHID 0001 2345/26")]
    public void The_runs_spell_the_value_and_count_every_symbol(string value)
    {
        var plan = Code128Plan.For(value);

        string.Concat(plan.Runs.Select(r => r.Characters)).ShouldBe(value);
        plan.Runs.Where(r => r.Set == Code128Set.C).ShouldAllBe(r => r.Characters.Length % 2 == 0 && r.Characters.All(char.IsAsciiDigit));
        var symbols = plan.Runs.Sum(r => r.Set == Code128Set.C ? r.Characters.Length / 2 : r.Characters.Length) + plan.Runs.Count - 1;
        plan.Modules.ShouldBe((11 * (symbols + 2)) + 13);
        plan.Modules.ShouldBeLessThanOrEqualTo((11 * (value.Length + 2)) + 13, "Never longer than the whole value in set B.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB!C")]
    [InlineData("A>B")]
    [InlineData("A\"B")]
    [InlineData("A\\B")]
    [InlineData("José")]
    [InlineData("TAB\tTAB")]
    public void Refuses_what_a_label_barcode_may_not_hold(string value)
    {
        Code128Plan.IsEncodable(value).ShouldBeFalse();
        Should.Throw<LabelLayoutException>(() => Code128Plan.For(value));
    }

    [Fact]
    public void Refuses_values_longer_than_48_characters()
    {
        Code128Plan.IsEncodable(new string('9', 48)).ShouldBeTrue();
        Code128Plan.IsEncodable(new string('9', 49)).ShouldBeFalse();
    }
}
