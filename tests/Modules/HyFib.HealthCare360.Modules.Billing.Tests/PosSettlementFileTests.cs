using System.IO.Compression;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Reading acquirers' card and UPI settlement reports (INT-POS reconciliation).</summary>
public sealed class PosSettlementFileTests
{
    private static readonly PosSettlementSettings Defaults = new();

    /// <summary>A bank's MIS as it comes: title rows, then the headings, transactions, a total row.</summary>
    private const string BankReport = """
        HDFC Bank Merchant Settlement Report
        Merchant: NANA HOSPITALS,MID: 7200012345

        Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR %,MDR Amount,GST,Net Amount
        07/10/2026,AXISN26280012345,72001234,06/10/2026 10:15:32,SALE,VISA DEBIT,4718XXXXXXXX1234,A1B2C3,627910123456,"1,500.00",1.80,27.00,4.86,"1,468.14"
        07/10/2026,AXISN26280012345,72001234,06/10/2026 11:02:10,SALE,UPI,,,627910654321,800.00,0.00,0.00,0.00,800.00
        07/10/2026,AXISN26280012345,72001234,06/10/2026 12:30:00,REFUND,MASTER CREDIT,5241XXXXXXXX9876,Z9Y8X7,627911000001,250.00,0.00,0.00,0.00,250.00
        Total,,,,,,,,,"2,550.00",,27.00,4.86,"2,018.14"
        """;

