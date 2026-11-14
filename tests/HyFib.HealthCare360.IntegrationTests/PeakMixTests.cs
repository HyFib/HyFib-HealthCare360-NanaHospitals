using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.LoadTest;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using BillPaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using BillPaymentRequest = HyFib.HealthCare360.Modules.Billing.Features.Payments.CollectPaymentRequest;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The hospital's peak mix of the load test (tools/HyFib.HealthCare360.LoadTest, <see cref="PeakMix"/>) against the whole
/// server: the fixtures are read as the runner reads them, and then, for each role, every step of the mix is taken once by a
/// test account that holds the role. Every step must succeed, and the chains must complete: a bill started, given a
/// service, finalised and paid; a counter sale; a lab order, the worklist, a result sheet and its results; the due
/// doses read and one given; the portal's record views. The test server is given what the steps need through the API (a
/// medicine on the shelf, a received lab sample, an admitted patient with a dose due, a portal patient), so none is left out
/// of the mix and its "Not run" list must be empty. What it does not cover: the portal's sign-in itself (a code sent to the
/// patient's phone and a proof of work, which the runner cannot answer; the test signs in as the portal tests do and takes
/// the portal steps with that session) and a run of many users at once (LoadTestTests has a short run for the staff steps).
/// </summary>
public sealed class PeakMixTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_step_of_the_peak_mix_works_for_the_role_that_takes_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // What the test server needs for the steps, made through the API as staff make it.
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var sample = await ReceivedLabSampleAsync(frontOffice, doctor, billing, lab);
        var (wardId, dose) = await DueDoseAsync(frontOffice, doctor, admin);
        var portalPhone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        await RegisterAsync(frontOffice, portalPhone);
        var portal = await server.SignInToPortalAsync(portalPhone);

        var clients = new Dictionary<Role, ApiClient>
        {
            [Role.FrontOffice] = frontOffice,
            [Role.Billing] = billing,
            [Role.Pharmacy] = pharmacist,
            [Role.Lab] = lab,
            [Role.Ward] = nurse,
            [Role.Portal] = portal,
        };

        // The fixtures, read as the runner reads them.
        var options = new LoadTestOptions
        {
            Server = new Uri("https://localhost:7360/"),
            Accounts = [HospitalServer.Users.FrontOffice],
            Password = server.Password,
            PharmacyStore = medicine.StoreId,
            Ward = wardId,
        };
        using var http = new HttpClient(server.CreateHandler()) { BaseAddress = options.Server };
        var tokens = clients.Where(c => c.Key != Role.Portal).ToDictionary(c => c.Key, c => c.Value.Http.DefaultRequestHeaders.Authorization!.Parameter!);
        using var log = new StringWriter();
        var fixtures = await FixtureDiscovery.DiscoverAsync(http, tokens, options, log, Ct);
        var mix = PeakMix.Build(new HashSet<Role>(clients.Keys), fixtures, new Dictionary<string, int>(), out var notRun);

        notRun.ShouldBeEmpty(log + Environment.NewLine + string.Join(Environment.NewLine, notRun));
        fixtures.PharmacyItems.ShouldContain(i => i.ItemId == medicine.ItemId, "the medicine on the shelf sells over the counter");

        var patients = (await frontOffice.GetAsync<JsonElement>("/api/v1/patients?pageSize=50"))
            .GetProperty("items").EnumerateArray().Where(p => !p.GetProperty("isVip").GetBoolean()).Select(p => p.GetProperty("id").GetGuid()).ToList();
        OperationContext Context(int seed) => new(patients, new Random(seed), () => Random.Shared.NextInt64(1, 999_999_999), fixtures);
        Operation Step(string name) => mix.Single(o => o.Name == name);

        // Registration: the desk's steps, and the OPD visit with its token.
        var desk = Context(1);
        foreach (var operation in mix.Where(o => o.Role == Role.FrontOffice))
        {
            await TakeAsync(frontOffice, operation, desk);
        }

        // The patient the desk registered had one visit (a patient seen before may have a visit open today, which is refused), and
        // is the one the clerk bills below.
        fixtures.NewPatients.IsEmpty.ShouldBeTrue("the registered patient was given their visit");
        fixtures.BillPatients.ShouldHaveSingleItem();

        // Billing: the counter shift (open already, or opened by the clerk), then a bill from start to receipt.
        var clerk = Context(2);
        await TakeAsync(billing, Step(PeakMix.ShiftCheck), clerk);
        if (Step(PeakMix.ShiftOpen).IsAvailable!(clerk))
        {
            await TakeAsync(billing, Step(PeakMix.ShiftOpen), clerk);
        }

        await TakeAsync(billing, Step(PeakMix.BillFind), clerk);
        foreach (var name in new[] { PeakMix.BillStart, PeakMix.BillLine, PeakMix.BillFinalise, PeakMix.BillReceipt })
        {
            Step(name).IsAvailable!(clerk).ShouldBeTrue($"{name} follows the step before it");
            await TakeAsync(billing, Step(name), clerk);
        }

        // Pharmacy.
        var counter = Context(3);
        await TakeAsync(pharmacist, Step(PeakMix.StockLookup), counter);
        await TakeAsync(pharmacist, Step(PeakMix.CounterSale), counter);

        // Laboratory: the worklist holds the received sample; a walk-in order; the sheet of that sample and its results.
        var bench = Context(4);
        await TakeAsync(lab, Step(PeakMix.LabWorklist), bench);
        fixtures.Samples.ShouldContain(sample, "the received sample awaits results on the worklist");
        await TakeAsync(lab, Step(PeakMix.LabOrder), bench);
        fixtures.Samples.Clear();
        fixtures.Samples.Enqueue(sample);
        Step(PeakMix.LabSheet).IsAvailable!(bench).ShouldBeTrue();
        await TakeAsync(lab, Step(PeakMix.LabSheet), bench);
        Step(PeakMix.LabResults).IsAvailable!(bench).ShouldBeTrue("the sheet has results pending");
        await TakeAsync(lab, Step(PeakMix.LabResults), bench);

        // Ward: the due list holds the dose; a nurse gives it.
        var round = Context(5);
        await TakeAsync(nurse, Step(PeakMix.MarDue), round);
        fixtures.Doses.ShouldContain(d => d.EntryId == dose, "the dose is due and needs no witness");
        while (fixtures.Doses.TryDequeue(out _))
        {
        }

        fixtures.Doses.Enqueue(new DueDose(dose, await DoseVersionAsync(nurse, wardId, dose)));
        Step(PeakMix.MarGiven).IsAvailable!(round).ShouldBeTrue();
        await TakeAsync(nurse, Step(PeakMix.MarGiven), round);

        // Patient portal, with a session signed in as the portal signs in.
        var visitor = Context(6);
        foreach (var operation in mix.Where(o => o.Role == Role.Portal))
        {
            await TakeAsync(portal, operation, visitor);
        }
    }

    /// <summary>Takes one step as <paramref name="client"/>'s account does, asserts it succeeded, and lets it keep what the next step needs.</summary>
    private static async Task TakeAsync(ApiClient client, Operation operation, OperationContext context)
    {
        using var request = operation.Request(context);
        using var response = await client.Http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{operation.Name}: {(int)response.StatusCode} {text}");
        operation.OnSuccess?.Invoke(context, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(Guid Id, string Uhid)> RegisterAsync(ApiClient frontOffice, string phone)
    {
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Mohan"), MiddleName: null, LastName: "Peakmix" + Guid.NewGuid().ToString("N")[..6], Gender: Gender.Male,
            DateOfBirth: new DateOnly(1962, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null, Phone: phone, AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return (created.Id, created.Uhid);
    }

    /// <summary>
    /// A fasting-sugar sample the lab has received, as in the lab journey: a doctor's order, paid at the billing counter,
    /// drawn and received. Its results are then waiting to be entered. Returns the sample's id.
    /// </summary>
    private async Task<Guid> ReceivedLabSampleAsync(ApiClient frontOffice, ApiClient doctor, ApiClient billing, ApiClient lab)
    {
        var patient = await RegisterAsync(frontOffice, "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10)))));
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Excessive thirst",
            false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: null));
        var fbsService = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Laboratory&search=LAB-FBS")).Single(s => s.Code == "LAB-FBS");
        await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, encounter.Id, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Polyuria and thirst",
            [new OrderLineRequest(fbsService.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        var tube = (await lab.GetAsync<PagedResult<PendingCollectionItem>>($"/api/v1/lab/samples/pending-collection?search={patient.Uhid}")).Items.ShouldHaveSingleItem();

        // An out-patient test is paid before it is drawn.
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, PatientId: null, ChargeIds: null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        using (var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current"))
        {
            if (current.StatusCode == HttpStatusCode.NoContent)
            {
                await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
            }
        }

        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new BillPaymentRequest(
            bill.Id, [new PaymentLineInput(BillPaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));
        await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{tube.LabOrderId}/collect", new CollectSamplesRequest(null, null, "OP collection"));
        (await lab.PostAsync<List<ReceiveOutcome>>("/api/v1/lab/samples/receive", new ReceiveSamplesRequest([tube.Barcode]))).ShouldHaveSingleItem().Received.ShouldBeTrue();
        return tube.SampleId;
    }

    /// <summary>
    /// An admitted patient in the special ward with a dose due now that needs no witness (a drug neither controlled nor
    /// high-alert, ordered by the doctor so it is signed at once). Returns the ward and the dose's MAR entry.
    /// </summary>
    private async Task<(Guid WardId, Guid DoseId)> DueDoseAsync(ApiClient frontOffice, ApiClient doctor, ApiClient admin)
    {
        var patient = await RegisterAsync(frontOffice, "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10)))));
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        var bed = await TestBeds.AddAsync(admin, ward.Id);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Post-operative pain", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        var order = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders", new AddMedicationOrderRequest(
            DrugItemId: null, DrugName: "Paracetamol 500 mg Tablet", GenericName: null, Strength: null, Dose: "1 tablet", Route: "PO", Frequency: "STAT",
            DurationDays: 1, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null));
        var chart = await doctor.GetAsync<List<MarEntryDto>>($"/api/v1/nursing/admissions/{admission.Id}/mar");
        return (ward.Id, chart.Single(e => e.MedicationOrderId == order.Id && e.Status == MarStatus.Due).Id);
    }

    /// <summary>The version of a due dose, as the ward's due list shows it.</summary>
    private static async Task<uint> DoseVersionAsync(ApiClient nurse, Guid wardId, Guid entryId)
    {
        var due = await nurse.GetAsync<JsonElement>($"/api/v1/nursing/wards/{wardId}/mar/due?withinMinutes=240");
        return due.EnumerateArray().Single(e => e.GetProperty("entryId").GetGuid() == entryId).GetProperty("version").GetUInt32();
    }
}
