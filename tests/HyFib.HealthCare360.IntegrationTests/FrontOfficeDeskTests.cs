using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Enquiries and visitor passes at the front desk (SRS FO-01..06) over the real API. An enquiry is recorded open and
/// resolved with the desk's answer, and an edit made from an older copy cannot overwrite it. A visitor pass is issued for
/// a patient keeping only the last four characters of the visitor's ID, to one attendant at a time, and is checked out or
/// revoked once. Enquiries belong to the front office, hospital administration and CRM, and visitor passes to the front
/// office, hospital administration and nurses: other roles are refused.
/// </summary>
public sealed class FrontOfficeDeskTests(HospitalServer server)
{
    private const string Enquiries = "/api/v1/front-office/enquiries";
    private const string Passes = "/api/v1/front-office/visitor-passes";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_enquiry_is_recorded_open_and_resolved_with_the_desks_answer()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var caller = "Sunita " + Letters(8);
        var mobile = "9" + Digits(9);
        var asked = new SaveEnquiryRequest($"  {caller} ", mobile, EnquiryType.DoctorAvailability, EnquiryChannel.Phone,
            " Is the cardiologist in on Saturday morning? ", EnquiryStatus.Open, Response: null, PatientId: null, Version: null);

        var created = await frontOffice.PostAsync<Created>(Enquiries, asked);
        var open = await EnquiryAsync(frontOffice, caller, EnquiryStatus.Open);

        open.Id.ShouldBe(created.Id);
        (open.Name, open.Phone, open.Details).ShouldBe((caller, "+91" + mobile, "Is the cardiologist in on Saturday morning?"));
        (open.Type, open.Channel).ShouldBe((EnquiryType.DoctorAvailability, EnquiryChannel.Phone));
        open.Response.ShouldBeNull();
        open.ResolvedAt.ShouldBeNull();

        // Taken up, then answered.
        await frontOffice.PutAsync($"{Enquiries}/{created.Id}", asked with { Status = EnquiryStatus.InProgress, Version = open.Version });
        var inProgress = await EnquiryAsync(frontOffice, caller, EnquiryStatus.InProgress);
        inProgress.ResolvedAt.ShouldBeNull("An enquiry being worked on is not resolved.");

        var answering = DateTimeOffset.UtcNow.AddSeconds(-1);
        await frontOffice.PutAsync($"{Enquiries}/{created.Id}", asked with
        {
            Status = EnquiryStatus.Resolved,
            Response = " Dr. Rao sees patients on Saturdays from 9 to 1; booked for 10:30. ",
            Version = inProgress.Version,
        });
        var resolved = await EnquiryAsync(frontOffice, caller, EnquiryStatus.Resolved);

        resolved.Response.ShouldBe("Dr. Rao sees patients on Saturdays from 9 to 1; booked for 10:30.");
        resolved.ResolvedAt.ShouldNotBeNull().ShouldBeInRange(answering, DateTimeOffset.UtcNow.AddSeconds(1));
        (await EnquiriesAsync(frontOffice, caller, EnquiryStatus.Open)).ShouldBeEmpty("A resolved enquiry has left the desk's open list.");

