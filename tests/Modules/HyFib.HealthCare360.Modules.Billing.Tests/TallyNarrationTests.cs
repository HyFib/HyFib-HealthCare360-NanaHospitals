using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The patient in a voucher's narration (N12, "patient details can be fetched before narration"): the name and UHID
/// before the document numbers by default, the UHID alone for a VIP or a record in a protected category, and as little
/// as the facility's setting allows.
/// </summary>
public sealed class TallyNarrationTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly TallyPatient Ravi = TallyPatient.Registered("NHU26000123", "Ravi Kumar", isVip: false, SensitiveCategory.None);
    private static readonly TallyLedgerMap Ledgers = new();

    private static readonly TallySale Sale = new("NHOB260000001", Day, null, 500m, 0m, 0m, [new TallySaleLine(null, null, 500m, 0m, 0m, 0m)], false);

    private static TallyDayBook Book(TallyPatient? patient) => new(
        Day,
        [Sale with { Patient = patient }, Sale with { Patient = patient, IsCancellation = true }],
        [
            new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Cash, 500m)]) { Patient = patient },
            new TallyReceipt("NHRC260000002", Day, ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.Advance, 200m)]) { Patient = patient },
        ],
        [],
        [
            new TallyRefund("NHRF260000001", Day, RefundPurpose.Bill, PaymentMode.Cash, 100m) { Patient = patient },
            new TallyRefund("NHRF260000002", Day, RefundPurpose.Bill, PaymentMode.Advance, 50m) { Patient = patient },
        ],
        [
            new TallyCreditNote("NHCN260000001", Day, CreditNoteKind.CreditNote, 50m) { Patient = patient },
            new TallyCreditNote("NHCN260000002", Day, CreditNoteKind.WriteOff, 20m) { Patient = patient },
        ],
        [new TallyDoctorFee("Dr. Arjun Mehta", 300m, "DR005")],
        [new TallyPayout("NHDP2600001", Day, "Dr. Arjun Mehta", 1_000m, 100m, 0m, 900m)]);

    private static readonly string[] PatientVouchers =
    [
        "NHOB260000001", "NHOB260000001/C", "NHRC260000001", "NHRC260000002", "NHRF260000001", "NHRF260000002", "NHCN260000001", "NHCN260000002",
    ];

    [Fact]
    public void A_patients_vouchers_name_the_patient_and_uhid_before_the_document_numbers()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(Ravi), Ledgers).ToDictionary(v => v.Number);

        vouchers["NHOB260000001"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Bill NHOB260000001");
        vouchers["NHOB260000001/C"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Cancellation of bill NHOB260000001");
        vouchers["NHRC260000001"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Receipt NHRC260000001");
        vouchers["NHRC260000002"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Advance adjusted, receipt NHRC260000002");
        vouchers["NHRF260000001"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Refund NHRF260000001");
        vouchers["NHRF260000002"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Advance restored, refund NHRF260000002");
        vouchers["NHCN260000001"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Credit note NHCN260000001");
        vouchers["NHCN260000002"].Narration.ShouldBe("Ravi Kumar (UHID NHU26000123): Write-off NHCN260000002");
        vouchers.Values.Where(v => !PatientVouchers.Contains(v.Number)).ShouldAllBe(v => !v.Narration.Contains("UHID"));
    }

    [Theory]
    [InlineData(false, SensitiveCategory.Hiv)]
    [InlineData(false, SensitiveCategory.MentalHealth)]
    [InlineData(false, SensitiveCategory.Mtp)]
    [InlineData(false, SensitiveCategory.SexualViolence)]
    [InlineData(false, SensitiveCategory.Hiv | SensitiveCategory.Mtp)]
    [InlineData(true, SensitiveCategory.None)]
    public void A_vip_or_a_patient_in_a_protected_category_is_named_by_uhid_only(bool isVip, SensitiveCategory categories)
    {
        var patient = TallyPatient.Registered("NHU26000124", "Asha Devi", isVip, categories);

        var vouchers = TallyVoucherBuilder.Build(Book(patient), Ledgers).Where(v => PatientVouchers.Contains(v.Number)).ToList();

        vouchers.Count.ShouldBe(PatientVouchers.Length);
        vouchers.ShouldAllBe(v => v.Narration.StartsWith("UHID NHU26000124: ", StringComparison.Ordinal) && !v.Narration.Contains("Asha"));
        patient.Restricted.ShouldBeTrue();
    }

    [Fact]
    public void The_facility_can_name_only_the_uhid_or_no_patient_at_all()
    {
        var uhidOnly = TallyVoucherBuilder.Build(Book(Ravi), Ledgers with { PatientInNarration = TallyNarrationPatient.UhidOnly });
        var none = TallyVoucherBuilder.Build(Book(Ravi), Ledgers with { PatientInNarration = TallyNarrationPatient.None });

        uhidOnly.Single(v => v.Number == "NHOB260000001").Narration.ShouldBe("UHID NHU26000123: Bill NHOB260000001");
        uhidOnly.ShouldAllBe(v => !v.Narration.Contains("Ravi"));
        none.Single(v => v.Number == "NHOB260000001").Narration.ShouldBe("Bill NHOB260000001");
        none.ShouldAllBe(v => !v.Narration.Contains("Ravi") && !v.Narration.Contains("UHID"));
    }

    [Fact]
    public void A_voucher_without_a_patient_keeps_its_narration() =>
        TallyVoucherBuilder.Build(Book(null), Ledgers).Single(v => v.Number == "NHRC260000001").Narration.ShouldBe("Receipt NHRC260000001");

    [Fact]
    public void A_walk_in_customer_without_a_uhid_is_named_as_given_at_the_counter()
    {
        var walkIn = new TallyPatient(null, "  Ramesh   Kumar ");

        walkIn.Describe(TallyNarrationPatient.NameAndUhid).ShouldBe("Ramesh Kumar");
        walkIn.Describe(TallyNarrationPatient.UhidOnly).ShouldBeNull();
        walkIn.Describe(TallyNarrationPatient.None).ShouldBeNull();
        new TallyPatient(null, " ").Describe(TallyNarrationPatient.NameAndUhid).ShouldBeNull();
        new TallyPatient(null, null).Describe(TallyNarrationPatient.NameAndUhid).ShouldBeNull();
    }

    [Fact]
    public void A_name_is_written_on_one_line()
    {
        var patient = TallyPatient.Registered("NHU26000123", "Ravi\r\n Kumar\u0007", isVip: false, SensitiveCategory.None);

        patient.Describe(TallyNarrationPatient.NameAndUhid).ShouldBe("Ravi Kumar (UHID NHU26000123)");
        TallyPatient.Registered("NHU26000123", " ", isVip: false, SensitiveCategory.None).Describe(TallyNarrationPatient.NameAndUhid).ShouldBe("UHID NHU26000123");
    }

    [Fact]
    public void The_setting_defaults_to_name_and_uhid_and_is_saved_by_name()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        new TallyLedgerMap().PatientInNarration.ShouldBe(TallyNarrationPatient.NameAndUhid);
        JsonSerializer.Deserialize<TallyLedgerMap>("""{"patientInNarration":"UhidOnly"}""", web)!.PatientInNarration.ShouldBe(TallyNarrationPatient.UhidOnly);
        JsonSerializer.Deserialize<TallyLedgerMap>("{}", web)!.PatientInNarration.ShouldBe(TallyNarrationPatient.NameAndUhid);
        JsonSerializer.Serialize(new TallyLedgerMap(), web).ShouldContain("\"patientInNarration\":\"NameAndUhid\"");
    }

    [Fact]
    public void Every_voucher_still_balances_with_the_patient_named() =>
        TallyVoucherBuilder.Build(Book(Ravi), Ledgers).ShouldAllBe(v => v.Entries.Where(e => e.IsDebit).Sum(e => e.Amount) == v.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));

    [Fact]
    public void The_voucher_fingerprint_follows_the_narration_so_a_changed_setting_is_posted_again()
    {
        var named = TallyVoucherBuilder.Build(Book(Ravi), Ledgers).Single(v => v.Number == "NHOB260000001");
        var numbersOnly = TallyVoucherBuilder.Build(Book(Ravi), Ledgers with { PatientInNarration = TallyNarrationPatient.None }).Single(v => v.Number == "NHOB260000001");

        TallyTracking.Fingerprint(named).ShouldNotBe(TallyTracking.Fingerprint(numbersOnly));
    }
}
