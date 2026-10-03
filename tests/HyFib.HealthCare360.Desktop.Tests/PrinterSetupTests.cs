using System.IO;
using HyFib.HealthCare360.Desktop.Bridge;

namespace HyFib.HealthCare360.Desktop.Tests;

public sealed class PrinterSetupTests : IDisposable
{
    private static readonly string[] Installed = ["ZDesigner GK420t", "ZDesigner HC100-300dpi", "EPSON TM-T82 Receipt", "HP LaserJet M404"];
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 4, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-printers-" + Guid.NewGuid().ToString("N"));

    private static WorkstationPrinters Counter() => new(
        Labels: new RolePrinter("ZDesigner GK420t", "zpl", 203, 50, 25),
        Wristbands: new RolePrinter("ZDesigner HC100-300dpi", "zpl", 300, 25, 254, "qr"),
        Receipts: new RolePrinter("EPSON TM-T82 Receipt"),
        Documents: new RolePrinter("HP LaserJet M404"),
        UpdatedBy: "Asha Rao");

    private PrinterSetupStore Store(string? policy = null)
    {
        Directory.CreateDirectory(_root);
        var policyPath = Path.Combine(_root, "policy.json");
        if (policy is not null)
        {
            File.WriteAllText(policyPath, policy);
        }

        return new PrinterSetupStore(Path.Combine(_root, "Workstation"), policyPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Saved_printers_are_read_back_for_everyone_on_the_computer()
    {
        var store = Store();

        var saved = store.Save(Counter(), Installed, Now);
        var loaded = store.Load(Installed);

        saved.UpdatedAt.ShouldBe(Now);
        loaded.LockedByPolicy.ShouldBeFalse();
        loaded.ComputerName.ShouldBe(Environment.MachineName);
        loaded.Setup.ShouldBe(saved);
        File.ReadAllText(store.FilePath).ShouldContain("\"widthMm\": 50");
        Directory.GetFiles(Path.Combine(_root, "Workstation")).ShouldBe([store.FilePath], "the temporary file is replaced, not left behind");
    }

    [Fact]
    public void Nothing_saved_means_every_print_asks()
    {
        var loaded = Store().Load(Installed);

        loaded.Setup.ShouldBe(new WorkstationPrinters());
    }

    [Fact]
    public void An_unreadable_file_is_treated_as_nothing_saved()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ not json");

        store.Load(Installed).Setup.ShouldBe(new WorkstationPrinters());
    }

    [Fact]
    public void Printers_set_by_it_in_the_policy_are_shown_and_cannot_be_changed_here()
    {
        var store = Store("""{ "serverUrl": "https://hc360-srv:7360/", "printers": { "labels": { "printer": "ZDesigner GK420t", "language": "tspl", "dpi": 300, "widthMm": 38, "heightMm": 25 } } }""");

        var loaded = store.Load(Installed);

        loaded.LockedByPolicy.ShouldBeTrue();
        loaded.Setup.Labels.ShouldBe(new RolePrinter("ZDesigner GK420t", "tspl", 300, 38, 25));
        Should.Throw<BridgeException>(() => store.Save(Counter(), Installed, Now)).Code.ShouldBe("not_allowed");
    }

    [Fact]
    public void A_saved_printer_that_is_no_longer_installed_is_not_used_and_the_screen_says_why()
    {
        var store = Store();
        store.Save(Counter(), Installed, Now);

        var loaded = store.Load(["ZDesigner GK420t", "EPSON TM-T82 Receipt", "HP LaserJet M404"]);

        loaded.Setup.Wristbands.ShouldBeNull("Wristbands fall back to the page and the print dialog.");
        loaded.Setup.Labels.ShouldBe(Counter().Labels);
        loaded.Problems.ShouldHaveSingleItem().ShouldBe(
            "The wristband printer is not used: The printer 'ZDesigner HC100-300dpi' is not installed on this computer.");
    }

    [Fact]
    public void A_saved_file_edited_by_hand_is_checked_like_a_save()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, """
            {
              "labels": { "printer": "ZDesigner GK420t", "language": "zpl", "dpi": 600, "widthMm": 50, "heightMm": 25 },
              "wristbands": { "printer": "ZDesigner HC100-300dpi", "language": "zpl", "dpi": 300, "widthMm": 25, "heightMm": 254, "code": "pdf417" },
              "receipts": { "printer": "\\\\print-srv\\receipts" },
              "documents": { "printer": "HP LaserJet M404" }
            }
            """);

        var loaded = store.Load(Installed);

