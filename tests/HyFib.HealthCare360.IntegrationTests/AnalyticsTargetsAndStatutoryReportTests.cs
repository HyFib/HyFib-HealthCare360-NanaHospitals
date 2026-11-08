using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.KpiTargets;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// KPI targets (set by an administrator, shown with the KPIs, breaches told to the owner role) and the tax, payroll and
/// claims reports, run through the API over rows seeded for a month no other test uses (February 2019), with the expected
/// figures worked out by hand in each test.
/// </summary>
public sealed class AnalyticsTargetsAndStatutoryReportTests(HospitalServer server)
{
    private const string Targets = "/api/v1/analytics/kpi-targets";
    private const string February2019 = "period=custom&from=2019-02-01&to=2019-02-28";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_administrator_sets_a_target_the_kpis_show_it_against_the_period_and_its_owners_hear_of_a_breach_once()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        using (var refused = await billing.SendRawAsync(HttpMethod.Get, Targets))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var list = await admin.GetAsync<KpiTargetsDto>(Targets);
        list.Kpis.Select(k => k.Key).ShouldBe(["opVisits", "admissions", "occupancy", "alos", "labTat", "nps", "revenue", "collections"]);
        list.Kpis.Single(k => k.Key == "opVisits").Monthly.ShouldBeTrue();
        list.Kpis.Single(k => k.Key == "occupancy").Monthly.ShouldBeFalse();
        list.Roles.ShouldContain(SystemRoles.HospitalAdmin);

        // Thresholds on the wrong side of the target, and a role that does not exist, are refused.
        await RefusedAsync(admin, "opVisits", new SaveKpiTargetRequest(3000m, 3500m, 2000m, SystemRoles.HospitalAdmin), "amberThreshold");
        await RefusedAsync(admin, "alos", new SaveKpiTargetRequest(4m, 5m, 4.5m, SystemRoles.HospitalAdmin), "redThreshold");
        await RefusedAsync(admin, "opVisits", new SaveKpiTargetRequest(3000m, 2500m, 2000m, "No such role"), "ownerRole");

