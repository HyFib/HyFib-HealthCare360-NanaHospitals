using System.Text.Json;

namespace HyFib.HealthCare360.LoadTest.Tests;

/// <summary>
/// The hospital's peak as the load test runs it (<see cref="PeakMix"/>): OPD registration and token, billing receipt, pharmacy
/// counter sale, lab order and result entry, ward MAR administration and the portal's record views, weighted, configurable, and
/// reported per step.
/// </summary>
public sealed class PeakMixTests
{
    private static readonly HashSet<Role> AllRoles = [.. Enum.GetValues<Role>()];

    private static Fixtures FullFixtures() => new()
    {
        DepartmentId = Guid.NewGuid(),
        ServiceItemId = Guid.NewGuid(),
        PharmacyStoreId = Guid.NewGuid(),
        PharmacyItems = [new StockItem(Guid.NewGuid(), 12.5m)],
        LabTestIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
        WardId = Guid.NewGuid(),
    };

    private static OperationContext ContextFor(Fixtures fixtures) => new([Guid.NewGuid()], new Random(11), () => 1, fixtures);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static Operation Step(IEnumerable<Operation> mix, string name) => mix.Single(o => o.Name == name);

    [Fact]
    public void The_peak_covers_registration_billing_pharmacy_the_lab_the_ward_and_the_portal()
    {
        var mix = PeakMix.Build(AllRoles, FullFixtures(), new Dictionary<string, int>(), out var notRun);

        notRun.ShouldBeEmpty();
        mix.Select(o => o.Role).Distinct().Order().ShouldBe([.. Enum.GetValues<Role>()]);
        foreach (var name in new[]
                 {
                     "Register a patient", PeakMix.OpdVisit, PeakMix.BillReceipt, PeakMix.CounterSale, PeakMix.LabOrder, PeakMix.LabResults,
                     PeakMix.MarGiven, PeakMix.PortalMe, PeakMix.PortalRecords,
                 })
        {
            mix.ShouldContain(o => o.Name == name, name);
        }

        mix.ShouldAllBe(o => o.Weight > 0);
        mix.Select(o => o.Name).Distinct().Count().ShouldBe(mix.Count, "a step's name is its row in the results");
    }

    [Fact]
    public void A_role_with_no_accounts_is_left_out_and_the_report_says_which_option_gives_them()
    {
        var mix = PeakMix.Build(new HashSet<Role> { Role.FrontOffice, Role.Billing }, FullFixtures(), new Dictionary<string, int>(), out var notRun);

        mix.Select(o => o.Role).Distinct().ShouldBe([Role.FrontOffice, Role.Billing], ignoreOrder: true);
        notRun.ShouldContain(line => line.StartsWith("Pharmacy:", StringComparison.Ordinal) && line.Contains("--pharmacy-accounts", StringComparison.Ordinal));
        notRun.ShouldContain(line => line.Contains("--portal-tokens", StringComparison.Ordinal));
        notRun.ShouldNotContain(line => line.StartsWith("Front office", StringComparison.Ordinal));
    }

    [Fact]
    public void A_step_the_server_has_nothing_for_is_left_out_with_the_reason()
    {
        var fixtures = FullFixtures();
        fixtures.PharmacyItems = [];
        fixtures.WardId = null;

        var mix = PeakMix.Build(AllRoles, fixtures, new Dictionary<string, int>(), out var notRun);

        mix.ShouldNotContain(o => o.Name == PeakMix.CounterSale);
        mix.ShouldNotContain(o => o.Role == Role.Ward);
        notRun.ShouldContain(line => line.Contains("no medicine in stock", StringComparison.Ordinal));
        notRun.ShouldContain(line => line.StartsWith("Ward:", StringComparison.Ordinal) && line.Contains("--ward", StringComparison.Ordinal));
        mix.ShouldContain(o => o.Name == PeakMix.StockLookup, "the lookup needs only the store");
    }

    [Fact]
    public void Weights_set_on_the_command_line_win_and_zero_leaves_a_step_out()
    {
        var weights = LoadTestOptions.ParseWeights("billing receipt=77, Lab order=0").ShouldNotBeNull();

        var mix = PeakMix.Build(AllRoles, FullFixtures(), weights, out _);

        Step(mix, PeakMix.BillReceipt).Weight.ShouldBe(77);
        mix.ShouldNotContain(o => o.Name == PeakMix.LabOrder);
    }