        // Another desk still showing the enquiry before it was answered cannot reopen it over the answer.
        using (var stale = await frontOffice.SendRawAsync(HttpMethod.Put, $"{Enquiries}/{created.Id}",
                   asked with { Status = EnquiryStatus.Open, Version = inProgress.Version }))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await EnquiryAsync(frontOffice, caller, EnquiryStatus.Resolved)).Response.ShouldBe(resolved.Response);
    }

    [Fact]
    public async Task A_visitor_pass_is_issued_for_a_patient_and_closed_once()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterPatientAsync();
        var son = "Ramesh " + Letters(8);

        var issuing = DateTimeOffset.UtcNow.AddSeconds(-1);
        var issued = await frontOffice.PostAsync<IssuedPass>(Passes, new IssueVisitorPassRequest(
            patient.Id, $" {son} ", "9" + Digits(9), "Son", "Aadhaar", "2345 6789 0123", "Attendant for the stay", "Ward 3, bed 12",
            IsAttendant: true, ValidHours: 12));

        issued.ValidUntil.ShouldBeInRange(issuing.AddHours(12), DateTimeOffset.UtcNow.AddHours(12).AddSeconds(1));
        var pass = (await PassesAsync(frontOffice, son)).ShouldHaveSingleItem();
        (pass.Id, pass.PassNumber, pass.Status).ShouldBe((issued.Id, issued.PassNumber, VisitorPassStatus.Active));
        pass.ValidUntil.ShouldBe(issued.ValidUntil, TimeSpan.FromMilliseconds(1), "PostgreSQL keeps microseconds.");
        (pass.PatientId, pass.PatientUhid, pass.VisitorName, pass.IsAttendant).ShouldBe((patient.Id, patient.Uhid, son, true));
        pass.IdProofLast4.ShouldBe("0123", "Only the last four characters of the visitor's ID are kept.");

        // One attendant at a time; other visitors may still come.
        using (var second = await frontOffice.SendRawAsync(HttpMethod.Post, Passes, VisitorOf(patient.Id, "Suresh " + Letters(8), attendant: true)))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await second.Content.ReadAsStringAsync(Ct)).ShouldContain("visitor.attendant_limit");
        }

        var neighbour = "Lakshmi " + Letters(8);
        var visit = await frontOffice.PostAsync<IssuedPass>(Passes, VisitorOf(patient.Id, neighbour, attendant: false));

        // The attendant leaves: the pass is no longer active, and it closes only once.
        await frontOffice.PostAsync($"{Passes}/{issued.Id}/checkout");

        (await PassesAsync(frontOffice, son)).ShouldBeEmpty("A checked-out pass is not active.");
        var checkedOut = (await PassesAsync(frontOffice, son, activeOnly: false)).ShouldHaveSingleItem();
        checkedOut.Status.ShouldBe(VisitorPassStatus.CheckedOut);
        checkedOut.CheckedOutAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(issuing);
        foreach (var close in new[] { "checkout", "revoke" })
        {
            using var again = await frontOffice.SendRawAsync(HttpMethod.Post, $"{Passes}/{issued.Id}/{close}");
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, close);
            (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("visitor.closed");
        }

        // The next attendant may now have a pass, and a visitor's pass revoked at the gate is closed too.
        await frontOffice.PostAsync<IssuedPass>(Passes, VisitorOf(patient.Id, "Suresh " + Letters(8), attendant: true));
        await frontOffice.PostAsync($"{Passes}/{visit.Id}/revoke");
        (await PassesAsync(frontOffice, neighbour, activeOnly: false)).ShouldHaveSingleItem().Status.ShouldBe(VisitorPassStatus.Revoked);
    }

    [Fact]
    public async Task Enquiries_and_visitor_passes_are_refused_to_roles_without_their_permission()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var caller = "Farida " + Letters(8);
        var asked = new SaveEnquiryRequest(caller, null, EnquiryType.Billing, EnquiryChannel.WalkIn, "What does a health check cost?",
            EnquiryStatus.Open, Response: null, PatientId: null, Version: null);
        var enquiry = await frontOffice.PostAsync<Created>(Enquiries, asked);

        // Nurses issue and close visitor passes on the ward; enquiries are not theirs, and doctors have neither.
        var visitor = "Gopal " + Letters(8);
        var pass = await nurse.PostAsync<IssuedPass>(Passes, VisitorOf((await RegisterPatientAsync()).Id, visitor, attendant: true));
        var refusals = new (ApiClient Client, HttpMethod Method, string Path, object? Body)[]
        {
            (nurse, HttpMethod.Get, Enquiries, null),
            (nurse, HttpMethod.Post, Enquiries, asked),
            (nurse, HttpMethod.Put, $"{Enquiries}/{enquiry.Id}", asked with { Status = EnquiryStatus.Resolved, Response = "Rs 2,500" }),
            (doctor, HttpMethod.Get, Enquiries, null),
            (doctor, HttpMethod.Get, Passes, null),
            (doctor, HttpMethod.Post, Passes, VisitorOf(null, "Mohan " + Letters(8), attendant: false)),
            (doctor, HttpMethod.Post, $"{Passes}/{pass.Id}/checkout", null),
            (doctor, HttpMethod.Post, $"{Passes}/{pass.Id}/revoke", null),
        };
        foreach (var (client, method, path, body) in refusals)
        {
            using var refused = await client.SendRawAsync(method, path, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{(client == nurse ? "nurse" : "doctor")}: {method} {path}");
        }

        // The refusals changed nothing: the enquiry is still open and the pass still active, until the nurse checks it out.
        (await EnquiryAsync(frontOffice, caller, EnquiryStatus.Open)).Id.ShouldBe(enquiry.Id);
        (await PassesAsync(nurse, visitor)).ShouldHaveSingleItem().Id.ShouldBe(pass.Id);
        await nurse.PostAsync($"{Passes}/{pass.Id}/checkout");
        (await PassesAsync(nurse, visitor, activeOnly: false)).ShouldHaveSingleItem().Status.ShouldBe(VisitorPassStatus.CheckedOut);
    }

    private static IssueVisitorPassRequest VisitorOf(Guid? patientId, string name, bool attendant) => new(
        patientId, name, "9" + Digits(9), attendant ? "Brother" : "Neighbour", null, null, "Visiting", "Ward 3", attendant, ValidHours: 4);

    private static async Task<EnquiryDto> EnquiryAsync(ApiClient client, string caller, EnquiryStatus status) =>
        (await EnquiriesAsync(client, caller, status)).ShouldHaveSingleItem();

    private static async Task<IReadOnlyList<EnquiryDto>> EnquiriesAsync(ApiClient client, string caller, EnquiryStatus status) =>
        (await client.GetAsync<PagedResult<EnquiryDto>>($"{Enquiries}?status={status}&search={Uri.EscapeDataString(caller)}")).Items;

    private static async Task<IReadOnlyList<VisitorPassDto>> PassesAsync(ApiClient client, string visitor, bool activeOnly = true) =>
        (await client.GetAsync<PagedResult<VisitorPassDto>>($"{Passes}?activeOnly={activeOnly}&search={Uri.EscapeDataString(visitor)}")).Items;

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Venkat", MiddleName: null, LastName: "Inpatient" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);

    private sealed record IssuedPass(Guid Id, string PassNumber, DateTimeOffset ValidUntil);
}
