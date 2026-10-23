using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Corporate health check-ups (MHC-05) through the API, as the check-up desk runs a company's drive: the company is a
/// corporate payer in Billing's payer master, a batch books its employees on one package and day and reports those it
/// cannot book, and each employee's check-in bills the package to the company, not to the employee.
/// </summary>
public sealed class CorporateCheckupTests(HospitalServer server)
{
    private const string Batches = "/api/v1/health-checkups/corporate-batches";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_batch_books_the_eligible_employees_and_bills_each_check_up_to_the_company()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var company = await CompanyAsync(admin, isActive: true);
        var package = await PackageFromThirtyAsync(admin);

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var manager = await EmployeeAsync(frontOffice, "Ravi", Gender.Male, today.AddYears(-46));
        var analyst = await EmployeeAsync(frontOffice, "Asha", Gender.Female, today.AddYears(-34));
        var trainee = await EmployeeAsync(frontOffice, "Kiran", Gender.Male, today.AddYears(-22));

        // The HR desk sends three names, one of them twice. The trainee is too young for the package and is reported,
        // with the reason, instead of being booked.
        var booked = await frontOffice.PostAsync<CorporateBatchResult>(Batches, new CorporateBatchRequest(
            company.Id, "Meena Iyer", "9876543210", "PO-2026-118", package.Id, today, [manager, analyst, trainee, manager], null));
        booked.Booked.ShouldBe(2);
        var skipped = booked.Skipped.ShouldHaveSingleItem();
        skipped.PatientId.ShouldBe(trainee);
        skipped.Reason.ShouldBe("This package is for ages 30–any.");

        // The batch is the company's: its name comes from the payer master, and so does every booking's.
        var detail = await frontOffice.GetAsync<BatchDetail>($"{Batches}/{booked.BatchId}");
        detail.Batch.PayerId.ShouldBe(company.Id);
        detail.Batch.CorporateName.ShouldBe(company.Name);
        (detail.Batch.BookedCount, detail.Batch.SkippedCount).ShouldBe((2, 1));
        detail.Bookings.Select(b => b.PatientId).ShouldBe([manager, analyst], ignoreOrder: true);
        detail.Bookings.ShouldAllBe(b => b.Status == BookingStatus.Booked && b.CorporatePayerId == company.Id && b.CorporateName == company.Name);

        // The manager checks in without a visit of his own: the desk gets a health check-up visit billed to the company.
        var managerBooking = detail.Bookings.Single(b => b.PatientId == manager);
        var checkedIn = await frontOffice.PostAsync<BookingDto>(CheckIn(managerBooking), new CheckInCheckupRequest(null));
        checkedIn.Status.ShouldBe(BookingStatus.CheckedIn);
        var visit = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{checkedIn.VisitId.ShouldNotBeNull()}");
        visit.PatientId.ShouldBe(manager);
        visit.Type.ShouldBe(VisitType.HealthCheckup);
        (visit.PayerType, visit.PayerId).ShouldBe((PayerType.Corporate, company.Id));

        // Billing bills the visit to the company: the package is the company's share, and the employee owes nothing.
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        (draft.PayerType, draft.PayerId).ShouldBe((PayerType.Corporate, company.Id));
        draft.Lines.ShouldContain(l => l.ServiceItemId == package.ServiceItemId && l.GrossAmount == package.Price);
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.PayerAmount.ShouldBe(bill.NetAmount);
        bill.PayerAmount.ShouldBeGreaterThanOrEqualTo(package.Price);
        (bill.PatientAmount, bill.DueAmount).ShouldBe((0m, 0m));