    [Theory]
    [InlineData("Nonsense=3")]
    [InlineData("Billing receipt")]
    [InlineData("Billing receipt=lots")]
    [InlineData("Billing receipt=-1")]
    [InlineData("")]
    public void Weights_naming_no_step_or_no_number_are_refused(string text) => LoadTestOptions.ParseWeights(text).ShouldBeNull();

    [Fact]
    public void Virtual_users_divide_among_the_roles_by_the_weight_of_their_steps()
    {
        var mix = PeakMix.Build(AllRoles, FullFixtures(), new Dictionary<string, int>(), out _);

        var split = PeakMix.Split(mix, 75);

        split.Values.Sum().ShouldBe(75);
        split.Count.ShouldBe(Enum.GetValues<Role>().Length, "every role has someone");
        split[Role.FrontOffice].ShouldBeGreaterThan(split[Role.Portal], "the registration desk is the busiest");
        PeakMix.Split(mix, 3).Values.Sum().ShouldBe(3);
    }

    [Fact]
    public void With_only_the_registration_desks_accounts_the_run_is_the_one_it_always_was()
    {
        var mix = PeakMix.Build(new HashSet<Role> { Role.FrontOffice }, new Fixtures(), new Dictionary<string, int>(), out _);

        mix.Select(o => o.Name).ShouldBe(Operations.All.Select(o => o.Name), ignoreOrder: true);
        PeakMix.Split(mix, 75).ShouldBe(new Dictionary<Role, int> { [Role.FrontOffice] = 75 });
    }

