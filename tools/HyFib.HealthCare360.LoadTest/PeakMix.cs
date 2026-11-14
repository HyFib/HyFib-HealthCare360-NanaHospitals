using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace HyFib.HealthCare360.LoadTest;

/// <summary>
/// Who takes a step. Each virtual user has one role for the whole run, as staff keep to their desks: the registration desk
/// does not give medicines. A role's users sign in with the accounts the run is given for it (<see cref="LoadTestOptions"/>); a
/// role with none is left out of the mix and the report says so.
/// </summary>
internal enum Role
{
    FrontOffice,
    Billing,
    Pharmacy,
    Lab,
    Ward,
    Portal,
}

/// <summary>A medicine the pharmacy counter can sell now: in stock, no prescription needed, with its price.</summary>
internal sealed record StockItem(Guid ItemId, decimal Mrp);

/// <summary>A dose due on a ward that a nurse may give without a witness.</summary>
internal sealed record DueDose(Guid EntryId, uint Version);

/// <summary>
/// What the server has for the steps to use, found before the run (<see cref="FixtureDiscovery"/>), and the doses and samples
/// that steps of one user leave for steps of another (a ward's due list read by one nurse, a dose given by the next).
/// </summary>
internal sealed class Fixtures
{
    /// <summary>The most doses and samples kept waiting.</summary>
    private const int PoolLimit = 1000;

    private readonly ConcurrentDictionary<Guid, byte> _seen = new();

    public Guid? DepartmentId { get; set; }

    public Guid? ServiceItemId { get; set; }

    public Guid? PharmacyStoreId { get; set; }

    public IReadOnlyList<StockItem> PharmacyItems { get; set; } = [];

    public IReadOnlyList<Guid> LabTestIds { get; set; } = [];

    public Guid? WardId { get; set; }

    public ConcurrentQueue<DueDose> Doses { get; } = new();

    public ConcurrentQueue<Guid> Samples { get; } = new();

    /// <summary>Keeps a dose for a nurse to give, once: a dose already seen (or given) is not kept again.</summary>
    public void KeepDose(Guid entryId, uint version)
    {
        if (Doses.Count < PoolLimit && _seen.TryAdd(entryId, 0))
        {
            Doses.Enqueue(new DueDose(entryId, version));
        }
    }

    /// <summary>Keeps a sample waiting for its results to be entered, once.</summary>
    public void KeepSample(Guid sampleId)
    {
        if (Samples.Count < PoolLimit && _seen.TryAdd(sampleId, 0))
        {
            Samples.Enqueue(sampleId);
        }
    }
}

/// <summary>A bill a billing clerk is making: drafted, given a line, finalised, and then paid.</summary>
internal sealed class BillInProgress(Guid id)
{
    public Guid Id => id;

    public uint Version { get; set; }

    public decimal NetAmount { get; set; }

    public bool HasLine { get; set; }

    public bool Finalised { get; set; }
}

/// <summary>Reading what the server answered, tolerant of the case of names and of enums as numbers or words.</summary>
internal static class Answer
{
    public static JsonElement? Prop(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
        }

        return null;
    }

    public static Guid? Id(JsonElement element, string name = "id") =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } text && Guid.TryParse(text.GetString(), out var id) ? id : null;

    public static uint Version(JsonElement element) =>
        Prop(element, "version") is { ValueKind: JsonValueKind.Number } number && number.TryGetUInt32(out var version) ? version : 0;

    public static decimal Number(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetDecimal(out var value) ? value : 0;

    public static bool Is(JsonElement element, string name, string word, int number) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString(), word, StringComparison.OrdinalIgnoreCase),
        { ValueKind: JsonValueKind.Number } n => n.TryGetInt32(out var value) && value == number,
        _ => false,
    };

    /// <summary>The rows of a list as the server sends it: a page (<c>items</c>) or a plain array.</summary>
    public static IEnumerable<JsonElement> Rows(JsonElement list) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray()
        : Prop(list, "items") is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray()
        : [];
}

