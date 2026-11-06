using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

/// <summary>
/// How the OT register writes a case: the register's own words, where the patient went after the theatre, the team with
/// what the master holds of them and the signatures, and what each member of staff may see of the patient.
/// </summary>
public sealed class OtRegisterTests
{
    // 6 Nov 2026: in at 9:40 am IST, out at 11:15 am.
    private static readonly DateTimeOffset In = new(2026, 11, 6, 4, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Out = In.AddMinutes(95);

    private static readonly Guid Surgeon = Guid.CreateVersion7();
    private static readonly Guid Anaesthetist = Guid.CreateVersion7();
    private static readonly Guid Assistant = Guid.CreateVersion7();
    private static readonly Guid Ward = Guid.CreateVersion7();
    private static readonly Guid Icu = Guid.CreateVersion7();

    private static readonly Dictionary<Guid, OtRegister.Clinician> Team = new()
    {
        [Surgeon] = new(Surgeon, "Dr. Arun Kumar", "MS, MCh (Surgical Gastroenterology)", "TNMC", "84512"),
        [Anaesthetist] = new(Anaesthetist, "Dr. Meena Raj", "MD (Anaesthesia)", "TNMC", "90211"),
        [Assistant] = new(Assistant, "Dr. Vivek S", "MBBS", null, "112233"),
    };

    private static readonly OtRegister.Viewer Doctor = new(FullContacts: false, ReadRestricted: false, SensitiveCategory.None);

    private static readonly ReportDescriptor Register = new OtRegisterReport().Descriptor;

    [Fact]
    public void The_register_is_patient_level_clinical_and_in_the_papers_column_order()
    {
        Register.ContainsPatientIdentifiers.ShouldBeTrue();
        Register.CanSchedule.ShouldBeFalse();
        Register.Columns.Select(c => c.Label).ShouldBe(
        [
            "S.No", "OT case no.", "OT", "Date", "Time from", "Time to", "Duration", "IP no.", "UHID", "Transferred to", "Name", "Age / sex", "Address",
            "ID no.", "Mobile no.", "Provisional diagnosis", "Name of the surgery", "Final diagnosis & operating notes", "Anaesthesia given",
            "Drug name & dose", "Surgeon", "Anaesthetist", "Assisting doctor", "Remarks",
        ]);
        Register.Columns.Where(c => c.IsPatientIdentifier).Select(c => c.Key).ShouldBe(["uhid", "patient"]);
    }

    [Theory]
    [InlineData(AnaesthesiaType.GA, "General")]
    [InlineData(AnaesthesiaType.Spinal, "Spinal")]
    [InlineData(AnaesthesiaType.Epidural, "Epidural")]
    [InlineData(AnaesthesiaType.Regional, "Nerve block")]
    [InlineData(AnaesthesiaType.Sedation, "IV / monitored sedation")]
    [InlineData(AnaesthesiaType.Local, "Local")]
    public void Anaesthesia_is_written_in_the_registers_words(AnaesthesiaType type, string written) =>
        OtRegister.AnaesthesiaLabel(type).ShouldBe(written);

    [Theory]
    [InlineData(Gender.Male, "M")]
    [InlineData(Gender.Female, "F")]
    [InlineData(Gender.Other, "TG")]
    [InlineData(Gender.Unknown, "—")]
    public void Sex_is_written_as_the_register_writes_it(Gender gender, string written) => OtRegister.Sex(gender).ShouldBe(written);

    [Theory]
    [InlineData(95, "1 h 35 min")]
    [InlineData(45, "45 min")]
    [InlineData(120, "2 h")]
    [InlineData(0, "0 min")]
    public void Duration_is_in_hours_and_minutes(int minutes, string written) =>
        OtRegister.Duration(TimeSpan.FromMinutes(minutes)).ShouldBe(written);

    [Fact]
    public void A_patient_moved_to_the_icu_after_the_theatre_was_transferred_there()
    {
        OtRegister.BedStay[] stays = [new(Guid.Empty, Ward, In.AddDays(-1), Out.AddMinutes(20)), new(Guid.Empty, Icu, Out.AddMinutes(20), null)];

        OtRegister.WardAfter(stays, In, Out).ShouldBe(Icu);
    }

    [Fact]
    public void An_icu_bed_given_while_the_patient_was_still_in_the_theatre_is_where_the_theatre_sent_them()
    {
        OtRegister.BedStay[] stays = [new(Guid.Empty, Ward, In.AddDays(-1), In.AddMinutes(30)), new(Guid.Empty, Icu, In.AddMinutes(30), null)];

        OtRegister.WardAfter(stays, In, Out).ShouldBe(Icu);
    }

    [Fact]
    public void A_patient_admitted_to_their_ward_while_in_the_theatre_and_moved_to_the_icu_after_was_transferred_there()
    {
        OtRegister.BedStay[] stays = [new(Guid.Empty, Ward, In.AddMinutes(20), Out.AddMinutes(10)), new(Guid.Empty, Icu, Out.AddMinutes(10), null)];

        OtRegister.WardAfter(stays, In, Out).ShouldBe(Icu);
    }

    [Fact]
    public void A_patient_admitted_only_after_the_operation_went_to_the_first_bed_they_took()
    {
        OtRegister.BedStay[] stays = [new(Guid.Empty, Icu, Out.AddMinutes(30), null)];

        OtRegister.WardAfter(stays, In, Out).ShouldBe(Icu);
    }

    [Fact]
    public void A_patient_who_kept_their_bed_went_back_to_their_ward()
    {
        OtRegister.BedStay[] stays = [new(Guid.Empty, Ward, In.AddDays(-1), null)];

        OtRegister.WardAfter(stays, In, Out).ShouldBe(Ward);
    }

    [Fact]
    public void A_move_the_next_day_is_not_where_the_theatre_sent_the_patient()
    {
        OtRegister.BedStay[] stays =
        [
            new(Guid.Empty, Ward, In.AddDays(-1), Out.Add(OtRegister.PostOperativeMove).AddMinutes(1)),
            new(Guid.Empty, Icu, Out.Add(OtRegister.PostOperativeMove).AddMinutes(1), null),
        ];

        OtRegister.WardAfter(stays, In, Out).ShouldBe(Ward);
        OtRegister.WardAfter([], In, Out).ShouldBeNull();
    }

    [Fact]
    public void A_clinician_is_written_with_what_the_master_holds_of_them()
    {
        OtRegister.Credentials(Team[Surgeon]).ShouldBe("Dr. Arun Kumar, MS, MCh (Surgical Gastroenterology), Reg. 84512 (TNMC)");
        OtRegister.Credentials(Team[Assistant]).ShouldBe("Dr. Vivek S, MBBS, Reg. 112233");
        OtRegister.Credentials(new(Guid.Empty, "Dr. New", null, "TNMC", null)).ShouldBe("Dr. New");
        OtRegister.Credentials(null).ShouldBe("—");
    }

    [Fact]
    public void A_completed_emergency_case_reads_as_the_paper_register_does()
    {
        var row = Row(Case(), Patient(), Note(), Record(), Doctor);

        Cell(row, "serial").ShouldBe(7);
        Cell(row, "caseNumber").ShouldBe("NHOT26000123");
        Cell(row, "theatre").ShouldBe("OT-1");
        Cell(row, "date").ShouldBe(new DateOnly(2026, 11, 6));
        Cell(row, "timeFrom").ShouldBe("9:40 am");
        Cell(row, "timeTo").ShouldBe("11:15 am");
        Cell(row, "duration").ShouldBe("1 h 35 min");
        Cell(row, "ipNumber").ShouldBe("NHIP26000456");
        Cell(row, "transferredTo").ShouldBe("SICU");
        Cell(row, "ageSex").ShouldBe("54Y 8M / M");
        Cell(row, "provisionalDiagnosis").ShouldBe("Acute abdomen, ? perforation");
        Cell(row, "surgery").ShouldBe("Emergency laparotomy, perforated duodenal ulcer closure");
        Cell(row, "finalDiagnosis").ShouldBe("Perforated duodenal ulcer. Findings: 1 cm anterior wall perforation, 500 ml bilious fluid");
        Cell(row, "anaesthesia").ShouldBe("General");
        Cell(row, "drugs").ShouldBe("Propofol 120 mg; Fentanyl 100 mcg; Atracurium 35 mg");
        Cell(row, "surgeon").ShouldBe(
            "Dr. Arun Kumar, MS, MCh (Surgical Gastroenterology), Reg. 84512 (TNMC) · operation note signed by Dr. Arun Kumar, 06 Nov 2026, 11:40 am");
        Cell(row, "anaesthetist").ShouldBe("Dr. Meena Raj, MD (Anaesthesia), Reg. 90211 (TNMC) · anaesthesia record signed by Dr. Meena Raj, 06 Nov 2026, 11:30 am");
        Cell(row, "assistants").ShouldBe("Dr. Vivek S, MBBS, Reg. 112233");
        Cell(row, "remarks").ShouldBe("Emergency. Complications: Hypotension after induction, treated");
    }

    [Fact]
    public void Contact_details_show_as_the_viewer_may_see_them()
    {
        var masked = Row(Case(), Patient(), Note(), Record(), Doctor);
        Cell(masked, "patient").ShouldBe("Murugan Selvam");
        Cell(masked, "address").ShouldBe("Chennai, Chennai, Tamil Nadu");
        Cell(masked, "mobile").ShouldBe("+91******3210");
        Cell(masked, "idNumber").ShouldBe("Aadhaar XXXX XXXX 4321; ABHA xx-xxxx-xxxx-7788");

        var full = Row(Case(), Patient(), Note(), Record(), Doctor with { FullContacts = true });
        Cell(full, "address").ShouldBe("12 Anna Salai, Chennai, Chennai, Tamil Nadu, 600002");
        Cell(full, "mobile").ShouldBe("+919876543210");
    }

    [Fact]
    public void A_restricted_patient_is_named_by_initials_and_their_clinical_columns_are_withheld()
    {
        var row = Row(Case(), Patient() with { IsVip = true }, Note(), Record(), Doctor with { FullContacts = true });

        Cell(row, "patient").ShouldBe("M. S. (restricted)");
        Cell(row, "uhid").ShouldBe("NH26000099");
        foreach (var key in new[] { "address", "idNumber", "mobile" })
        {
            Cell(row, key).ShouldBe("—", key);
        }

        foreach (var key in new[] { "provisionalDiagnosis", "surgery", "finalDiagnosis", "drugs" })
        {
            Cell(row, key).ShouldBe(OtRegister.Withheld, key);
        }

        Cell(row, "remarks").ShouldBe("Emergency", "complications are the clinical record's");

        var opened = Row(Case(), Patient() with { IsVip = true }, Note(), Record(), Doctor with { ReadRestricted = true });
        Cell(opened, "patient").ShouldBe("Murugan Selvam");
        Cell(opened, "surgery").ShouldBe("Emergency laparotomy, perforated duodenal ulcer closure");
    }

    [Fact]
    public void A_confidential_record_shows_its_clinical_columns_only_to_staff_holding_every_category_it_is_in()
    {
        var mtp = Patient() with { SensitiveCategories = SensitiveCategory.Mtp | SensitiveCategory.Hiv };

        var withheld = Row(Case(), mtp, Note(), Record(), Doctor with { Categories = SensitiveCategory.Hiv });
        Cell(withheld, "patient").ShouldBe("Murugan Selvam", "a list names the patient; the record is what is closed");
        Cell(withheld, "surgery").ShouldBe(OtRegister.Withheld);
        Cell(withheld, "provisionalDiagnosis").ShouldBe(OtRegister.Withheld);
        Cell(withheld, "anaesthesia").ShouldBe("General");
        OtRegister.Withheld.ShouldNotContain("MTP");

        var held = Row(Case(), mtp, Note(), Record(), Doctor with { Categories = SensitiveCategory.Hiv | SensitiveCategory.Mtp });
        Cell(held, "surgery").ShouldBe("Emergency laparotomy, perforated duodenal ulcer closure");
    }

    [Fact]
    public void A_case_cancelled_after_sign_in_stays_on_the_register_with_its_reason()
    {
        var row = Row(Case() with { Status = SurgeryStatus.Cancelled, CompletedAt = null, CancelReason = "BP 210/120 on the table" }, Patient(), null, null, Doctor);

        Cell(row, "timeTo").ShouldBe("Cancelled");
        Cell(row, "duration").ShouldBe("—");
        Cell(row, "remarks").ShouldBe("Emergency. Cancelled after Sign-in: BP 210/120 on the table");
    }

    [Fact]
    public void A_case_without_its_notes_says_so_and_names_the_surgery_as_booked()
    {
        var row = Row(Case() with { Status = SurgeryStatus.InProgress, CompletedAt = null }, Patient(), null, null, Doctor);

        Cell(row, "timeTo").ShouldBe("In theatre");
        Cell(row, "surgery").ShouldBe("Exploratory laparotomy (as booked)");
        Cell(row, "provisionalDiagnosis").ShouldBe("Peritonitis");
        Cell(row, "finalDiagnosis").ShouldBe("Operation note not written");
        Cell(row, "drugs").ShouldBe("—");
        Cell(row, "surgeon").ShouldBe("Dr. Arun Kumar, MS, MCh (Surgical Gastroenterology), Reg. 84512 (TNMC) · operation note not written");
        Cell(row, "anaesthetist").ShouldBe("Dr. Meena Raj, MD (Anaesthesia), Reg. 90211 (TNMC) · anaesthesia record not written");

        var unsigned = Row(Case(), Patient(), Note() with { SignedAt = null }, Record(), Doctor);
        ((string?)Cell(unsigned, "surgeon")).ShouldEndWith("· operation note not signed");
    }

    [Fact]
    public void Local_anaesthesia_given_by_the_surgeon_names_no_anaesthetist()
    {
        var row = Row(Case() with { AnaesthesiaType = AnaesthesiaType.Local, AnaesthetistId = null, Laterality = Laterality.Right }, Patient(), Note(), null, Doctor);

        Cell(row, "anaesthesia").ShouldBe("Local");
        Cell(row, "anaesthetist").ShouldBe("Local by the surgeon");
        Cell(row, "surgery").ShouldBe("Emergency laparotomy, perforated duodenal ulcer closure, right");
    }

    private static object?[] Row(OtRegister.Case c, OtRegister.Person patient, OtRegister.Note? note, OtRegister.Anaesthesia? record, OtRegister.Viewer viewer) =>
        OtRegister.Row(7, c, "OT-1", "NHIP26000456", "SICU", patient, note, record, Team, viewer);

    private static object? Cell(object?[] row, string key) => row[Register.ColumnIndex(key)];

    private static OtRegister.Case Case() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "NHOT26000123", Guid.CreateVersion7(), SurgeryStatus.Completed,
        SurgeryPriority.Emergency, In, Out, "Exploratory laparotomy", Laterality.NotApplicable, "Peritonitis", Surgeon, [Assistant], Anaesthetist,
        AnaesthesiaType.GA, null);

