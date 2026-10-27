using System.IO.Compression;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Reading AB PM-JAY's HBP master (INS-PMJAY) as state agencies publish it: the HBP 2022 layout (specialty, specialty
/// code, package name, procedure code, procedure name, price, the tier-3 code, stratification, reservation, length of
/// stay), as CSV or .xlsx, with title rows above the headings.
/// </summary>
public sealed class HbpMasterFileTests
{
    private const string Csv = """
        AB PM-JAY Health Benefit Package 2022
        S.No,Specialty,Specialty Code,AB PM-JAY Package Name,HBP 2022 Procedure Code,Procedure Name,Procedure Price,Tier 3 (Z) Code,Stratification Criteria,Stratification Remarks,Reservation for Public Hospitals,Length of Stay
        1,Burns Management,BM,Thermal burns,BM001A,"Thermal burns, TBSA less than 20%","7,700",BM001AZ,Y,TBSA < 20%,N,3 days
        2,Burns Management,BM,Thermal burns,BM001D,Thermal burns 40-60% TBSA,100000,BM001DZ,Y,TBSA 40-60%,N,15
        3,General Surgery,SG,Appendicectomy,sg039a,Appendicectomy (laparoscopic),"₹ 21,000",SG039AZ,N,,Y,
        4,General Surgery,SG,Appendicectomy,SG039A,Appendicectomy again,20000,,N,,N,
        5,General Surgery,SG,Hernia,S.01.0001,Old style code,1000,,N,,N,
        6,General Surgery,SG,Hernia,SG040A,,1000,,N,,N,
        Total,,,,,,,,,,,
        """;

    [Fact]
    public void A_state_agencys_csv_is_read_by_its_headings()
    {
        var content = HbpMasterFile.Read(Encoding.UTF8.GetBytes(Csv)).Value;

        content.Rows.Select(r => r.Code).ShouldBe(["BM001A", "BM001D", "SG039A"]);
        var burns = content.Rows[0];
        (burns.SpecialtyCode, burns.Specialty, burns.PackageName, burns.ProcedureName).ShouldBe(("BM", "Burns Management", "Thermal burns", "Thermal burns, TBSA less than 20%"));
        (burns.Rate, burns.LengthOfStayDays, burns.ReservedForPublic, burns.Stratification).ShouldBe((7700m, 3, false, "TBSA < 20%"));
        var appendix = content.Rows[2];
        (appendix.Rate, appendix.ReservedForPublic, appendix.LengthOfStayDays).ShouldBe((21000m, true, null), "a code in lower case is read, a rupee sign and commas are not part of the rate");
    }

    [Fact]
    public void Rows_that_are_not_procedures_are_reported_and_left_out()
    {
        var problems = HbpMasterFile.Read(Encoding.UTF8.GetBytes(Csv)).Value.Problems;

        problems.ShouldBe(
        [
            "Row 6: procedure SG039A is listed again (first on row 5); the first is kept.",
            "Row 7: \"S.01.0001\" is not an HBP procedure code (two letters, three digits, then a letter for a stratified procedure).",
            "Row 8: procedure SG040A has no name.",
        ], "the totals row, with no code or name, is not a procedure and not a problem");
    }

    [Fact]
    public void An_xlsx_master_is_read_from_the_sheet_with_the_codes()
    {
        var content = HbpMasterFile.Read(Workbook()).Value;

        var row = content.Rows.ShouldHaveSingleItem();
        (row.Code, row.ProcedureName, row.Rate, row.SpecialtyCode).ShouldBe(("OR009B", "Total knee replacement (bilateral)", 160000m, "OR"));
        content.Problems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Code,Name\nBM001A,Burns\n")]
    [InlineData("")]
    public void A_file_without_procedure_codes_and_names_is_refused(string text)
    {
        HbpMasterFile.Read(Encoding.UTF8.GetBytes(text)).Error!.Code.ShouldBe("validation");
    }

    [Theory]
    [InlineData("BM001A", true)]
    [InlineData("SG039", true)]
    [InlineData("bm001a", false)]
    [InlineData("BM01A", false)]
    [InlineData("S.01.0001", false)]
    [InlineData("BM001AZ", false)]
    public void An_hbp_procedure_code_is_two_letters_three_digits_and_a_stratification_letter(string code, bool valid)
    {
        HbpMasterFile.IsProcedureCode(code).ShouldBe(valid);
    }

    private static byte[] Workbook()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }

            Add("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="Notes" sheetId="1" r:id="rId1"/><sheet name="HBP 2022" sheetId="2" r:id="rId2"/></sheets></workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Read the guidelines before booking.</t></is></c></row>
                </sheetData></worksheet>
                """);
            Add("xl/worksheets/sheet2.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Specialty Code</t></is></c><c r="B1" t="inlineStr"><is><t>Procedure Code</t></is></c><c r="C1" t="inlineStr"><is><t>Procedure Name</t></is></c><c r="D1" t="inlineStr"><is><t>Procedure Price</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>OR</t></is></c><c r="B2" t="inlineStr"><is><t>OR009B</t></is></c><c r="C2" t="inlineStr"><is><t>Total knee replacement (bilateral)</t></is></c><c r="D2"><v>160000</v></c></row>
                </sheetData></worksheet>
                """);
        }

        return stream.ToArray();
    }
}