    private static PosSettlementFileContent Read(string text, PosSettlementSettings? settings = null, DateOnly? settledOn = null)
    {
        var result = PosSettlementFile.Read(Encoding.UTF8.GetBytes(text), settings ?? Defaults, settledOn);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    private static string Failure(string text, PosSettlementSettings? settings = null, DateOnly? settledOn = null)
    {
        var result = PosSettlementFile.Read(Encoding.UTF8.GetBytes(text), settings ?? Defaults, settledOn);
        result.IsFailure.ShouldBeTrue();
        return result.Error!.Message;
    }

    [Fact]
    public void A_banks_report_is_read_from_its_headings_after_the_title_rows()
    {
        var file = Read(BankReport);

        file.Rows.Count.ShouldBe(3);
        file.Ignored.ShouldBe(0, "the total row is not a transaction left out");
        file.Problems.ShouldBeEmpty();
        file.Columns.ShouldContain("RRN from \"RRN\"");
        file.Columns.ShouldContain("Charges from \"MDR Amount\"");
        file.Columns.ShouldNotContain(c => c.Contains("MDR %", StringComparison.Ordinal), "a rate is not an amount");

        var card = file.Rows[0];
        card.ShouldSatisfyAllConditions(
            r => r.Type.ShouldBe(PosSettlementLineType.Sale),
            r => r.Mode.ShouldBe(PaymentMode.Card),
            r => r.TerminalId.ShouldBe("72001234"),
            r => r.Amount.ShouldBe(1_500m),
            r => r.Charges.ShouldBe(27m),
            r => r.Tax.ShouldBe(4.86m),
            r => r.Net.ShouldBe(1_468.14m),
            r => r.Rrn.ShouldBe("627910123456"),
            r => r.ApprovalCode.ShouldBe("A1B2C3"),
            r => r.CardLast4.ShouldBe("1234"),
            r => r.SettledOn.ShouldBe(new DateOnly(2026, 10, 7)),
            r => r.Utr.ShouldBe("AXISN26280012345"),
            r => r.TransactionAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 10, 15, 32, TimeSpan.FromHours(5.5))),
            r => r.TransactionOn.ShouldBe(new DateOnly(2026, 10, 6)));
    }

    [Fact]
    public void Only_the_last_four_digits_of_a_card_are_kept()
    {
        var file = Read(BankReport);

        file.Rows.Select(r => r.CardLast4).ShouldBe(["1234", null, "9876"]);
    }

    [Fact]
    public void Upi_and_refunds_are_told_apart_and_a_refund_takes_money_back()
    {
        var file = Read(BankReport);

        file.Rows[1].ShouldSatisfyAllConditions(r => r.Mode.ShouldBe(PaymentMode.Upi), r => r.ApprovalCode.ShouldBeNull(), r => r.Net.ShouldBe(800m));
        file.Rows[2].ShouldSatisfyAllConditions(
            r => r.Type.ShouldBe(PosSettlementLineType.Refund),
            r => r.Amount.ShouldBe(250m),
            r => r.Net.ShouldBe(-250m),
            r => r.Charges.ShouldBe(0m),
            r => r.Tax.ShouldBe(0m));
    }

    [Fact]
    public void Every_line_balances_gross_against_net_charges_and_gst()
    {
        foreach (var row in Read(BankReport).Rows)
        {
            var gross = row.Type == PosSettlementLineType.Sale ? row.Amount : -row.Amount;
            (row.Net + row.Charges + row.Tax).ShouldBe(gross, $"row {row.RowNumber}");
        }
    }

    [Fact]
    public void Without_a_net_column_the_net_is_the_amount_less_charges_and_gst()
    {
        var file = Read("""
            RRN,Txn Amount,Commission,GST,Settlement Date
            111122223333,1000.00,15.00,2.70,07-Oct-2026
            """);

        file.Rows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Net.ShouldBe(982.30m), r => r.Charges.ShouldBe(15m), r => r.Tax.ShouldBe(2.70m));
    }

    [Fact]
    public void Gst_split_into_cgst_and_sgst_is_added_up()
    {
        var file = Read("""
            RRN;Amount;MDR Amount;CGST;SGST;Net Amount;Settlement Date
            111122223333;1000.00;15.00;1.35;1.35;982.30;2026-10-07
            """);

        file.Rows.ShouldHaveSingleItem().Tax.ShouldBe(2.70m);
    }

    [Fact]
    public void Semicolons_tabs_and_pipes_separate_columns_too_and_quoted_fields_may_hold_them()
    {
        Read("RRN|Txn Amount|Settlement Date\n111122223333|500.00|07/10/2026\n").Rows.ShouldHaveSingleItem().Amount.ShouldBe(500m);
        Read("RRN\tTxn Amount\tSettlement Date\n111122223333\t500.00\t07/10/2026\n").Rows.ShouldHaveSingleItem().Amount.ShouldBe(500m);
        Read("RRN;Txn Amount;Settlement Date;Remarks\n111122223333;500.00;07/10/2026;\"a; b\"\n").Rows.ShouldHaveSingleItem().Amount.ShouldBe(500m);
    }

    [Fact]
    public void An_acquirers_own_headings_can_be_added_in_the_setting()
    {
        const string report = "Ref Num,Sale Value,Paid On\n111122223333,500.00,07/10/2026\n";
        Failure(report).ShouldContain(PosSettlementSettings.Key);

        var settings = new PosSettlementSettings
        {
            Columns = new Dictionary<string, IReadOnlyList<string>>
            {
                ["rrn"] = ["Ref Num"],
                ["amount"] = ["Sale Value"],
                ["settlementDate"] = ["Paid On"],
            },
        };
        Read(report, settings).Rows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Rrn.ShouldBe("111122223333"), r => r.Amount.ShouldBe(500m));
    }

    [Fact]
    public void A_report_without_settlement_dates_needs_the_day_the_bank_was_paid()
    {
        const string report = "RRN,Txn Amount\n111122223333,500.00\n";

        Failure(report).ShouldContain("settlement date");
        Read(report, settledOn: new DateOnly(2026, 10, 7)).Rows.ShouldHaveSingleItem().SettledOn.ShouldBe(new DateOnly(2026, 10, 7));
    }

    [Fact]
    public void Rows_that_cannot_be_matched_or_do_not_add_up_are_left_out_with_the_reason()
    {
        var file = Read("""
            RRN,Approval Code,Txn Amount,Net Amount,Settlement Date
            111122223333,,500.00,490.00,07/10/2026
            ,,300.00,295.00,07/10/2026
            444455556666,,200.00,250.00,07/10/2026
            777788889999,,abc,,07/10/2026
            111122223333,,500.00,490.00,07/10/2026
            """);

        file.Rows.ShouldHaveSingleItem().Rrn.ShouldBe("111122223333");
        file.Ignored.ShouldBe(4);
        file.Problems.ShouldBe(
        [
            "Row 3 has neither an RRN nor an approval code, so it cannot be matched and was left out.",
            "Row 4: the net amount, charges and GST do not add up to the transaction amount, so it was left out.",
            "Row 5 has no transaction amount, so it was left out.",
            "Row 6 repeats row 2, so it was left out.",
        ]);
    }

    [Fact]
    public void A_chargeback_is_kept_even_without_a_reference()
    {
        var file = Read("""
            TID,Txn Type,RRN,Txn Amount,Net Amount,Settlement Date,UTR
            72001234,Chargeback,,1200.00,1200.00,07/10/2026,UTR123
            """);

        file.Rows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Type.ShouldBe(PosSettlementLineType.Adjustment),
            r => r.Net.ShouldBe(-1_200m),
            r => r.Key.ShouldContain("UTR123"));
    }

    [Fact]
    public void Chargebacks_that_nothing_tells_apart_are_all_kept_and_keep_their_keys_in_a_later_report()
    {
        const string report = """
            TID,Txn Type,RRN,Txn Amount,Net Amount,Settlement Date,UTR
            72001234,Chargeback,,500.00,500.00,07/10/2026,UTR123
            72001234,Chargeback,,500.00,500.00,07/10/2026,UTR123
            """;

        var file = Read(report);

        file.Rows.Count.ShouldBe(2, "both took money from the payout");
        file.Problems.ShouldBeEmpty();
        file.Rows.Select(r => r.Key).Distinct().Count().ShouldBe(2);
        Read(report).Rows.Select(r => r.Key).ShouldBe(file.Rows.Select(r => r.Key), "an overlapping report adds neither again");
    }

    [Fact]
    public void Every_sheet_with_the_headings_is_read_and_the_problems_name_their_sheet()
    {
        var file = PosSettlementFile.Read(SplitWorkbook(), Defaults, null);

        file.IsSuccess.ShouldBeTrue(file.Error?.Message);
        file.Value.Rows.Select(r => (r.Mode, r.Rrn)).ShouldBe([(PaymentMode.Card, "627910123456"), (PaymentMode.Upi, "627910654321")]);
        file.Value.Problems.ShouldBe(
        [
            "Sheet \"Card\", row 3 has neither an RRN nor an approval code, so it cannot be matched and was left out.",
            "Sheet \"UPI\", row 3 repeats sheet \"Card\", row 2, so it was left out.",
        ]);
        file.Value.Ignored.ShouldBe(2);
    }

    [Theory]
    [InlineData("06/10/2026", 2026, 10, 6)]
    [InlineData("6-10-2026", 2026, 10, 6)]
    [InlineData("06-Oct-2026", 2026, 10, 6)]
    [InlineData("06-OCT-26", 2026, 10, 6)]
    [InlineData("2026-10-06", 2026, 10, 6)]
    [InlineData("06.10.2026", 2026, 10, 6)]
    [InlineData("46301", 2026, 10, 6)]
    public void Dates_are_read_day_first_or_as_excel_serial_numbers(string text, int year, int month, int day)
    {
        PosSettlementFile.ParseDateTime(text).ShouldNotBeNull().Date.ShouldBe(new DateTime(year, month, day));
    }

    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("₹ 1,234.50", 1234.50)]
    [InlineData("INR 99", 99)]
    [InlineData("(12.00)", -12)]
    [InlineData("-12.00", -12)]
    [InlineData("50.00 Dr", -50)]
    public void Amounts_are_read_as_reports_write_them(string text, double expected)
    {
        PosSettlementFile.ParseAmount(text).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("'627910123456", "627910123456")]
    [InlineData("627910123456.0", "627910123456")]
    [InlineData(" a1b2-c3 ", "A1B2C3")]
    [InlineData("   ", null)]
    public void References_keep_letters_and_digits_only(string text, string? expected)
    {
        PosSettlementFile.Reference(text, 32).ShouldBe(expected);
    }

    [Fact]
    public void The_same_transaction_has_the_same_key_in_every_report()
    {
        var first = Read(BankReport).Rows[0];
        var again = Read(BankReport.Replace("AXISN26280012345", "OTHERUTR", StringComparison.Ordinal)).Rows[0];

        again.Key.ShouldBe(first.Key, "the RRN identifies the transaction");
        Read(BankReport).Rows[2].Key.ShouldNotBe(Read(BankReport).Rows[0].Key);
    }

    [Fact]
    public void An_excel_workbook_is_read_like_the_csv()
    {
        var file = PosSettlementFile.Read(Workbook(), Defaults, null);

        file.IsSuccess.ShouldBeTrue(file.Error?.Message);
        file.Value.Rows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Rrn.ShouldBe("627910123456"),
            r => r.Amount.ShouldBe(1_500m),
            r => r.Net.ShouldBe(1_468.14m),
            r => r.SettledOn.ShouldBe(new DateOnly(2026, 10, 7)),
            r => r.TransactionAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 10, 30, 0, TimeSpan.FromHours(5.5))));
    }

    [Fact]
    public void An_old_excel_file_or_an_empty_one_is_refused_with_what_to_do()
    {
        PosSettlementFile.Read([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1], Defaults, null).Error!.Message.ShouldContain(".xlsx or .csv");
        PosSettlementFile.Read([], Defaults, null).Error!.Message.ShouldContain("empty");
        PosSettlementFile.Read([(byte)'P', (byte)'K', 3, 4, 0, 0], Defaults, null).Error!.Message.ShouldContain("could not be read");
    }

    /// <summary>
    /// A workbook as Excel saves it: shared strings for the text, numbers for amounts, an RRN typed as a number, and
    /// dates as serial numbers (46302 is 7 October 2026; 46301.4375 is 6 October 2026, 10:30).
    /// </summary>
    /// <summary>
    /// A workbook with a summary sheet (no transactions) and the card and UPI transactions on sheets of their own, in
    /// inline strings. The UPI sheet repeats the card sheet's transaction.
    /// </summary>
    private static byte[] SplitWorkbook()
    {
        static string Cell(string column, int row, string value) =>
            double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _)
                ? $"<c r=\"{column}{row}\"><v>{value}</v></c>"
                : $"<c r=\"{column}{row}\" t=\"inlineStr\"><is><t>{value}</t></is></c>";

        static string Sheet(params string[][] rows) =>
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
            + string.Concat(rows.Select((cells, i) =>
                $"<row r=\"{i + 1}\">{string.Concat(cells.Select((value, c) => value.Length == 0 ? string.Empty : Cell(((char)('A' + c)).ToString(), i + 1, value)))}</row>"))
            + "</sheetData></worksheet>";

        string[] headings = ["RRN", "Approval Code", "Payment Mode", "Txn Amount", "Settlement Date"];
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
                <sheets><sheet name="Summary" sheetId="1" r:id="rId1"/><sheet name="Card" sheetId="2" r:id="rId2"/><sheet name="UPI" sheetId="3" r:id="rId3"/></sheets></workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet3.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", Sheet(["Settlement summary"], ["Total paid", "2300"]));
            Add("xl/worksheets/sheet2.xml", Sheet(headings, ["627910123456", "A1B2C3", "VISA", "1500", "07/10/2026"], ["", "", "VISA", "100", "07/10/2026"]));
            Add("xl/worksheets/sheet3.xml", Sheet(headings, ["627910654321", "", "UPI", "800", "07/10/2026"], ["627910123456", "A1B2C3", "VISA", "1500", "07/10/2026"]));
        }

        return stream.ToArray();
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
                <sheets><sheet name="MIS" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add("xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="6" uniqueCount="6">
                <si><t>Settlement report</t></si><si><t>RRN</t></si><si><r><t>Txn </t></r><r><t>Amount</t></r></si>
                <si><t>Net Amount</t></si><si><t>Settlement Date</t></si><si><t>Txn Date</t></si>
                </sst>
                """);
            Add("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c></row>
                <row r="3"><c r="A3" t="s"><v>1</v></c><c r="B3" t="s"><v>2</v></c><c r="C3" t="s"><v>3</v></c><c r="D3" t="s"><v>4</v></c><c r="E3" t="s"><v>5</v></c></row>
                <row r="4"><c r="A4"><v>627910123456</v></c><c r="B4"><v>1500</v></c><c r="C4"><v>1468.14</v></c><c r="D4" s="1"><v>46302</v></c><c r="E4" s="2"><v>46301.4375</v></c></row>
                </sheetData></worksheet>
                """);
        }

        return stream.ToArray();
    }
}