    private static OtRegister.Person Patient() => new(
        Guid.CreateVersion7(), "NH26000099", "Murugan Selvam", Gender.Male, new DateOnly(1972, 3, 1),
        new Address { Line1 = "12 Anna Salai", City = "Chennai", District = "Chennai", State = "Tamil Nadu", PinCode = "600002" },
        "+919876543210", "4321", "xx-xxxx-xxxx-7788", IsVip: false, SensitiveCategory.None);

    private static OtRegister.Note Note() => new(
        Guid.Empty, "Acute abdomen, ? perforation", "Perforated duodenal ulcer.", "Emergency laparotomy, perforated duodenal ulcer closure",
        "1 cm anterior wall perforation, 500 ml bilious fluid", "Hypotension after induction, treated", Out.AddMinutes(25), "Dr. Arun Kumar");

    private static OtRegister.Anaesthesia Record() => new(
        Guid.Empty, Anaesthetist, AnaesthesiaType.GA,
        [new(In.AddMinutes(5), "Fentanyl", "100 mcg", "IV"), new(In.AddMinutes(2), "Propofol", "120 mg", "IV"), new(In.AddMinutes(8), "Atracurium", "35 mg", "IV")],
        "hypotension after induction, treated", Out.AddMinutes(15), "Dr. Meena Raj");
}