/// <summary>
/// The hospital's peak hour as a mix of steps (SRS §7.3 is stated at 75 concurrent users): OPD registration and the token, the
/// billing receipt, the pharmacy counter sale, a lab order and its result entry, the ward's medication round (MAR), and the
/// patient portal's sign-in check and record views. <see cref="Defaults"/> weighs them; <c>--weights</c> changes any weight,
/// to 0 to leave a step out. The registration desk's own steps are <see cref="Operations.All"/>.
/// </summary>
internal static class PeakMix
{
    /// <summary>
    /// Weights of the steps that are not the registration desk's own, which keep theirs in <see cref="Operations.All"/>. The
    /// totals per role (about 130 registration, 50 billing, 50 pharmacy, 60 lab, 50 ward, 50 portal) say how the virtual
    /// users divide among the roles.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Defaults { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [OpdVisit] = 30,
        [BillFind] = 8,
        [BillStart] = 12,
        [BillLine] = 12,
        [BillFinalise] = 8,
        [BillReceipt] = 20,
        [StockLookup] = 20,
        [CounterSale] = 30,
        [LabWorklist] = 12,
        [LabOrder] = 18,
        [LabSheet] = 12,
        [LabResults] = 18,
        [MarDue] = 20,
        [MarGiven] = 30,
        [PortalMe] = 5,
        [PortalRecords] = 20,
        [PortalReports] = 12,
        [PortalBills] = 8,
        [PortalAppointments] = 5,
    };

    public const string OpdVisit = "OPD visit and token";
    public const string BillFind = "Find a bill";
    public const string BillStart = "Start a bill";
    public const string BillLine = "Add a service to the bill";
    public const string BillFinalise = "Finalise the bill";
    public const string BillReceipt = "Billing receipt";
    public const string StockLookup = "Pharmacy stock lookup";
    public const string CounterSale = "Pharmacy counter sale";
    public const string LabWorklist = "Lab worklist";
    public const string LabOrder = "Lab order";
    public const string LabSheet = "Open a result sheet";
    public const string LabResults = "Lab result entry";
    public const string MarDue = "Ward medication round (due doses)";
    public const string MarGiven = "Give a dose (MAR)";
    public const string PortalMe = "Portal sign-in check";
    public const string PortalRecords = "Portal health records";
    public const string PortalReports = "Portal lab and imaging reports";
    public const string PortalBills = "Portal bills";
    public const string PortalAppointments = "Portal appointments";

    /// <summary>
    /// The mix for a run: every step the run's roles and the server's fixtures allow, with <paramref name="weights"/> (by step
    /// name, any case) over the defaults, a weight of 0 leaving the step out. <paramref name="notRun"/> says, for each area left
    /// out, why.
    /// </summary>
    public static IReadOnlyList<Operation> Build(
        IReadOnlySet<Role> roles, Fixtures fixtures, IReadOnlyDictionary<string, int> weights, out IReadOnlyList<string> notRun)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(weights);
        var skipped = new List<string>();
        var mix = new List<Operation>();
        foreach (var operation in Operations.All.Concat(Extra()))
        {
            var weight = weights.TryGetValue(operation.Name, out var set) ? set : operation.Weight;
            if (weight <= 0)
            {
                continue;
            }

            if (!roles.Contains(operation.Role))
            {
                continue;
            }

            if (Missing(operation, fixtures) is { } reason)
            {
                if (!skipped.Exists(s => s.StartsWith(operation.Area + ":", StringComparison.Ordinal)))
                {
                    skipped.Add($"{operation.Area}: {reason}");
                }

                continue;
            }

            mix.Add(operation with { Weight = weight });
        }

        foreach (var role in Enum.GetValues<Role>().Where(r => !roles.Contains(r) && r != Role.FrontOffice))
        {
            skipped.Add($"{AreaOf(role)}: no accounts given for it ({OptionFor(role)}).");
        }

        notRun = skipped;
        return mix;
    }

    /// <summary>Why <paramref name="operation"/> cannot run on this server, or null when it can.</summary>
    internal static string? Missing(Operation operation, Fixtures f) => operation.Name switch
    {
        OpdVisit when f.DepartmentId is null => "no department found for the visit (the registration desk may not read the department list).",
        BillStart or BillLine or BillFinalise or BillReceipt when f.ServiceItemId is null => "no active billable service found.",
        StockLookup or CounterSale when f.PharmacyStoreId is null => "no pharmacy store found; give one with --pharmacy-store.",
        CounterSale when f.PharmacyItems.Count == 0 => "no medicine in stock that sells without a prescription.",
        LabOrder when f.LabTestIds.Count == 0 => "no lab tests found to order.",
        MarDue or MarGiven when f.WardId is null => "no ward found; give one with --ward.",
        _ => null,
    };

    public static string AreaOf(Role role) => role switch
    {
        Role.FrontOffice => "Front office",
        Role.Billing => "Billing",
        Role.Pharmacy => "Pharmacy",
        Role.Lab => "Laboratory",
        Role.Ward => "Ward",
        _ => "Patient portal",
    };

    public static string OptionFor(Role role) => role switch
    {
        Role.FrontOffice => "--accounts",
        Role.Billing => "--billing-accounts",
        Role.Pharmacy => "--pharmacy-accounts",
        Role.Lab => "--lab-accounts",
        Role.Ward => "--ward-accounts",
        _ => "--portal-tokens",
    };

    /// <summary>
    /// How many of <paramref name="users"/> take each role: by the roles' total weights, every role in the mix with at least
    /// one while there are users enough.
    /// </summary>
    public static IReadOnlyDictionary<Role, int> Split(IReadOnlyList<Operation> mix, int users)
    {
        var weights = mix.GroupBy(o => o.Role).ToDictionary(g => g.Key, g => g.Sum(o => o.Weight));
        var total = weights.Values.Sum();
        var split = new Dictionary<Role, int>();
        if (total == 0)
        {
            return split;
        }

        foreach (var (role, weight) in weights.OrderByDescending(p => p.Value))
        {
            split[role] = Math.Max(users >= weights.Count ? 1 : 0, (int)Math.Round((double)users * weight / total));
        }

        // Rounding: the largest role gives or takes what is over or short.
        var largest = split.OrderByDescending(p => p.Value).First().Key;
        split[largest] = Math.Max(1, split[largest] + users - split.Values.Sum());
        return split.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
    }

    /// <summary>The steps beyond the registration desk's own (<see cref="Operations.All"/>).</summary>
    internal static IReadOnlyList<Operation> Extra() =>
    [
        // OPD registration and token: the new patient (Operations.Register) is then given a visit and its queue token.
        new(OpdVisit, OperationKind.Write, Defaults[OpdVisit], OpdVisitRequest, Role.FrontOffice, "OPD registration and token"),

        new(BillFind, OperationKind.Read, Defaults[BillFind], _ => Get("api/v1/billing/bills?pageSize=25"), Role.Billing, "Billing"),
        new(BillStart, OperationKind.Write, Defaults[BillStart], c => Post("api/v1/billing/bills", new { patientId = c.AnyPatient() }), Role.Billing, "Billing",
            c => !c.State.ContainsKey("bill"), OnBillStarted),
        new(BillLine, OperationKind.Write, Defaults[BillLine], BillLineRequest, Role.Billing, "Billing",
            c => c.State.TryGetValue("bill", out var b) && b is BillInProgress { HasLine: false }, OnBillChanged),
        new(BillFinalise, OperationKind.Write, Defaults[BillFinalise], c => Post($"api/v1/billing/bills/{Bill(c).Id}/finalize", new { version = Bill(c).Version }), Role.Billing, "Billing",
            c => c.State.TryGetValue("bill", out var b) && b is BillInProgress { HasLine: true, Finalised: false }, OnBillFinalised),
        new(BillReceipt, OperationKind.Write, Defaults[BillReceipt], ReceiptRequest, Role.Billing, "Billing",
            c => c.State.TryGetValue("bill", out var b) && b is BillInProgress { Finalised: true }, (c, _) => c.State.Remove("bill")),

        new(StockLookup, OperationKind.Search, Defaults[StockLookup], c => Get(
            $"api/v1/pharmacy/stock?storeId={c.Fixtures.PharmacyStoreId}&search={Uri.EscapeDataString(c.Pick(["Para", "Amox", "Cetri", "Metf", "Omep", "Azith"]))}&inStockOnly=true&take=20"),
            Role.Pharmacy, "Pharmacy"),
        new(CounterSale, OperationKind.Write, Defaults[CounterSale], CounterSaleRequest, Role.Pharmacy, "Pharmacy"),

        new(LabWorklist, OperationKind.Read, Defaults[LabWorklist], _ => Get("api/v1/lab/worklist"), Role.Lab, "Laboratory", null, OnWorklist),
        new(LabOrder, OperationKind.Write, Defaults[LabOrder], LabOrderRequest, Role.Lab, "Laboratory"),
        new(LabSheet, OperationKind.Read, Defaults[LabSheet], c => Get($"api/v1/lab/samples/{c.State["sample"]}/results"), Role.Lab, "Laboratory",
            c => !c.State.ContainsKey("sheet") && TakeSample(c), OnSheet),
        new(LabResults, OperationKind.Write, Defaults[LabResults], LabResultsRequest, Role.Lab, "Laboratory",
            c => c.State.ContainsKey("sheet"), (c, _) => c.State.Remove("sheet")),

        new(MarDue, OperationKind.Read, Defaults[MarDue], c => Get($"api/v1/nursing/wards/{c.Fixtures.WardId}/mar/due?withinMinutes=240"), Role.Ward, "Ward", null, OnMarDue),
        new(MarGiven, OperationKind.Write, Defaults[MarGiven], MarGivenRequest, Role.Ward, "Ward", c => !c.Fixtures.Doses.IsEmpty),

        new(PortalMe, OperationKind.Read, Defaults[PortalMe], _ => Get("api/v1/auth/me"), Role.Portal, "Patient portal"),
        new(PortalRecords, OperationKind.Read, Defaults[PortalRecords], _ => Get("api/v1/portal/health-records"), Role.Portal, "Patient portal"),
        new(PortalReports, OperationKind.Read, Defaults[PortalReports], _ => Get("api/v1/portal/reports"), Role.Portal, "Patient portal"),
        new(PortalBills, OperationKind.Read, Defaults[PortalBills], _ => Get("api/v1/portal/bills"), Role.Portal, "Patient portal"),
        new(PortalAppointments, OperationKind.Read, Defaults[PortalAppointments], _ => Get("api/v1/portal/appointments"), Role.Portal, "Patient portal"),
    ];

    private static BillInProgress Bill(OperationContext c) => (BillInProgress)c.State["bill"];

    private static HttpRequestMessage OpdVisitRequest(OperationContext c) => Post("api/v1/visits", new
    {
        patientId = c.AnyPatient(),
        type = "OutPatient",
        departmentId = c.Fixtures.DepartmentId,
        practitionerId = (Guid?)null,
        appointmentId = (Guid?)null,
        payerType = "Self",
        payerId = (Guid?)null,
        referralSource = "Self",
        referredBy = (string?)null,
        chiefComplaint = "Fever for two days",
        isMlc = false,
        mlcNumber = (string?)null,
        notes = "Registered by the load test.",
        priority = "Normal",
        issueToken = true,
        notifyPatient = false,
    });

    private static void OnBillStarted(OperationContext c, JsonElement bill)
    {
        if (Answer.Id(bill) is { } id)
        {
            c.State["bill"] = new BillInProgress(id) { Version = Answer.Version(bill), NetAmount = Answer.Number(bill, "netAmount") };
        }
    }

    private static HttpRequestMessage BillLineRequest(OperationContext c) => Post($"api/v1/billing/bills/{Bill(c).Id}/lines", new
    {
        serviceItemId = c.Fixtures.ServiceItemId,
        quantity = 1,
        unitPrice = (decimal?)null,
        description = (string?)null,
        practitionerId = (Guid?)null,
        departmentId = (Guid?)null,
        version = Bill(c).Version,
    });

    private static void OnBillChanged(OperationContext c, JsonElement bill)
    {
        var current = Bill(c);
        current.Version = Answer.Version(bill) is var version and > 0 ? version : current.Version;
        current.NetAmount = Answer.Number(bill, "netAmount");
        current.HasLine = true;
    }

    private static void OnBillFinalised(OperationContext c, JsonElement bill)
    {
        var current = Bill(c);
        current.Version = Answer.Version(bill) is var version and > 0 ? version : current.Version;
        current.NetAmount = Answer.Number(bill, "netAmount") is var net and > 0 ? net : current.NetAmount;
        current.Finalised = true;
    }

    private static HttpRequestMessage ReceiptRequest(OperationContext c) => Post("api/v1/billing/receipts", new
    {
        billId = Bill(c).Id,
        payments = new[]
        {
            new
            {
                mode = "Cash",
                amount = Bill(c).NetAmount,
                reference = (string?)null,
                cardLast4 = (string?)null,
                cardNetwork = (string?)null,
                chequeNumber = (string?)null,
                chequeBank = (string?)null,
                chequeDate = (DateOnly?)null,
            },
        },
        notes = "Collected by the load test.",
    });

    private static HttpRequestMessage CounterSaleRequest(OperationContext c)
    {
        var item = c.Pick(c.Fixtures.PharmacyItems);
        return Post("api/v1/pharmacy/sales/counter", new
        {
            storeId = c.Fixtures.PharmacyStoreId,
            patientId = (Guid?)null,
            customerName = "Walk-in customer",
            customerPhone = (string?)null,
            prescription = (object?)null,
            lines = new[] { new { itemId = item.ItemId, quantity = 1, batchId = (Guid?)null } },
            discountPercent = 0,
            payments = new[] { new { mode = "Cash", amount = item.Mrp, reference = (string?)null } },
            confirmControlledDrugs = false,
        });
    }

    private static HttpRequestMessage LabOrderRequest(OperationContext c) => Post("api/v1/lab/orders", new
    {
        patientId = c.AnyPatient(),
        visitId = (Guid?)null,
        testIds = c.Fixtures.LabTestIds.OrderBy(_ => c.Random.Next()).Take(Math.Min(2, c.Fixtures.LabTestIds.Count)).ToList(),
        priority = "Routine",
        source = "WalkIn",
        orderingPractitionerId = (Guid?)null,
        referringDoctorName = "Self",
        clinicalNotes = "Ordered by the load test.",
    });

    /// <summary>The samples on the bench's worklist that still await results: kept for the next sheet to open.</summary>
    private static void OnWorklist(OperationContext c, JsonElement list)
    {
        foreach (var row in Answer.Rows(list))
        {
            if (Answer.Id(row, "sampleId") is { } sample && Answer.Number(row, "pending") > 0)
            {
                c.Fixtures.KeepSample(sample);
            }
        }
    }

    /// <summary>Takes a sample off the pool for this user to open, when there is one (its id waits in the user's state until the sheet is read).</summary>
    private static bool TakeSample(OperationContext c)
    {
        if (c.State.ContainsKey("sample"))
        {
            return true;
        }

        if (!c.Fixtures.Samples.TryDequeue(out var sample))
        {
            return false;
        }

        c.State["sample"] = sample;
        return true;
    }

    private static void OnSheet(OperationContext c, JsonElement sheet)
    {
        c.State.Remove("sample");
        var lines = Answer.Prop(sheet, "lines") is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToList() : [];
        var pending = lines.Where(l => Answer.Is(l, "status", "Pending", 0) && Answer.Id(l, "resultId") is not null).ToList();
        if (pending.Count > 0)
        {
            c.State["sheet"] = pending;
        }
    }

    private static HttpRequestMessage LabResultsRequest(OperationContext c)
    {
        var lines = (List<JsonElement>)c.State["sheet"];
        // A value each line takes: the first of its options when it has any, else a plausible number.
        return Post("api/v1/lab/results", new
        {
            entries = lines.Select(l => new
            {
                resultId = Answer.Id(l, "resultId"),
                value = Answer.Prop(l, "options") is { ValueKind: JsonValueKind.Array } options && options.GetArrayLength() > 0
                    ? options[0].GetString()
                    : c.Random.Next(40, 120).ToString(CultureInfo.InvariantCulture),
                remarks = (string?)null,
                organism = (string?)null,
                colonyCount = (string?)null,
                susceptibilities = (object?)null,
            }).ToList(),
        });
    }

    private static void OnMarDue(OperationContext c, JsonElement list)
    {
        foreach (var row in Answer.Rows(list))
        {
            var needsWitness = Answer.Prop(row, "needsWitness") is { ValueKind: JsonValueKind.True };
            var held = Answer.Prop(row, "heldBecause") is { ValueKind: JsonValueKind.String };
            if (!needsWitness && !held && Answer.Id(row, "entryId") is { } entry)
            {
                c.Fixtures.KeepDose(entry, Answer.Version(row));
            }
        }
    }

    private static HttpRequestMessage MarGivenRequest(OperationContext c)
    {
        // Another nurse may have taken the last one since this one looked: the due list again is a read, and a fair step.
        if (!c.Fixtures.Doses.TryDequeue(out var dose))
        {
            return Get($"api/v1/nursing/wards/{c.Fixtures.WardId}/mar/due?withinMinutes=240");
        }

        return Post($"api/v1/nursing/mar/{dose.EntryId}/record", new
        {
            outcome = "Given",
            administeredAt = (DateTimeOffset?)null,
            reason = (string?)null,
            batchNumber = (string?)null,
            witnessConfirmationId = (Guid?)null,
            notes = "Given (load test).",
            version = dose.Version,
        });
    }

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, path);

    private static HttpRequestMessage Post(string path, object body) => new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Operations.Json) };
}