        // A monthly target of 3,000 OP visits: over February 2019 (28 days) that is 3,000 × 28 × 12 ÷ 365 = 2,761.64.
        var saved = await admin.PutAsync<KpiTargetItem>($"{Targets}/opVisits", new SaveKpiTargetRequest(3000m, 2500m, 2000m, SystemRoles.HospitalAdmin));
        await using var cleanup = new Cleanup(async () =>
        {
            var current = (await admin.GetAsync<KpiTargetsDto>(Targets)).Kpis.Single(k => k.Key == "opVisits").Target;
            if (current is not null)
            {
                await admin.SendRawAsync(HttpMethod.Delete, $"{Targets}/opVisits?version={current.Version}");
            }
        });
        var target = saved.Target.ShouldNotBeNull();
        target.SetByName.ShouldNotBeNullOrWhiteSpace();
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, $"{Targets}/opVisits", new SaveKpiTargetRequest(3100m, 2500m, 2000m, SystemRoles.HospitalAdmin)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a save without the version, over a target that exists, could overwrite someone's change");
        }

        var kpis = await admin.GetAsync<KpiSet>("/api/v1/analytics/kpis?from=2019-02-01&to=2019-02-28");
        var visits = kpis.Kpis.Single(k => k.Key == "opVisits");
        var view = visits.Target.ShouldNotBeNull();
        view.Prorated.ShouldBeTrue();
        view.Target.ShouldBe(2761.64m);
        view.Amber.ShouldBe(2301.37m);
        view.Red.ShouldBe(1841.10m);
        view.DailyTarget.ShouldBe(98.63m);
        view.Status.ShouldBe(KpiStatus.Red, "no visits were recorded in February 2019");
        kpis.Kpis.Single(k => k.Key == "occupancy").Target.ShouldBeNull("only targets that are set are shown");

        // Owners hear when the KPI gets worse, once: a target no month can reach is red at the check.
        target = (await admin.PutAsync<KpiTargetItem>($"{Targets}/opVisits",
            new SaveKpiTargetRequest(1_000_000_000m, 1_000_000_000m, 1_000_000_000m, SystemRoles.HospitalAdmin, target.Version))).Target.ShouldNotBeNull();
        var job = server.Services.GetServices<IHostedService>().OfType<ScheduledReportJob>().Single();
        var before = await BreachNotificationsAsync(admin);
        (await job.CheckKpiTargetsAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        var after = await BreachNotificationsAsync(admin);
        after.Count.ShouldBe(before.Count + 1);
        var notice = after[0];
        notice.Title.ShouldBe("OP visits is red");
        notice.Body.ShouldNotBeNull().ShouldContain("against a target of");
        notice.Link.ShouldBe(KpiTargetMonitor.Link);
        notice.Severity.ShouldBe("error");

        (await job.CheckKpiTargetsAsync(Ct)).ShouldBe(0, "still red: the owner was told already");
        (await BreachNotificationsAsync(admin)).Count.ShouldBe(after.Count);

        var checkedTarget = (await admin.GetAsync<KpiTargetsDto>(Targets)).Kpis.Single(k => k.Key == "opVisits").Target.ShouldNotBeNull();
        checkedTarget.LastStatus.ShouldBe(KpiStatus.Red);
        checkedTarget.LastCheckedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_gst_summary_nets_credit_notes_by_rate_and_code_and_reconciles_to_the_bills()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = await FacilityAsync(admin);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var patientId = await db.Set<Patient>().IgnoreQueryFilters().Select(p => p.Id).FirstAsync(Ct);
            var serviceItemId = await db.Set<ServiceItem>().IgnoreQueryFilters().Select(s => s.Id).FirstAsync(Ct);

            // A bill of 15 Feb 2019: a consultation at 0% (₹1,000) and a procedure at 18% (₹2,000 + ₹180 CGST + ₹180 SGST).
            var bill = new Bill
            {
                FacilityId = facilityId,
                BillNumber = "ITGST" + Guid.NewGuid().ToString("N")[..8],
                Type = BillType.OutPatient,
                Status = BillStatus.Paid,
                PatientId = patientId,
                GrossAmount = 3360m,
                TaxableAmount = 3000m,
                CgstAmount = 180m,
                SgstAmount = 180m,
                TaxAmount = 360m,
                NetAmount = 3360m,
                PatientAmount = 3360m,
                PaidAmount = 3360m,
                FinalizedAt = new DateTimeOffset(2019, 2, 15, 12, 0, 0, TimeSpan.FromMinutes(330)),
            };
            var consultation = Line(bill, serviceItemId, "999311", 0m, 1000m, 0m);
            var procedure = Line(bill, serviceItemId, "999312", 18m, 2000m, 180m);
            bill.Lines.AddRange([consultation, procedure]);
            db.Add(bill);

            // A GST credit note of 20 Feb 2019 takes ₹590 off the procedure: ₹500 taxable value and ₹90 GST.
            var note = new CreditNote
            {
                FacilityId = facilityId,
                Number = "ITCN" + Guid.NewGuid().ToString("N")[..8],
                Kind = CreditNoteKind.CreditNote,
                BillId = bill.Id,
                PatientId = patientId,
                Amount = 590m,
                Reason = "Rate corrected",
                IssuedAt = new DateTimeOffset(2019, 2, 20, 12, 0, 0, TimeSpan.FromMinutes(330)),
            };
            note.Credit(procedure, 0m, 590m, interState: false, timeBarred: false);
            db.Add(note);
            await db.SaveChangesAsync(Ct);
        }

        var result = await RunAsync(admin, "gst-summary", $"{February2019}&source=hospital");
        var invoice18 = result.Row("Invoices (bills)", 18m, "999312");
        result.Money(invoice18, "taxable").ShouldBe(2000m);
        result.Money(invoice18, "gst").ShouldBe(360m);
        var credit18 = result.Row("Credit notes", 18m, "999312");
        result.Money(credit18, "taxable").ShouldBe(-500m);
        result.Money(credit18, "cgst").ShouldBe(-45m);
        result.Money(credit18, "sgst").ShouldBe(-45m);
        result.Money(credit18, "value").ShouldBe(-590m);
        result.Money(result.Row("Invoices (bills)", 0m, "999311"), "taxable").ShouldBe(1000m);
        result.Figure("taxable").ShouldBe(2500m);
        result.Figure("gst").ShouldBe(270m);
        result.Text("reconciled").ShouldBe("Yes");
        result.Notes.ShouldContain(n => n.StartsWith("Hospital bills: the lines add up", StringComparison.Ordinal));

        // By rate only, the codes fold together.
        var byRate = await RunAsync(admin, "gst-summary", $"{February2019}&source=hospital&groupBy=rate");
        byRate.Rows.ShouldAllBe(r => r[3].GetString() == "All");
    }

    [Fact]
    public async Task The_claims_reports_age_open_claims_and_give_the_settlement_ratio_and_the_reasons_for_deductions()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = await FacilityAsync(admin);
        Guid payerId;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var patientId = await db.Set<Patient>().IgnoreQueryFilters().Select(p => p.Id).FirstAsync(Ct);
            var payer = new Payer { Code = "ITINS" + Guid.NewGuid().ToString("N")[..6], Name = "IT Claims Insurer " + Guid.NewGuid().ToString("N")[..6], Category = PayerCategory.Insurer };
            db.Add(payer);
            var bill = new Bill
            {
                FacilityId = facilityId,
                BillNumber = "ITCL" + Guid.NewGuid().ToString("N")[..8],
                Type = BillType.InPatient,
                Status = BillStatus.Finalized,
                PatientId = patientId,
                GrossAmount = 250000m,
                NetAmount = 250000m,
                PayerAmount = 250000m,
                PayerId = payer.Id,
                PayerType = PayerType.Insurance,
                FinalizedAt = new DateTimeOffset(2019, 2, 1, 12, 0, 0, TimeSpan.FromMinutes(330)),
            };
            db.Add(bill);
            InsuranceClaim Claim(int day, decimal claimed, ClaimStatus status) => new()
            {
                FacilityId = facilityId,
                ClaimNumber = "ITC" + Guid.NewGuid().ToString("N")[..10],
                BillId = bill.Id,
                PatientId = patientId,
                PayerId = payer.Id,
                ClaimedAmount = claimed,
                Status = status,
                SubmittedAt = new DateTimeOffset(2019, 2, day, 12, 0, 0, TimeSpan.FromMinutes(330)),
            };

            // Settled: ₹1,00,000 claimed, ₹90,000 paid ten days later, ₹10,000 deducted for non-medical items.
            var settled = Claim(5, 100000m, ClaimStatus.Settled);
            settled.SettledAmount = 90000m;
            settled.DeductionAmount = 10000m;
            settled.SettledAt = settled.SubmittedAt.AddDays(10);
            settled.Deductions.Add(new ClaimDeduction { ClaimId = settled.Id, Reason = "Non-medical items", Amount = 10000m });

            // Rejected: ₹50,000 for a policy exclusion.
            var rejected = Claim(6, 50000m, ClaimStatus.Rejected);
            rejected.RejectionReason = "Policy exclusion";
            rejected.RejectedAt = rejected.SubmittedAt.AddDays(7);

            // Open: ₹40,000 submitted, and ₹60,000 partly settled (₹30,000 paid, ₹5,000 deducted, the same reason typed otherwise).
            var open = Claim(7, 40000m, ClaimStatus.Submitted);
            var partly = Claim(8, 60000m, ClaimStatus.PartiallySettled);
            partly.SettledAmount = 30000m;
            partly.DeductionAmount = 5000m;
            partly.Deductions.Add(new ClaimDeduction { ClaimId = partly.Id, Reason = "  non-medical   items ", Amount = 5000m });
            db.AddRange(settled, rejected, open, partly);
            await db.SaveChangesAsync(Ct);
            payerId = payer.Id;
        }

        // Settlement: 4 claims of ₹2,50,000; ratio by claims 1 ÷ (1 + 1) = 50%; by amount ₹90,000 ÷ (₹1,00,000 + ₹50,000) = 60%.
        var settlement = await RunAsync(admin, "claims-settlement", $"{February2019}&payerId={payerId}");
        var insurer = settlement.Rows.ShouldHaveSingleItem();
        settlement.Int(insurer, "claims").ShouldBe(4);
        settlement.Money(insurer, "claimed").ShouldBe(250000m);
        settlement.Int(insurer, "settled").ShouldBe(1);
        settlement.Int(insurer, "rejected").ShouldBe(1);
        settlement.Int(insurer, "open").ShouldBe(2);
        settlement.Money(insurer, "paid").ShouldBe(120000m);
        settlement.Money(insurer, "deducted").ShouldBe(15000m);
        settlement.Money(insurer, "ratioCount").ShouldBe(50.0m);
        settlement.Money(insurer, "ratioAmount").ShouldBe(60.0m);
        settlement.Money(insurer, "days").ShouldBe(10.0m);

        // Ageing (as of today, years later): ₹40,000 + (₹60,000 − ₹30,000 − ₹5,000) = ₹65,000 over 180 days, on 2 claims.
        var ageing = await RunAsync(admin, "claims-ageing", $"payerId={payerId}");
        var aged = ageing.Rows.ShouldHaveSingleItem();
        ageing.Int(aged, "claims").ShouldBe(2);
        ageing.Money(aged, "d181").ShouldBe(65000m);
        ageing.Money(aged, "d0").ShouldBe(0m);
        ageing.Money(aged, "total").ShouldBe(65000m);

        // Reasons: the two spellings of "non-medical items" are one reason (₹15,000 on 2 claims); the rejection ₹50,000.
        var reasons = await RunAsync(admin, "claims-deductions", $"{February2019}&payerId={payerId}");
        reasons.Rows.Count.ShouldBe(2);
        var rejection = reasons.Rows[0];
        reasons.Text(rejection, "kind").ShouldBe("Rejection");
        reasons.Text(rejection, "reason").ShouldBe("Policy exclusion");
        reasons.Money(rejection, "amount").ShouldBe(50000m);
        var deduction = reasons.Rows[1];
        reasons.Text(deduction, "reason").ShouldBe("Non-medical items");
        reasons.Int(deduction, "claims").ShouldBe(2);
        reasons.Money(deduction, "amount").ShouldBe(15000m);
        reasons.Money(deduction, "share").ShouldBe(23.1m);
    }

    [Fact]
    public async Task Payroll_cost_and_tds_on_salaries_add_up_the_approved_payslips_of_the_months_in_the_period()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = await FacilityAsync(admin);
        var department = "IT Payroll " + Guid.NewGuid().ToString("N")[..6];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var employees = await db.Set<Employee>().IgnoreQueryFilters().Where(e => e.FacilityId == facilityId).Select(e => new { e.Id, e.EmployeeCode, e.FullName }).Take(2).ToListAsync(Ct);
            employees.Count.ShouldBe(2, "the test server seeds employees");
            var run = new PayrollRun { FacilityId = facilityId, Year = 2019, Month = 2, Status = PayrollRunStatus.Approved, EmployeeCount = 2 };
            db.Add(run);

            // ₹30,000 gross (basic ₹20,000, HRA ₹10,000), employer PF ₹1,800, TDS ₹1,000: costs ₹31,800.
            var first = Payslip(run, employees[0].Id, employees[0].EmployeeCode, employees[0].FullName, department, 30000m, 1800m, 0m, 1000m);
            first.Lines.AddRange([Earning(first, "BASIC", "Basic", 20000m), Earning(first, "HRA", "House rent allowance", 10000m)]);

            // ₹15,000 gross (basic), employer PF ₹1,800, employer ESI ₹487.50, no TDS: costs ₹17,287.50.
            var second = Payslip(run, employees[1].Id, employees[1].EmployeeCode, employees[1].FullName, department, 15000m, 1800m, 487.50m, 0m);
            second.Lines.Add(Earning(second, "BASIC", "Basic", 15000m));
            db.AddRange(first, second);
            await db.SaveChangesAsync(Ct);
        }

        var byDepartment = await RunAsync(admin, "payroll-cost", February2019);
        var row = byDepartment.Rows.Single(r => r[0].GetString() == department);
        byDepartment.Int(row, "employees").ShouldBe(2);
        byDepartment.Money(row, "earnings").ShouldBe(45000m);
        byDepartment.Money(row, "pf").ShouldBe(3600m);
        byDepartment.Money(row, "esi").ShouldBe(487.50m);
        byDepartment.Money(row, "cost").ShouldBe(49087.50m);
        byDepartment.Columns[0].Label.ShouldBe("Department");

        var byComponent = await RunAsync(admin, "payroll-cost", $"{February2019}&groupBy=component");
        byComponent.Columns[0].Label.ShouldBe("Salary component");
        byComponent.Money(byComponent.Rows.Single(r => r[0].GetString() == "Basic (BASIC)"), "earnings").ShouldBe(35000m);
        byComponent.Int(byComponent.Rows.Single(r => r[0].GetString() == "Basic (BASIC)"), "employees").ShouldBe(2);
        byComponent.Money(byComponent.Rows.Single(r => r[0].GetString() == "Employer PF contribution"), "pf").ShouldBe(3600m);
        byComponent.Money(byComponent.Rows.Single(r => r[0].GetString() == "Employer ESI contribution"), "esi").ShouldBe(487.50m);
        byComponent.Rows.Sum(r => byComponent.Money(r, "cost")).ShouldBe(49087.50m, "the components add up to the same cost");

        var byCentre = await RunAsync(admin, "payroll-cost", $"{February2019}&groupBy=costCentre");
        byCentre.Rows.Sum(r => byCentre.Money(r, "cost")).ShouldBe(49087.50m);

        // TDS on salaries (192): ₹1,000 on ₹45,000 paid to 2 employees, 2.22%.
        var tds = await RunAsync(admin, "tds-summary", February2019);
        var salaries = tds.Rows.Single(r => r[0].GetString() == "192");
        tds.Int(salaries, "deductees").ShouldBe(2);
        tds.Money(salaries, "amount").ShouldBe(45000m);
        tds.Money(salaries, "tds").ShouldBe(1000m);
        tds.Money(salaries, "rate").ShouldBe(2.22m);
        tds.Notes.ShouldContain(n => n.StartsWith("Vendor payments", StringComparison.Ordinal));
    }

    private static BillLine Line(Bill bill, Guid serviceItemId, string sac, decimal rate, decimal taxable, decimal halfTax) => new()
    {
        BillId = bill.Id,
        ChargeId = Guid.NewGuid(),
        ServiceItemId = serviceItemId,
        ServiceCode = sac,
        Description = "Service " + sac,
        Category = ServiceCategory.Procedure,
        SacCode = sac,
        Quantity = 1m,
        UnitPrice = taxable,
        GrossAmount = taxable + (2 * halfTax),
        TaxRate = rate,
        TaxableAmount = taxable,
        CgstAmount = halfTax,
        SgstAmount = halfTax,
        TaxAmount = 2 * halfTax,
        NetAmount = taxable + (2 * halfTax),
        ServiceDate = bill.FinalizedAt!.Value,
    };

    private static Payslip Payslip(PayrollRun run, Guid employeeId, string code, string name, string department, decimal gross, decimal pf, decimal esi, decimal tds) => new()
    {
        FacilityId = run.FacilityId,
        PayrollRunId = run.Id,
        EmployeeId = employeeId,
        Year = run.Year,
        Month = run.Month,
        EmployeeCode = code,
        EmployeeName = name,
        DepartmentName = department,
        DaysInMonth = 28,
        PayableDays = 28,
        StandardGross = gross,
        GrossEarnings = gross,
        TotalDeductions = tds,
        NetPay = gross - tds,
        EmployerPf = pf,
        EmployerEsi = esi,
        Tds = tds,
    };

    private static PayslipLine Earning(Payslip slip, string code, string name, decimal amount) =>
        new() { PayslipId = slip.Id, Code = code, Name = name, Kind = SalaryComponentKind.Earning, FullAmount = amount, Amount = amount };

    private static async Task<Guid> FacilityAsync(ApiClient client) =>
        (await client.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("The hospital administrator is signed in to no facility.");

    private static async Task<List<UserNotificationDto>> BreachNotificationsAsync(ApiClient client) =>
        [.. (await client.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=100")).Items.Where(n => n.Title == "OP visits is red")];

    private static async Task RefusedAsync(ApiClient client, string key, SaveKpiTargetRequest request, string field)
    {
        using var response = await client.SendRawAsync(HttpMethod.Put, $"{Targets}/{key}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(field);
    }

    private static async Task<ReportRun> RunAsync(ApiClient client, string reportId, string query) =>
        await client.GetAsync<ReportRun>($"/api/v1/analytics/reports/{reportId}/run?{query}&pageSize=500");

    private sealed record ReportColumnRun(string Key, string Label);

    private sealed record ReportFigureRun(string Key, JsonElement Value);

    private sealed record ReportRun(List<ReportColumnRun> Columns, List<JsonElement[]> Rows, List<ReportFigureRun> Summary, List<string> Notes)
    {
        private int Index(string key) => Columns.FindIndex(c => c.Key == key) is var i and >= 0 ? i : throw new InvalidOperationException($"No column {key}.");

        public decimal Money(JsonElement[] row, string key) => row[Index(key)].GetDecimal();

        public int Int(JsonElement[] row, string key) => row[Index(key)].GetInt32();

        public string? Text(JsonElement[] row, string key) => row[Index(key)].GetString();

        public decimal Figure(string key) => Summary.Single(f => f.Key == key).Value.GetDecimal();

        public string? Text(string figure) => Summary.Single(f => f.Key == figure).Value.GetString();

        public JsonElement[] Row(string document, decimal rate, string code) =>
            Rows.Single(r => r[Index("document")].GetString() == document && r[Index("rate")].GetDecimal() == rate && r[Index("code")].GetString() == code);
    }

    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action();
    }
}
