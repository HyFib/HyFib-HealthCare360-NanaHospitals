using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;
using HyFib.HealthCare360.Modules.Analytics.Tests.TestSupport;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

/// <summary>
/// The critical-values register and the deaths, LAMA and DAMA register show protected patients as the chart does: a VIP
/// patient by initials, and the clinical columns of a VIP or sensitive-category patient withheld, unless the person running
/// the report holds the permissions that open that record. A scheduled run holds none, so sees both withheld.
/// </summary>
public sealed class ProtectedPatientReportsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 4, 10, 0, TimeSpan.Zero);

    private static readonly PatientView Nobody = new(ReadRestricted: false, SensitiveCategory.None);
    private static readonly PatientView Registrar = new(ReadRestricted: true, SensitiveCategory.None);
    private static readonly PatientView HivCounsellor = new(ReadRestricted: false, SensitiveCategory.Hiv);

    private static readonly ReportDescriptor Critical = new CriticalValuesReport().Descriptor;
    private static readonly ReportDescriptor Adverse = new AdverseDischargesReport().Descriptor;

    private static CriticalValuesReport.CriticalRow LabRow(bool vip = false, SensitiveCategory categories = SensitiveCategory.None) => new(
        Guid.CreateVersion7(), "NH26000099", "Ravi Kumar Sharma", DiagnosticKind.Laboratory, "HIV viral load", 1_200_000m, null, "copies/mL", ResultFlag.CriticalHigh,
        At, At.AddMinutes(12), "Dr. Meena Raj", CommunicationMethod.Phone, true, vip, categories);

    private static AdverseDischargesReport.RegisterRow Stay(bool vip = false, SensitiveCategory categories = SensitiveCategory.None) => new(
        Guid.CreateVersion7(), "NHIP26000456", "NH26000099", "Ravi Kumar Sharma", Gender.Male, new DateOnly(1972, 3, 1), Guid.CreateVersion7(), Guid.CreateVersion7(),
        At, At.AddHours(30), DischargeType.Expired, IsMlc: true, vip, categories);

    private static object? Cell(ReportDescriptor report, object?[] row, string key) => row[report.ColumnIndex(key)];

    [Fact]
    public void An_unprotected_patients_critical_result_is_listed_in_full_to_anyone()
    {
        var row = CriticalValuesReport.Row(LabRow(), Nobody);

        Cell(Critical, row, "patient").ShouldBe("Ravi Kumar Sharma");
        Cell(Critical, row, "test").ShouldBe("HIV viral load");
        Cell(Critical, row, "result").ShouldBe("1200000 copies/mL (critical high)");
    }

    [Fact]
    public void A_vip_patient_is_listed_by_initials_with_the_result_withheld_from_staff_who_may_not_open_the_record()
    {
        var row = CriticalValuesReport.Row(LabRow(vip: true), Nobody);

        Cell(Critical, row, "patient").ShouldBe("R. K. S. (restricted)");
        Cell(Critical, row, "uhid").ShouldBe("NH26000099");
        Cell(Critical, row, "test").ShouldBe(PatientView.Withheld);
        Cell(Critical, row, "result").ShouldBe(PatientView.Withheld);

        // How fast they were told is the register's purpose and says nothing of what was found.
        Cell(Critical, row, "minutes").ShouldBe(12m);
        Cell(Critical, row, "informed").ShouldBe("Dr. Meena Raj");
    }

    [Fact]
    public void A_sensitive_category_patient_keeps_their_name_but_the_result_is_withheld_unless_the_viewer_holds_the_category()
    {
        var withheld = CriticalValuesReport.Row(LabRow(categories: SensitiveCategory.Hiv), Nobody);
        Cell(Critical, withheld, "patient").ShouldBe("Ravi Kumar Sharma");
        Cell(Critical, withheld, "test").ShouldBe(PatientView.Withheld);
        Cell(Critical, withheld, "result").ShouldBe(PatientView.Withheld);

        var allowed = CriticalValuesReport.Row(LabRow(categories: SensitiveCategory.Hiv), HivCounsellor);
        Cell(Critical, allowed, "test").ShouldBe("HIV viral load");

        // Holding one category does not open a record that is in another as well.
        var both = CriticalValuesReport.Row(LabRow(categories: SensitiveCategory.Hiv | SensitiveCategory.MentalHealth), HivCounsellor);
        Cell(Critical, both, "result").ShouldBe(PatientView.Withheld);
    }

    [Fact]
    public void Whoever_opens_restricted_records_reads_a_vip_patients_critical_result_in_full()
    {
        var row = CriticalValuesReport.Row(LabRow(vip: true, categories: SensitiveCategory.Hiv), Registrar);

        Cell(Critical, row, "patient").ShouldBe("Ravi Kumar Sharma");
        Cell(Critical, row, "test").ShouldBe("HIV viral load");
    }

    [Fact]
    public void A_critical_imaging_finding_is_withheld_the_same_way()
    {
        var finding = LabRow(vip: true) with { Kind = DiagnosticKind.Radiology, Test = "Large intracranial bleed", NumericValue = null, Unit = null, Flag = null };

        var row = CriticalValuesReport.Row(finding, Nobody);

        Cell(Critical, row, "test").ShouldBe(PatientView.Withheld);
        Cell(Critical, row, "result").ShouldBe(PatientView.Withheld);
    }

    [Fact]
    public void An_unprotected_patients_death_is_listed_in_full()
    {
        var row = AdverseDischargesReport.Row(Stay(), "General Medicine", "Dr. Arun Kumar", Nobody);

        Cell(Adverse, row, "patient").ShouldBe("Ravi Kumar Sharma");
        Cell(Adverse, row, "department").ShouldBe("General Medicine");
        Cell(Adverse, row, "outcome").ShouldNotBe(PatientView.Withheld);
        Cell(Adverse, row, "within48").ShouldBe("Yes");
        Cell(Adverse, row, "mlc").ShouldBe("Yes");
    }

    [Fact]
    public void A_vip_patients_death_is_listed_by_initials_with_the_clinical_columns_withheld()
    {
        var row = AdverseDischargesReport.Row(Stay(vip: true), "General Medicine", "Dr. Arun Kumar", Nobody);

        Cell(Adverse, row, "patient").ShouldBe("R. K. S. (restricted)");
        foreach (var key in new[] { "department", "consultant", "outcome", "within48", "mlc" })
        {
            Cell(Adverse, row, key).ShouldBe(PatientView.Withheld);
        }

        // The stay itself, as on the bed board.
        Cell(Adverse, row, "admissionNumber").ShouldBe("NHIP26000456");
        Cell(Adverse, row, "dischargedAt").ShouldBe(At.AddHours(30));
    }

    [Fact]
    public void A_sensitive_category_patients_lama_keeps_their_name_but_withholds_the_outcome_unless_the_viewer_holds_the_category()
    {
        var stay = Stay(categories: SensitiveCategory.Hiv) with { Outcome = DischargeType.Lama };

        var withheld = AdverseDischargesReport.Row(stay, "ART Centre", "Dr. Arun Kumar", Nobody);
        Cell(Adverse, withheld, "patient").ShouldBe("Ravi Kumar Sharma");
        Cell(Adverse, withheld, "department").ShouldBe(PatientView.Withheld);
        Cell(Adverse, withheld, "outcome").ShouldBe(PatientView.Withheld);

        var allowed = AdverseDischargesReport.Row(stay, "ART Centre", "Dr. Arun Kumar", HivCounsellor);
        Cell(Adverse, allowed, "department").ShouldBe("ART Centre");
    }

    [Fact]
    public async Task A_scheduled_run_with_no_viewer_withholds_protected_patients_and_a_registrar_reads_them()
    {
        var definition = new CriticalValuesReport();
        var arguments = ReportParameterBinder.Bind(definition.Descriptor, null, ReportTestHarness.Today).Value;
        using var db = ReportTestHarness.CreateContext(ReportTestHarness.FacilityId);
        var vip = LabRow(vip: true);

        var scheduled = await definition.RunAsync(
            new ReportContext(db, arguments, ReportTestHarness.Now, new CannedRows<CriticalValuesReport.CriticalRow>(vip), 100), TestContext.Current.CancellationToken);
        var registrar = await definition.RunAsync(
            new ReportContext(db, arguments, ReportTestHarness.Now, new CannedRows<CriticalValuesReport.CriticalRow>(vip), 100, Actor(PatientsPermissions.ReadRestricted)),
            TestContext.Current.CancellationToken);

        Cell(Critical, scheduled.Rows.Single(), "patient").ShouldBe("R. K. S. (restricted)");
        Cell(Critical, scheduled.Rows.Single(), "result").ShouldBe(PatientView.Withheld);
        Cell(Critical, registrar.Rows.Single(), "patient").ShouldBe("Ravi Kumar Sharma");
        Cell(Critical, registrar.Rows.Single(), "test").ShouldBe("HIV viral load");
    }

    [Fact]
    public async Task The_adverse_discharges_register_reads_the_viewers_category_permissions_from_the_run()
    {
        var definition = new AdverseDischargesReport();
        var arguments = ReportParameterBinder.Bind(definition.Descriptor, null, ReportTestHarness.Today).Value;
        using var db = ReportTestHarness.CreateContext(ReportTestHarness.FacilityId);
        var stay = Stay(categories: SensitiveCategory.Hiv);

        var withheld = await definition.RunAsync(
            new ReportContext(db, arguments, ReportTestHarness.Now, new CannedRows<AdverseDischargesReport.RegisterRow>(stay), 100, Actor()),
            TestContext.Current.CancellationToken);
        var held = await definition.RunAsync(
            new ReportContext(db, arguments, ReportTestHarness.Now, new CannedRows<AdverseDischargesReport.RegisterRow>(stay), 100, Actor(PatientsPermissions.SensitiveHiv)),
            TestContext.Current.CancellationToken);

        Cell(Adverse, withheld.Rows.Single(), "outcome").ShouldBe(PatientView.Withheld);
        Cell(Adverse, held.Rows.Single(), "outcome").ShouldNotBe(PatientView.Withheld);
        Cell(Adverse, held.Rows.Single(), "patient").ShouldBe("Ravi Kumar Sharma");
    }

    private static ReportActor Actor(params string[] permissions) =>
        new(Guid.NewGuid(), "u", "User", ReportTestHarness.FacilityId, permissions.ToHashSet());

    /// <summary>Answers the first query that returns <typeparamref name="TRow"/> with the rows given, and every other query with nothing.</summary>
    private sealed class CannedRows<TRow>(params TRow[] rows) : IReportQueryExecutor
    {
        private bool _answered;

        public Task<List<T>> ListAsync<T>(IQueryable<T> query, CancellationToken ct)
        {
            if (typeof(T) != typeof(TRow) || _answered)
            {
                return Task.FromResult(new List<T>());
            }

            _answered = true;
            return Task.FromResult(rows.Cast<T>().ToList());
        }
    }
}