    [Fact]
    public async Task An_OPD_visit_is_made_for_a_patient_in_the_department_with_a_queue_token()
    {
        var fixtures = FullFixtures();
        var context = ContextFor(fixtures);
        fixtures.PatientRegistered(Guid.NewGuid());

        using var request = Step(PeakMix.Extra(), PeakMix.OpdVisit).Request(context);
        var body = await request.Content.ShouldNotBeNull().ReadFromJsonAsyncForTest(TestContext.Current.CancellationToken);

        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.OriginalString.ShouldBe("api/v1/visits");
        body.GetProperty("departmentId").GetGuid().ShouldBe(fixtures.DepartmentId!.Value);
        body.GetProperty("type").GetString().ShouldBe("OutPatient");
        body.GetProperty("issueToken").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_bill_is_started_given_a_service_finalised_and_then_paid_in_that_order()
    {
        var mix = PeakMix.Extra();
        var context = ContextFor(FullFixtures());
        var start = Step(mix, PeakMix.BillStart);
        var line = Step(mix, PeakMix.BillLine);
        var finalise = Step(mix, PeakMix.BillFinalise);
        var receipt = Step(mix, PeakMix.BillReceipt);
        var billId = Guid.NewGuid();
        var check = Step(mix, PeakMix.ShiftCheck);
        var open = Step(mix, PeakMix.ShiftOpen);

        start.IsAvailable!(context).ShouldBeFalse("no bill before the cashier shift is open");
        open.IsAvailable!(context).ShouldBeFalse("the shift is checked first");
        check.IsAvailable!(context).ShouldBeTrue();
        check.OnSuccess!(context, default);
        check.IsAvailable(context).ShouldBeFalse("checked once");
        open.IsAvailable(context).ShouldBeTrue("no shift was open (no content)");
        open.OnSuccess!(context, default);

        context.Fixtures.PatientRegistered(Guid.NewGuid());
        start.IsAvailable(context).ShouldBeTrue();
        line.IsAvailable!(context).ShouldBeFalse("no bill yet");
        receipt.IsAvailable!(context).ShouldBeFalse("no bill yet");

        start.OnSuccess!(context, Json($"{{\"id\":\"{billId}\",\"version\":3,\"netAmount\":0}}"));
        start.IsAvailable(context).ShouldBeFalse("one bill at a time");
        line.IsAvailable(context).ShouldBeTrue();
        finalise.IsAvailable!(context).ShouldBeFalse("a bill without a service is not finalised");

        line.OnSuccess!(context, Json("{\"version\":4,\"netAmount\":350.5}"));
        finalise.IsAvailable(context).ShouldBeTrue();
        receipt.IsAvailable(context).ShouldBeFalse("a draft is not paid");

        finalise.OnSuccess!(context, Json("{\"version\":5,\"netAmount\":350.5}"));
        receipt.IsAvailable(context).ShouldBeTrue();

        using var request = receipt.Request(context);
        var body = await request.Content.ShouldNotBeNull().ReadFromJsonAsyncForTest(TestContext.Current.CancellationToken);
        request.RequestUri!.OriginalString.ShouldBe("api/v1/billing/receipts");
        body.GetProperty("billId").GetGuid().ShouldBe(billId);
        body.GetProperty("payments")[0].GetProperty("amount").GetDecimal().ShouldBe(350.5m);

        receipt.OnSuccess!(context, default);
        start.IsAvailable(context).ShouldBeTrue("the clerk starts the next bill");
    }

    [Fact]
    public void A_registered_patient_is_queued_once_and_gets_one_visit_and_one_bill()
    {
        var context = ContextFor(FullFixtures());
        var register = Operations.All.Single(o => o.Name == "Register a patient");
        var visit = Step(PeakMix.Extra(), PeakMix.OpdVisit);
        var patient = Guid.NewGuid();

        visit.IsAvailable!(context).ShouldBeFalse("no one registered yet: a patient seen before may have a visit open today (409)");

        register.OnSuccess!(context, Json($"{{\"id\":\"{patient}\",\"uhid\":\"NH1\"}}"));
        visit.IsAvailable(context).ShouldBeTrue();
        using (var request = visit.Request(context))
        {
            request.RequestUri!.OriginalString.ShouldBe("api/v1/visits");
            request.Content.ShouldNotBeNull();
        }

        visit.IsAvailable(context).ShouldBeFalse("each new patient has one visit");
        context.Fixtures.BillPatients.ShouldHaveSingleItem().ShouldBe(patient);
        using var again = visit.Request(context);
        again.Method.ShouldBe(HttpMethod.Get, "with no one queued the step reads, and never repeats a visit");
    }

    [Fact]
    public void A_bill_is_started_for_a_patient_registered_in_the_run_once()
    {
        var mix = PeakMix.Extra();
        var context = ContextFor(FullFixtures());
        context.State["shiftOpen"] = true;
        var start = Step(mix, PeakMix.BillStart);

        start.IsAvailable!(context).ShouldBeFalse("no patient without a bill yet");
        context.Fixtures.PatientRegistered(Guid.NewGuid());
        start.IsAvailable(context).ShouldBeTrue();
        using var request = start.Request(context);
        request.Method.ShouldBe(HttpMethod.Post);
        context.Fixtures.BillPatients.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Counter_sales_take_stock_off_the_shelf_and_stop_when_it_is_sold()
    {
        var fixtures = FullFixtures();
        fixtures.PharmacyItems = [new StockItem(Guid.NewGuid(), 10m, 2)];
        var context = ContextFor(fixtures);
        var sale = Step(PeakMix.Extra(), PeakMix.CounterSale);

        for (var i = 0; i < 2; i++)
        {
            sale.IsAvailable!(context).ShouldBeTrue();
            using var request = sale.Request(context);
            request.Method.ShouldBe(HttpMethod.Post);
        }

        sale.IsAvailable!(context).ShouldBeFalse("the last unit is sold once");
        using var read = sale.Request(context);
        read.Method.ShouldBe(HttpMethod.Get, "a sold-out shelf is read, not sold from again");
    }

    [Fact]
    public async Task A_counter_sale_sells_one_medicine_from_the_store_for_its_price_in_cash()
    {
        var fixtures = FullFixtures();

        using var request = Step(PeakMix.Extra(), PeakMix.CounterSale).Request(ContextFor(fixtures));
        var body = await request.Content.ShouldNotBeNull().ReadFromJsonAsyncForTest(TestContext.Current.CancellationToken);

        request.RequestUri!.OriginalString.ShouldBe("api/v1/pharmacy/sales/counter");
        body.GetProperty("storeId").GetGuid().ShouldBe(fixtures.PharmacyStoreId!.Value);
        body.GetProperty("lines")[0].GetProperty("itemId").GetGuid().ShouldBe(fixtures.PharmacyItems[0].ItemId);
        body.GetProperty("payments")[0].GetProperty("amount").GetDecimal().ShouldBe(12.5m);
        body.GetProperty("confirmControlledDrugs").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_lab_order_is_followed_by_the_sheet_of_a_sample_on_the_worklist_and_its_results()
    {
        var mix = PeakMix.Extra();
        var context = ContextFor(FullFixtures());
        var sample = Guid.NewGuid();
        var pendingResult = Guid.NewGuid();

        using (var order = Step(mix, PeakMix.LabOrder).Request(context))
        {
            var ordered = await order.Content.ShouldNotBeNull().ReadFromJsonAsyncForTest(TestContext.Current.CancellationToken);
            order.RequestUri!.OriginalString.ShouldBe("api/v1/lab/orders");
            ordered.GetProperty("testIds").GetArrayLength().ShouldBe(2);
            ordered.GetProperty("source").GetString().ShouldBe("WalkIn");
        }

        var sheet = Step(mix, PeakMix.LabSheet);
        var results = Step(mix, PeakMix.LabResults);
        sheet.IsAvailable!(context).ShouldBeFalse("nothing on the worklist yet");
        results.IsAvailable!(context).ShouldBeFalse();

        Step(mix, PeakMix.LabWorklist).OnSuccess!(context, Json(
            $"[{{\"sampleId\":\"{sample}\",\"pending\":2}},{{\"sampleId\":\"{Guid.NewGuid()}\",\"pending\":0}}]"));
        context.Fixtures.Samples.Count.ShouldBe(1, "only a sample awaiting results is kept");

        sheet.IsAvailable(context).ShouldBeTrue();
        using (var open = sheet.Request(context))
        {
            open.RequestUri!.OriginalString.ShouldBe($"api/v1/lab/samples/{sample}/results");
        }

        sheet.OnSuccess!(context, Json(
            $"{{\"lines\":[{{\"resultId\":\"{pendingResult}\",\"status\":\"Pending\",\"options\":[]}},{{\"resultId\":\"{Guid.NewGuid()}\",\"status\":\"Entered\",\"options\":[]}}]}}"));
        results.IsAvailable(context).ShouldBeTrue();

        using var enter = results.Request(context);
        var entered = await enter.Content.ShouldNotBeNull().ReadFromJsonAsyncForTest(TestContext.Current.CancellationToken);
        enter.RequestUri!.OriginalString.ShouldBe("api/v1/lab/results");
        var entries = entered.GetProperty("entries");
        entries.GetArrayLength().ShouldBe(1, "only the line still pending");
        entries[0].GetProperty("resultId").GetGuid().ShouldBe(pendingResult);
    }

    [Fact]
    public async Task A_nurse_gives_each_due_dose_once_and_never_one_that_needs_a_witness()
    {
        var mix = PeakMix.Extra();
        var context = ContextFor(FullFixtures());
        var plain = Guid.NewGuid();
        var witnessed = Guid.NewGuid();
        var list = Json(
            $"[{{\"entryId\":\"{plain}\",\"version\":7,\"needsWitness\":false}},{{\"entryId\":\"{witnessed}\",\"version\":2,\"needsWitness\":true}}]");
        var given = Step(mix, PeakMix.MarGiven);

        given.IsAvailable!(context).ShouldBeFalse("no due dose known yet");
        Step(mix, PeakMix.MarDue).OnSuccess!(context, list);
        Step(mix, PeakMix.MarDue).OnSuccess!(context, list);

        context.Fixtures.Doses.Count.ShouldBe(1);
        given.IsAvailable(context).ShouldBeTrue();
        using var request = given.Request(context);
        var body = await request.Content.ShouldNotBeNull().ReadFromJsonAsyncForTest(TestContext.Current.CancellationToken);
        request.RequestUri!.OriginalString.ShouldBe($"api/v1/nursing/mar/{plain}/record");
        body.GetProperty("outcome").GetString().ShouldBe("Given");
        body.GetProperty("version").GetUInt32().ShouldBe(7u);
        given.IsAvailable(context).ShouldBeFalse("the only dose is given");
    }

    [Fact]
    public void A_step_that_is_not_available_is_not_chosen()
    {
        var context = ContextFor(new Fixtures());
        Operation[] operations =
        [
            new("Never now", OperationKind.Read, 1000, _ => new HttpRequestMessage(), IsAvailable: _ => false),
            new("Always", OperationKind.Read, 1, _ => new HttpRequestMessage()),
        ];

        Enumerable.Range(0, 200).ShouldAllBe(_ => Operations.Choose(operations, context).Name == "Always");
    }

    [Fact]
    public void The_portal_steps_ask_the_portal_API_and_the_staff_steps_the_staff_API()
    {
        var context = ContextFor(FullFixtures());

        foreach (var operation in PeakMix.Extra())
        {
            // Steps that need a bill, a sample or a dose in hand are built where the user holds one (the tests above).
            if (operation.IsAvailable is not null || operation.Name == PeakMix.MarGiven)
            {
                continue;
            }

            using var request = operation.Request(context);
            request.RequestUri.ShouldNotBeNull().OriginalString.ShouldStartWith("api/v1/", Case.Sensitive, operation.Name);
        }

        PeakMix.Extra().Where(o => o.Role == Role.Portal).ShouldAllBe(o => o.Kind == OperationKind.Read);
    }

    [Fact]
    public void The_report_shows_each_area_and_what_was_not_run()
    {
        OperationResult[] operations =
        [
            new("Register a patient", OperationKind.Write, 100, 0, 5, 20, 30, 40, 600, "Front office"),
            new("Billing receipt", OperationKind.Write, 50, 5, 10, 700, 800, 900, 600, "Billing"),
            new("Lab order", OperationKind.Write, 40, 40, 0, 0, 0, 0, 600, "Laboratory"),
        ];
        var result = new LoadTestResult(
            new Uri("https://hc360-srv:7360/"), 75, TimeSpan.FromMinutes(5), operations, [], 190, 45, new Dictionary<int, int> { [403] = 45 },
            ["Pharmacy: no accounts given for it (--pharmacy-accounts)."]);

        var report = Results.Markdown(result);

        report.ShouldContain("| Billing receipt | 600 ms (PERF-01) | 50 | 5 | 10 | 700 | 800 | 900 | **no** |");
        report.ShouldContain("| Billing | 50 | 5 | Billing receipt (700) |");
        report.ShouldContain("| Laboratory | 40 | 40 | none succeeded |");
        report.ShouldContain("Not run in this run:");
        report.ShouldContain("- Pharmacy: no accounts given for it (--pharmacy-accounts).");
    }

    [Fact]
    public void Each_step_has_its_own_latency_figures_by_area()
    {
        var mix = PeakMix.Build(AllRoles, FullFixtures(), new Dictionary<string, int>(), out _);
        var samples = Enumerable.Range(1, 10).Select(i => new Sample(PeakMix.CounterSale, OperationKind.Write, TimeSpan.FromMilliseconds(i * 10), 201)).ToList();

        var results = Results.Summarize(mix, samples);

        var sale = results.Single(r => r.Name == PeakMix.CounterSale);
        sale.Area.ShouldBe("Pharmacy");
        sale.Count.ShouldBe(10);
        sale.P95.ShouldBe(100);
        sale.Met.ShouldBeTrue();
        results.Count.ShouldBe(mix.Count, "a row for every step in the mix, run or not");
    }

    [Fact]
    public void The_other_roles_accounts_the_portal_and_the_fixtures_are_read_from_the_command()
    {
        var store = Guid.NewGuid();
        var ward = Guid.NewGuid();
        var (options, problem) = LoadTestOptions.Parse(
            [
                "run", "--server", "https://hc360-srv:7360/", "--accounts", "frontoffice", "--test-server",
                "--billing-accounts", "billing1, billing2", "--pharmacy-accounts", "pharm1", "--lab-accounts", "lab1", "--ward-accounts", "nurse1",
                "--pharmacy-store", store.ToString(), "--ward", ward.ToString(),
                "--portal-tokens", "tokens.txt", "--portal-server", "https://hc360-srv:7361/", "--weights", "Billing receipt=40",
            ],
            name => name == LoadTestOptions.PasswordVariable ? "Load-Test-Pass-1" : null);

        problem.ShouldBeNull();
        options.ShouldNotBeNull();
        options.RoleAccounts[Role.Billing].ShouldBe(["billing1", "billing2"]);
        options.RoleAccounts[Role.Pharmacy].ShouldBe(["pharm1"]);
        options.RoleAccounts[Role.Lab].ShouldBe(["lab1"]);
        options.RoleAccounts[Role.Ward].ShouldBe(["nurse1"]);
        options.PharmacyStore.ShouldBe(store);
        options.Ward.ShouldBe(ward);
        options.PortalTokensPath.ShouldBe("tokens.txt");
        options.PortalServer.ShouldBe(new Uri("https://hc360-srv:7361/"));
        options.Weights["billing receipt"].ShouldBe(40);
    }

    [Fact]
    public void Portal_tokens_need_the_portals_own_address()
    {
        var (options, problem) = LoadTestOptions.Parse(
            ["run", "--server", "https://hc360-srv:7360/", "--accounts", "frontoffice", "--test-server", "--portal-tokens", "tokens.txt"],
            name => name == LoadTestOptions.PasswordVariable ? "Load-Test-Pass-1" : null);

        options.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain("--portal-server");
    }
}

internal static class HttpContentExtensions
{
    public static async Task<JsonElement> ReadFromJsonAsyncForTest(this HttpContent content, CancellationToken cancellationToken) =>
        await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<JsonElement>(content, cancellationToken: cancellationToken);
}