/// <summary>
/// Finds, before the run, what the steps need from the server: a department, a billable service, the pharmacy's store and
/// medicines, lab tests, a ward. Each is read with an account of the role that uses it; a read that fails leaves its area out
/// of the mix (<see cref="PeakMix.Missing"/>) rather than failing the run.
/// </summary>
internal static class FixtureDiscovery
{
    public static async Task<Fixtures> DiscoverAsync(
        HttpClient http, IReadOnlyDictionary<Role, string> tokens, LoadTestOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        var fixtures = new Fixtures { PharmacyStoreId = options.PharmacyStore, WardId = options.Ward };

        if (tokens.TryGetValue(Role.FrontOffice, out var front))
        {
            fixtures.DepartmentId = Answer.Rows(await ReadAsync(http, front, "api/v1/masters/lookups/departments", log, cancellationToken))
                .Select(r => Answer.Id(r)).FirstOrDefault(id => id is not null);
        }

        if (tokens.TryGetValue(Role.Billing, out var billing))
        {
            fixtures.ServiceItemId = Answer.Rows(await ReadAsync(http, billing, "api/v1/masters/services?pageSize=50", log, cancellationToken))
                .Where(r => Answer.Prop(r, "isActive") is not { ValueKind: JsonValueKind.False } && Answer.Number(r, "basePrice") > 0)
                .Select(r => Answer.Id(r)).FirstOrDefault(id => id is not null);
        }

        if (tokens.TryGetValue(Role.Pharmacy, out var pharmacy))
        {
            fixtures.PharmacyStoreId ??= Answer.Rows(await ReadAsync(http, pharmacy, "api/v1/inventory/stores", log, cancellationToken))
                .Select(r => Answer.Id(r)).FirstOrDefault(id => id is not null);
            if (fixtures.PharmacyStoreId is { } store)
            {
                // Medicines that sell over the counter: in stock, with a price, and not needing a prescription.
                var stock = await ReadAsync(http, pharmacy, $"api/v1/pharmacy/stock?storeId={store}&inStockOnly=true&take=100", log, cancellationToken);
                fixtures.PharmacyItems =
                [
                    .. Answer.Rows(stock)
                        .Where(r => Answer.Prop(r, "needsPrescription") is not { ValueKind: JsonValueKind.True } && Answer.Number(r, "available") > 0 && Answer.Number(r, "currentMrp") > 0)
                        .Select(r => (Id: Answer.Id(r, "itemId"), Mrp: Answer.Number(r, "currentMrp")))
                        .Where(r => r.Id is not null)
                        .Select(r => new StockItem(r.Id!.Value, r.Mrp)),
                ];
            }
        }

        if (tokens.TryGetValue(Role.Lab, out var lab))
        {
            fixtures.LabTestIds =
            [
                .. Answer.Rows(await ReadAsync(http, lab, "api/v1/lab/tests?pageSize=50", log, cancellationToken))
                    .Where(r => Answer.Prop(r, "isActive") is not { ValueKind: JsonValueKind.False })
                    .Select(r => Answer.Id(r)).Where(id => id is not null).Select(id => id!.Value).Take(20),
            ];
        }

        if (tokens.TryGetValue(Role.Ward, out var ward))
        {
            fixtures.WardId ??= Answer.Rows(await ReadAsync(http, ward, "api/v1/inpatient/wards", log, cancellationToken))
                .Select(r => Answer.Id(r)).FirstOrDefault(id => id is not null);
        }

        return fixtures;
    }

    /// <summary>A list read as <paramref name="token"/>'s account; an empty one (and a line in the log) when it could not be read.</summary>
    private static async Task<JsonElement> ReadAsync(HttpClient http, string token, string path, TextWriter log, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                log.WriteLine($"  {path}: the server answered {(int)response.StatusCode}");
                return default;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            log.WriteLine($"  {path}: {ex.Message}");
            return default;
        }
    }
}