        // The analyst came in on a visit she pays for herself, which cannot carry a check-up the company pays for. The
        // booking waits; a visit billed to the company is accepted.
        var analystBooking = detail.Bookings.Single(b => b.PatientId == analyst);
        var ownVisit = await VisitAsync(frontOffice, analyst, PayerType.Self, null);
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, CheckIn(analystBooking), new CheckInCheckupRequest(ownVisit.Id)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await refused.Content.ReadAsStringAsync(Ct);
            problem.ShouldContain("visitId");
            problem.ShouldContain($"Visit {ownVisit.VisitNumber} is not billed to {company.Name}");
        }

        (await frontOffice.GetAsync<BookingDto>($"/api/v1/health-checkups/bookings/{analystBooking.Id}")).Status.ShouldBe(BookingStatus.Booked);
        var companyVisit = await VisitAsync(frontOffice, analyst, PayerType.Corporate, company.Id);
        var linked = await frontOffice.PostAsync<BookingDto>(CheckIn(analystBooking), new CheckInCheckupRequest(companyVisit.Id));
        (linked.Status, linked.VisitId).ShouldBe((BookingStatus.CheckedIn, companyVisit.Id));
    }

    [Fact]
    public async Task The_visit_a_check_in_opened_for_the_company_closes_with_the_check_up_but_a_linked_one_is_the_desks()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var company = await CompanyAsync(admin, isActive: true);
        var package = await PackageFromThirtyAsync(admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var (examined, leftEarly, linked) = (
            await EmployeeAsync(frontOffice, "Suresh", Gender.Male, today.AddYears(-41)),
            await EmployeeAsync(frontOffice, "Divya", Gender.Female, today.AddYears(-37)),
            await EmployeeAsync(frontOffice, "Anil", Gender.Male, today.AddYears(-52)));
        var batch = await frontOffice.PostAsync<CorporateBatchResult>(Batches, new CorporateBatchRequest(
            company.Id, "Meena Iyer", "9876543210", null, package.Id, today, [examined, leftEarly, linked], null));
        var bookings = (await frontOffice.GetAsync<BatchDetail>($"{Batches}/{batch.BatchId}")).Bookings.ToDictionary(b => b.PatientId);

        // Seen at every station and summarised by the doctor: his check-up is complete, and so is the visit opened for it.
        var checkedIn = await frontOffice.PostAsync<BookingDto>(CheckIn(bookings[examined]), new CheckInCheckupRequest(null));
        foreach (var stop in checkedIn.Itinerary)
        {
            await frontOffice.PostAsync<BookingDto>($"{Booking(checkedIn)}/itinerary/{stop.Id}/complete", new CompleteStopRequest(null));
        }

        await doctor.PutAsync<BookingDto>($"{Booking(checkedIn)}/summary", new CheckupSummaryRequest("Fit. No abnormality detected.", null));
        (await frontOffice.PostAsync<BookingDto>($"{Booking(checkedIn)}/complete")).Status.ShouldBe(BookingStatus.Completed);
        (await VisitStatusAsync(frontOffice, checkedIn)).ShouldBe(VisitStatus.Completed);

        // She left before the tests: her check-up is cancelled, and the visit opened for it with it.
        var abandoned = await frontOffice.PostAsync<BookingDto>(CheckIn(bookings[leftEarly]), new CheckInCheckupRequest(null));
        await frontOffice.PostAsync<BookingDto>($"{Booking(abandoned)}/cancel", new CancelBookingRequest("Left before the tests"));
        (await VisitStatusAsync(frontOffice, abandoned)).ShouldBe(VisitStatus.Cancelled);

        // He came in on a visit the desk opened for the company: it is the desk's to close, not the check-up's.
        var deskVisit = await VisitAsync(frontOffice, linked, PayerType.Corporate, company.Id);
        var onDeskVisit = await frontOffice.PostAsync<BookingDto>(CheckIn(bookings[linked]), new CheckInCheckupRequest(deskVisit.Id));
        await frontOffice.PostAsync<BookingDto>($"{Booking(onDeskVisit)}/cancel", new CancelBookingRequest("Rescheduled by HR"));
        (await VisitStatusAsync(frontOffice, onDeskVisit)).ShouldBe(VisitStatus.Open);
    }

    [Fact]
    public async Task Only_an_active_corporate_payer_can_be_the_company_of_a_batch()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var package = await PackageFromThirtyAsync(admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var employee = await EmployeeAsync(frontOffice, "Vinod", Gender.Male, today.AddYears(-40));

        var insurer = (await admin.GetAsync<List<PayerListItem>>("/api/v1/insurance/payers")).Single(p => p.Code == "STAR");
        var closed = await CompanyAsync(admin, isActive: false);
        foreach (var (payerId, reason) in new[]
                 {
                     (insurer.Id, $"{insurer.Name} is not a corporate payer."),
                     (closed.Id, $"{closed.Name} is inactive in the payer master."),
                     (Guid.CreateVersion7(), "Choose the company from the payer master."),
                 })
        {
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Batches,
                new CorporateBatchRequest(payerId, null, null, null, package.Id, today, [employee], null));
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await refused.Content.ReadAsStringAsync(Ct);
            problem.ShouldContain("payerId");
            problem.ShouldContain(reason);
        }

        // Nothing was booked for the refused batches.
        var bookings = await frontOffice.GetAsync<PagedResult<BookingDto>>($"/api/v1/health-checkups/bookings?date={today:yyyy-MM-dd}&pageSize=200");
        bookings.Items.ShouldNotContain(b => b.PatientId == employee);
    }

    private static string CheckIn(BookingDto booking) => $"{Booking(booking)}/check-in";

    private static string Booking(BookingDto booking) => $"/api/v1/health-checkups/bookings/{booking.Id}";

    private static async Task<VisitStatus> VisitStatusAsync(ApiClient frontOffice, BookingDto booking) =>
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{booking.VisitId.ShouldNotBeNull()}")).Status;

    /// <summary>A company the hospital has a tie-up with, as the insurance desk adds it to the payer master.</summary>
    private static Task<PayerDetail> CompanyAsync(ApiClient admin, bool isActive) =>
        admin.PostAsync<PayerDetail>("/api/v1/insurance/payers", new SavePayerRequest(
            $"CO{Letters(8)}", $"Acme Steel {Letters(6)} Ltd", PayerCategory.Corporate, "Meena Iyer", "9876543210", null, null, null, null,
            CreditDays: 45, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: isActive, Notes: null, Version: null));

    /// <summary>A package for adults from 30, with one consultation.</summary>
    private static Task<PackageDto> PackageFromThirtyAsync(ApiClient admin) =>
        admin.PostAsync<PackageDto>("/api/v1/health-checkups/packages", new SavePackageRequest(
            $"HC-CO{Letters(8)}", "Employee Annual Checkup", null, PackageGender.Any, MinAgeYears: 30, MaxAgeYears: null, Price: 2400m, IsActive: true,
            [new PackageItemRequest(PackageItemKind.Consultation, null, "GM", PackageGender.Any, null)], Version: null));

    private static async Task<Guid> EmployeeAsync(ApiClient frontOffice, string firstName, Gender gender, DateOnly dateOfBirth)
    {
        // A patient of this test alone: the unique names and mobile number keep the duplicate check quiet.
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: gender == Gender.Female ? "Ms." : "Mr.", FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Employee" + Letters(8),
            Gender: gender, DateOfBirth: dateOfBirth, AgeYears: null, AgeMonths: null, Phone: "9" + Digits(9), AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static Task<VisitDetail> VisitAsync(ApiClient frontOffice, Guid patientId, PayerType payerType, Guid? payerId) =>
        frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.HealthCheckup, null, null, null, payerType, payerId, ReferralSource.Corporate, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private sealed record BatchDetail(CorporateBatchDto Batch, List<BookingDto> Bookings);
}
