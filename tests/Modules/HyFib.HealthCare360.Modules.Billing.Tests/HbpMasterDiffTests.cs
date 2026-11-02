using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// What an HBP master import would change (INS-PMJAY, maker-checker): the counts and the changes the approver is shown are
/// the ones the import is applied with, and the master's stamp tells an approval whether the master is still the one the
/// import was read against.
/// </summary>
public sealed class HbpMasterDiffTests
{
    private const string Version = "HBP 2022";

    [Fact]
    public void A_master_adds_what_it_newly_lists_changes_what_it_lists_otherwise_and_retires_what_it_no_longer_lists()
    {
        HbpProcedure[] master =
        [
            Procedure("BM001A", rate: 7700m),
            Procedure("BM001B", rate: 9000m),
            Procedure("SG039A", rate: 21000m),
            Procedure("SG040A", rate: 15000m, active: false),
            Procedure("SG041A", rate: 12000m, active: false),
        ];
        HbpMasterRow[] rows =
        [
            Row("BM001A", rate: 7700m),
            Row("BM001B", rate: 9500m),
            Row("SG040A", rate: 15000m),
            Row("SG050A", rate: 30000m),
        ];

        var diff = HbpMasterDiff.Of(master, rows, Version);

        diff.Added.ShouldBe(["SG050A"]);
        diff.Updated.ShouldBe(["BM001B", "SG040A"], "a new rate is a change, and so is bringing back a retired procedure as it was");
        diff.Retired.ShouldBe(["SG039A"], "an active procedure the master no longer lists is retired; one already retired is not retired again");
        diff.Rates.ShouldBe([new HbpRateChange("BM001B", "Procedure BM001B", 9000m, 9500m)]);
        diff.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void The_same_master_again_changes_nothing_but_a_new_version_name_changes_every_procedure_it_lists()
    {
        HbpProcedure[] master = [Procedure("BM001A", rate: 7700m), Procedure("SG039A", rate: 21000m)];
        HbpMasterRow[] rows = [Row("BM001A", rate: 7700m), Row("SG039A", rate: 21000m)];

        HbpMasterDiff.Of(master, rows, Version).IsEmpty.ShouldBeTrue("the procedures already read as the file does");
        var renamed = HbpMasterDiff.Of(master, rows, "HBP 2022 (revised)");
        renamed.Updated.ShouldBe(["BM001A", "SG039A"], "each procedure is put under the master that now lists it");
        renamed.Rates.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("name")]
    [InlineData("rate")]
    [InlineData("stay")]
    [InlineData("reserved")]
    [InlineData("specialty")]
    [InlineData("package")]
    [InlineData("stratification")]
    public void Each_thing_a_master_says_of_a_procedure_is_a_change(string field)
    {
        var procedure = Procedure("SG039A", rate: 21000m);
        var row = Row("SG039A", rate: 21000m);
        var changed = field switch
        {
            "name" => row with { ProcedureName = "Appendicectomy (open)" },
            "rate" => row with { Rate = 22000m },
            "stay" => row with { LengthOfStayDays = 5 },
            "reserved" => row with { ReservedForPublic = true },
            "specialty" => row with { Specialty = "Surgery" },
            "package" => row with { PackageName = "Appendix" },
            _ => row with { Stratification = "Laparoscopic" },
        };

        HbpMasterDiff.Differs(procedure, row).ShouldBeFalse();
        HbpMasterDiff.Differs(procedure, changed).ShouldBeTrue();
        HbpMasterDiff.Of([procedure], [changed], Version).Updated.ShouldBe(["SG039A"]);
    }

    [Fact]
    public void A_procedure_newly_reserved_for_public_hospitals_is_named_and_one_already_reserved_is_not()
    {
        HbpProcedure[] master = [Procedure("SG039A", rate: 21000m), Procedure("SG040A", rate: 15000m, reserved: true)];
        HbpMasterRow[] rows = [Row("SG039A", rate: 21000m) with { ReservedForPublic = true }, Row("SG040A", rate: 15000m) with { ReservedForPublic = true }];

        HbpMasterDiff.Of(master, rows, Version).NewlyReserved.ShouldBe(["SG039A"]);
    }

    [Fact]
    public void The_masters_stamp_changes_with_anything_an_import_sets_and_not_with_the_order_procedures_are_read_in()
    {
        HbpProcedure[] Master() => [Procedure("SG039A", rate: 21000m), Procedure("BM001A", rate: 7700m)];
        var stamp = HbpMasterDiff.Stamp(Master());

        HbpMasterDiff.Stamp(Enumerable.Reverse(Master())).ShouldBe(stamp);
        HbpMasterDiff.Stamp([Procedure("SG039A", rate: 21000.00m), Procedure("BM001A", rate: 7700.0m)])
            .ShouldBe(stamp, "a rate read back from the database with its two decimals is the same rate");
        foreach (var change in new Action<HbpProcedure>[]
        {
            p => p.Rate = 21001m,
            p => p.Rate = null,
            p => p.ProcedureName = "Appendicectomy (open)",
            p => p.LengthOfStayDays = 4,
            p => p.ReservedForPublic = true,
            p => p.Specialty = "Surgery",
            p => p.PackageName = "Appendix",
            p => p.Stratification = "Open",
            p => p.MasterVersion = "HBP 2023",
            p => p.IsActive = false,
        })
        {
            var master = Master();
            change(master[0]);
            HbpMasterDiff.Stamp(master).ShouldNotBe(stamp);
        }

        HbpMasterDiff.Stamp([.. Master(), Procedure("CA001A", rate: 1000m)]).ShouldNotBe(stamp, "a procedure added is a change of the master");
        HbpMasterDiff.Stamp([Master()[0]]).ShouldNotBe(stamp, "and so is one gone");
    }

    [Fact]
    public void The_rows_staged_read_back_as_they_were_written_and_their_hash_is_of_that_text()
    {
        HbpMasterRow[] rows = [Row("BM001A", rate: 7700m) with { Stratification = "TBSA < 20%" }, Row("SG039A", rate: null) with { ReservedForPublic = true }];

        var text = HbpMasterImportRows.Write(rows);

        HbpMasterImportRows.Read(text).ShouldBe(rows);
        HbpMasterImportRows.Hash(text).ShouldBe(HbpMasterImportRows.Hash(HbpMasterImportRows.Write(rows)));
        HbpMasterImportRows.Hash(text).ShouldNotBe(HbpMasterImportRows.Hash(text.Replace("7700", "7800", StringComparison.Ordinal)));
    }

    private static HbpProcedure Procedure(string code, decimal? rate, bool active = true, bool reserved = false) => new()
    {
        Code = code,
        PackageCode = code[..5],
        SpecialtyCode = code[..2],
        Specialty = "Specialty " + code[..2],
        PackageName = "Package " + code[..5],
        ProcedureName = "Procedure " + code,
        Rate = rate,
        LengthOfStayDays = 2,
        ReservedForPublic = reserved,
        Stratification = null,
        MasterVersion = Version,
        IsActive = active,
    };

    private static HbpMasterRow Row(string code, decimal? rate) =>
        new(code, code[..2], "Specialty " + code[..2], "Package " + code[..5], "Procedure " + code, rate, 2, false, null);
}