        loaded.Setup.ShouldBe(new WorkstationPrinters(Documents: new RolePrinter("HP LaserJet M404")));
        loaded.Problems.Count.ShouldBe(3);
        loaded.Problems[0].ShouldContain("label printer is not used: Choose the resolution");
        loaded.Problems[1].ShouldContain("Code 128 or QR");
        loaded.Problems[2].ShouldContain(@"'\\print-srv\receipts' is not installed");
    }

    [Fact]
    public void Printers_in_the_policy_that_are_not_valid_are_not_used_either()
    {
        var store = Store("""{ "printers": { "labels": { "printer": "ZDesigner GK420t", "language": "zpl" } } }""");

        var loaded = store.Load(Installed);

        loaded.LockedByPolicy.ShouldBeTrue();
        loaded.Setup.Labels.ShouldBeNull();
        loaded.Problems.ShouldHaveSingleItem().ShouldContain("203 or 300 dpi");
    }

    [Fact]
    public void A_printer_that_is_not_installed_is_refused() =>
        Should.Throw<BridgeException>(() => Store().Save(Counter() with { Receipts = new RolePrinter("Old receipt printer") }, Installed, Now))
            .Code.ShouldBe("printer_not_found");

    [Theory]
    [InlineData(null, 203, 50, 25, "Choose how HC360 prints")]
    [InlineData("epl", 203, 50, 25, "Choose how HC360 prints")]
    [InlineData("zpl", 600, 50, 25, "203 or 300 dpi")]
    [InlineData("zpl", 203, 120, 25, "25 to 110 mm wide")]
    [InlineData("tspl", 203, 50, 25.25, "15 to 100 mm long")]
    public void Label_printers_need_a_language_a_resolution_and_a_label_size(string? language, int dpi, double width, double height, string message) =>
        Should.Throw<BridgeException>(() =>
                PrinterSetupStore.Validate(new WorkstationPrinters(Labels: new RolePrinter("ZDesigner GK420t", language, dpi, (decimal)width, (decimal)height)), Installed))
            .Message.ShouldContain(message);

    [Fact]
    public void A_label_printer_on_its_windows_driver_needs_no_size()
    {
        Should.NotThrow(() => PrinterSetupStore.Validate(new WorkstationPrinters(Labels: new RolePrinter("ZDesigner GK420t", "driver")), Installed));
        Should.NotThrow(() => PrinterSetupStore.Validate(new WorkstationPrinters(Receipts: new RolePrinter("EPSON TM-T82 Receipt")), Installed));
    }

    [Fact]
    public void Wristbands_print_on_bands_with_a_code_scanners_read()
    {
        Should.Throw<BridgeException>(() => PrinterSetupStore.Validate(
            new WorkstationPrinters(Wristbands: new RolePrinter("ZDesigner HC100-300dpi", "zpl", 300, 50, 25)), Installed)).Message.ShouldContain("wristband");
        Should.Throw<BridgeException>(() => PrinterSetupStore.Validate(
            new WorkstationPrinters(Wristbands: new RolePrinter("ZDesigner HC100-300dpi", "zpl", 300, 25, 254, "pdf417")), Installed)).Message.ShouldContain("Code 128 or QR");
    }

    [Fact]
    public void Printer_names_with_control_characters_are_refused() =>
        Should.Throw<BridgeException>(() => PrinterSetupStore.Validate(new WorkstationPrinters(Documents: new RolePrinter("HP\nLaserJet")), Installed))
            .Code.ShouldBe("bad_request");
}

public sealed class PrintPageTests
{
    private static readonly string[] Installed = ["ZDesigner GK420t", @"\\print-srv\Ward3 Zebra", "HP LaserJet M404"];

    [Theory]
    [InlineData("ZDesigner GK420t", "ZDesigner GK420t")]
    [InlineData("zdesigner gk420t", "ZDesigner GK420t")]
    [InlineData(@"\\print-srv\Ward3 Zebra", @"\\print-srv\Ward3 Zebra")]
    public void Raw_jobs_go_to_printers_installed_on_this_computer(string name, string expected) =>
        PrintService.InstalledPrinter(name, Installed).ShouldBe(expected);

    [Theory]
    [InlineData(@"\\attacker\queue")]
    [InlineData(@"\\print-srv\Ward4 Zebra")]
    [InlineData("ZDesigner GK420t (copy 1)")]
    [InlineData("LPT1:")]
    public void Raw_jobs_to_any_other_printer_are_refused(string name) =>
        Should.Throw<BridgeException>(() => PrintService.InstalledPrinter(name, Installed)).Code.ShouldBe("printer_not_found");

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void A_raw_job_must_name_a_printer(string? name) =>
        Should.Throw<BridgeException>(() => PrintService.InstalledPrinter(name, Installed)).Code.ShouldBe("bad_request");

    [Fact]
    public void The_wristband_is_an_adult_band_printed_almost_to_the_edge()
    {
        PrintService.PageSizes["Wristband"].ShouldBe((1.0, 11.0));
        PrintService.Margin("Wristband").ShouldBe(0.05);
        PrintService.Margin("Label50x25").ShouldBe(0.05);
        PrintService.Margin("A4").ShouldBe(0.4);
    }

    [Theory]
    [InlineData(true, "ZDesigner GK420t", true)]
    [InlineData(true, null, false)]
    [InlineData(true, " ", false)]
    [InlineData(false, "ZDesigner GK420t", false)]
    [InlineData(null, "ZDesigner GK420t", false)]
    public void Prints_silently_only_to_a_named_printer(bool? silent, string? printer, bool expected) =>
        PrintService.PrintsSilently(new PrintHtmlRequest("<p>x</p>", printer, silent, 1, "Label50x25", false)).ShouldBe(expected);
}
