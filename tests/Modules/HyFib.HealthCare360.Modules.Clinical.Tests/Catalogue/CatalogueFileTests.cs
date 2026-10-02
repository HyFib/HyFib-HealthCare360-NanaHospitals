using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Catalogue;

public sealed class CatalogueFileTests
{
    private static readonly CatalogueFileLimits Limits = CatalogueFileLimits.ForSamples;

    [Fact]
    public void Reads_the_claml_file_as_uploaded()
    {
        using var file = CatalogueSamples.Stream(CatalogueSamples.Claml);

        var content = CatalogueFile.Read(file, "icd102019en.xml", Limits);

        content.Kind.ShouldBe(CatalogueKind.Icd10);
        content.InnerFileName.ShouldBeNull();
        content.Icd10!.Codes.Count.ShouldBe(42);
    }

    [Fact]
    public void Reads_the_claml_file_inside_the_zip_who_provides()
    {
        using var zip = CatalogueSamples.Zip(("icd102019en.xml", CatalogueSamples.Claml));

        var content = CatalogueFile.Read(zip, "icd102019en.xml.zip", Limits);

        content.Kind.ShouldBe(CatalogueKind.Icd10);
        content.InnerFileName.ShouldBe("icd102019en.xml");
        content.Icd10!.Codes.ShouldContain(c => c.Code == "E11.9");
    }

    [Fact]
    public void Reads_the_order_file_as_uploaded_and_inside_a_zip()
    {
        using var plain = CatalogueSamples.Stream(CatalogueSamples.PcsOrderFile);
        using var zip = CatalogueSamples.Zip(("icd10pcs_order_2025.txt", CatalogueSamples.PcsOrderFile), ("readme.pdf", "%PDF"));

        CatalogueFile.Read(plain, "icd10pcs_order_2025.txt", Limits).Pcs!.Codes.Count.ShouldBe(4);
        var zipped = CatalogueFile.Read(zip, "2025-icd-10-pcs-order-file.zip", Limits);
        zipped.Kind.ShouldBe(CatalogueKind.Icd10Pcs);
        zipped.Pcs!.Version.ShouldBe("2025");
    }

    [Fact]
    public void A_utf8_byte_order_mark_before_the_xml_is_fine()
    {
        using var file = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(CatalogueSamples.Claml)]);

        CatalogueFile.Read(file, "icd102019en.xml", Limits).Kind.ShouldBe(CatalogueKind.Icd10);
    }

    [Fact]
    public void A_zip_with_two_catalogue_files_is_refused()
    {
        using var zip = CatalogueSamples.Zip(("icd102019en.xml", CatalogueSamples.Claml), ("icd102016en.xml", CatalogueSamples.Claml));

        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(zip, "both.zip", Limits)).Message.ShouldContain("more than one catalogue file");
    }

    [Fact]
    public void A_zip_without_a_catalogue_file_is_refused()
    {
        using var zip = CatalogueSamples.Zip(("notes.txt", "hello"));

        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(zip, "notes.zip", Limits)).Message.ShouldContain("no ClaML");
    }

    [Fact]
    public void The_unzipped_size_is_limited_while_reading()
    {
        using var zip = CatalogueSamples.Zip(("icd102019en.xml", CatalogueSamples.Claml));

        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(zip, "icd102019en.xml.zip", Limits with { MaxUnzippedBytes = 1024 }))
            .Message.ShouldContain("once unzipped");
    }

    [Fact]
    public void A_damaged_zip_is_refused()
    {
        using var file = new MemoryStream([(byte)'P', (byte)'K', 3, 4, 1, 2, 3, 4, 5, 6, 7, 8]);

        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(file, "broken.zip", Limits)).Message.ShouldContain("damaged");
    }

    [Theory]
    [InlineData("%PDF-1.7 not a catalogue")]
    [InlineData("Code,Title\nE11.9,Type 2 diabetes")]
    public void Other_files_are_refused_with_what_to_upload_instead(string text)
    {
        using var file = CatalogueSamples.Stream(text);

        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(file, "codes.csv", Limits)).Message.ShouldContain("WHO ICD-10 ClaML file");
    }

    [Fact]
    public void Empty_and_oversized_files_are_refused()
    {
        using var empty = new MemoryStream();
        using var file = CatalogueSamples.Stream(CatalogueSamples.Claml);

        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(empty, "empty.xml", Limits)).Message.ShouldContain("empty");
        Should.Throw<CatalogueFileException>(() => CatalogueFile.Read(file, "icd.xml", Limits with { MaxUploadBytes = 100 })).Message.ShouldContain("larger than");
    }
}

public sealed class PcsOrderFileReaderTests
{
    [Fact]
    public void Reads_valid_codes_with_their_long_titles_and_tables()
    {
        var file = PcsOrderFileReader.Read(CatalogueSamples.Stream(CatalogueSamples.PcsOrderFile), "icd10pcs_order_2025.txt", CatalogueFileLimits.ForSamples);

        file.Version.ShouldBe("2025");
        file.Tables.ShouldBe(2);
        file.Codes.Select(c => c.Code).ShouldBe(["0016070", "0DTJ4ZZ", "0DTJ0ZZ", "BW03ZZZ"]);
        var appendix = file.Codes.Single(c => c.Code == "0DTJ4ZZ");
        appendix.Title.ShouldBe("Resection of Appendix, Percutaneous Endoscopic Approach");
        appendix.Category.ShouldBe("Gastrointestinal System, Resection");
        file.Codes.Single(c => c.Code == "BW03ZZZ").Category.ShouldBe("Imaging", "a code without a table heading is filed under its section");
    }

    [Fact]
    public void A_file_that_is_not_an_order_file_is_refused() =>
        Should.Throw<CatalogueFileException>(() =>
                PcsOrderFileReader.Read(CatalogueSamples.Stream("12345 this is not an order line\n"), "codes.txt", CatalogueFileLimits.ForSamples))
            .Message.ShouldContain("not the ICD-10-PCS order file");

    [Fact]
    public void A_line_without_a_break_is_not_read_whole()
    {
        var text = "00001 0016070 1 " + new string('x', 5000);

        Should.Throw<CatalogueFileException>(() => PcsOrderFileReader.Read(CatalogueSamples.Stream(text), "icd10pcs_order_2025.txt", CatalogueFileLimits.ForSamples))
            .Message.ShouldContain("longer than");
    }

    [Fact]
    public void A_partial_order_file_cannot_withdraw_the_catalogue() =>
        Should.Throw<CatalogueFileException>(() =>
                PcsOrderFileReader.Read(CatalogueSamples.Stream(CatalogueSamples.PcsOrderFile), "icd10pcs_order_2025.txt", new CatalogueFileLimits()))
            .Message.ShouldContain("only 4 valid codes");

    [Theory]
    [InlineData('0', "Medical and Surgical")]
    [InlineData('B', "Imaging")]
    [InlineData('X', "New Technology")]
    public void Sections_are_named(char section, string name) => PcsOrderFileReader.SectionOf(section).ShouldBe(name);
}
