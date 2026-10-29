using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The state an address names, however it is commonly written: what the rate list's language and a facility's saved
/// address are read from.
/// </summary>
public sealed class IndianStatesTests
{
    [Fact]
    public void Every_state_is_known_by_its_gst_name() =>
        Gstin.States.ShouldAllBe(state => IndianStates.Code(state.Value) == state.Key);

    [Theory]
    [InlineData("TN", "33")]
    [InlineData("Tamilnadu", "33")]
    [InlineData(" TAMIL NADU. ", "33")]
    [InlineData("tamil-nadu", "33")]
    [InlineData("Puducherry (U.T.)", "34")]
    [InlineData("Pondicherry", "34")]
    [InlineData("PY", "34")]
    [InlineData("U.P.", "09")]
    [InlineData("Orissa", "21")]
    [InlineData("OD", "21")]
    [InlineData("OR", "21")]
    [InlineData("Uttaranchal", "05")]
    [InlineData("UK", "05")]
    [InlineData("NCT of Delhi", "07")]
    [InlineData("New Delhi", "07")]
    [InlineData("Jammu & Kashmir", "01")]
    [InlineData("J&K", "01")]
    [InlineData("Dadra & Nagar Haveli and Daman & Diu", "26")]
    [InlineData("Daman and Diu", "26")]
    [InlineData("TS", "36")]
    [InlineData("TG", "36")]
    [InlineData("CG", "22")]
    [InlineData("Andaman & Nicobar", "35")]
    public void A_state_is_known_by_its_other_names_and_codes(string name, string code) => IndianStates.Code(name).ShouldBe(code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("India")]
    [InlineData("Madras")]
    [InlineData("Tamil")]
    [InlineData("XX")]
    [InlineData("Delhi NCR")]
    public void Something_else_names_no_state(string? name) => IndianStates.Code(name).ShouldBeNull();

    [Fact]
    public void A_state_is_written_as_gst_writes_it() =>
        (IndianStates.Name("tn"), IndianStates.Name("Orissa"), IndianStates.Name("Bavaria")).ShouldBe(("Tamil Nadu", "Odisha", null));
}
