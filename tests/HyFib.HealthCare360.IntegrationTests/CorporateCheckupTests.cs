using System.Globalization;
using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Corporate health check-ups (MHC-05) through the API, as the check-up desk runs a company's drive: the company is a
/// corporate payer in Billing's payer master, and the front desk books one package for its employees on one day. Every
/// employee the package's sex and age rules allow is booked once, under the batch, with the day's itinerary for their age
/// and the package's tests ordered; every other one is reported with the reason and not booked. Booking charges nothing:
/// each employee's check-in bills the package to the company, not to the employee.
/// </summary>
public sealed class CorporateCheckupTests(HospitalServer server)
{
    private const string Bookings = "/api/v1/health-checkups/bookings";
    private const string Batches = "/api/v1/health-checkups/corporate-batches";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Books_every_eligible_employee_once_and_reports_each_of_the_others_with_the_reason()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var company = await CompanyAsync(await server.SignInAsync(HospitalServer.Users.HospitalAdmin), isActive: true, "Kaveri Software Services");
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var (package, mammography) = await WomensPackageAsync(frontOffice);

        var priya = await RegisterAsync(Gender.Female, years: 30, at: day);
        var lakshmi = await RegisterAsync(Gender.Female, years: 45, at: day);
        var ravi = await RegisterAsync(Gender.Male, years: 35, at: day);
        var trainee = await RegisterAsync(Gender.Female, years: 16, at: day);
        var retiring = await RegisterAsync(Gender.Female, years: 60, at: day);
        var meena = await RegisterAsync(Gender.Female, years: 33, at: day);
        var unknown = Guid.CreateVersion7();

        // Meena has already booked the same check-up for that day herself.
        await frontOffice.PostAsync<BookingDto>(Bookings, new BookCheckupRequest(meena, package.Id, day, null, null));

        var result = await frontOffice.PostAsync<CorporateBatchResult>(Batches, new CorporateBatchRequest(
            company.Id, "Anita Rao", "9840012345", "PO-" + Letters(6), package.Id, day,
            [priya, lakshmi, ravi, trainee, retiring, meena, unknown, priya], "Annual check-up"));

        // Priya, listed twice, is booked once; the others are reported, each with the reason.
        result.Booked.ShouldBe(2);
        result.BatchNumber.ShouldNotBeNullOrWhiteSpace();
        result.Skipped.ToDictionary(s => s.PatientId, s => s.Reason).ShouldBe(new Dictionary<Guid, string>
        {
            [ravi] = "This package is for female patients only.",
            [trainee] = "This package is for ages 18–55.",
            [retiring] = "This package is for ages 18–55.",
            [meena] = "This patient is already booked for this package on that date.",
            [unknown] = $"Patient '{unknown}' was not found.",
        }, ignoreOrder: true);

        var batch = await frontOffice.GetAsync<BatchDetail>($"{Batches}/{result.BatchId}");
        (batch.Batch.PayerId, batch.Batch.CorporateName).ShouldBe((company.Id, company.Name));
        batch.Batch.ScheduledDate.ShouldBe(day);
        batch.Batch.BookedCount.ShouldBe(2);
        batch.Batch.SkippedCount.ShouldBe(5);
        batch.Bookings.Select(b => b.PatientId).ShouldBe([priya, lakshmi], ignoreOrder: true);
        batch.Bookings.ShouldAllBe(b => b.Status == BookingStatus.Booked && b.ScheduledDate == day && b.PackageId == package.Id
                                        && b.CorporateBatchId == result.BatchId && b.CorporatePayerId == company.Id && b.CorporateName == company.Name
                                        && b.OrderId != null);

        // Each itinerary follows the package's item rules for the employee's age: mammography from 40.
        var stops = batch.Bookings.ToDictionary(b => b.PatientId, b => string.Join(" | ", b.Itinerary.Select(i => i.Description)));
        stops[lakshmi].ShouldContain(mammography);
        stops[priya].ShouldNotContain(mammography);

        // Nobody is charged until checking in, neither for the package nor for its tests.
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        foreach (var employee in new[] { priya, lakshmi })
        {
            (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={employee}&pageSize=200")).Items.ShouldBeEmpty();
        }
    }

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
        var current = checkedIn;
        foreach (var stop in checkedIn.Itinerary)
        {
            current = await frontOffice.PostAsync<BookingDto>($"{Booking(checkedIn)}/itinerary/{stop.Id}/complete", new CompleteStopRequest(null));
        }

        // The summary is written to the booking as the doctor saw it: a second doctor still looking at that version is
        // refused rather than overwriting the first one's summary (REL-04).
        await doctor.PutAsync<BookingDto>($"{Booking(checkedIn)}/summary", new CheckupSummaryRequest("Fit. No abnormality detected.", null, current.Version));
        using (var stale = await doctor.SendRawAsync(HttpMethod.Put, $"{Booking(checkedIn)}/summary", new CheckupSummaryRequest("Unfit.", null, current.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

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
    public async Task A_check_up_desk_with_only_its_own_permission_lists_the_companies_to_book_for()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var company = await CompanyAsync(admin, isActive: true);
        var closed = await CompanyAsync(admin, isActive: false);

        // A role the hospital made for its check-up desk: health check-ups, nothing of billing or insurance.
        var suffix = Letters(8).ToLowerInvariant();
        var role = await (await server.SignInAsync(HospitalServer.Users.Admin)).PostAsync<RoleDetail>("/api/v1/admin/roles", new SaveRoleRequest(
            "IT check-up desk " + suffix, "Runs corporate check-up drives", [ClinicalPermissions.HealthCheckManage], Version: null));
        await server.CreateStaffAccountAsync("it-checkup-" + suffix, role.Name);
        var desk = await server.SignInAsync("it-checkup-" + suffix);

        // The insurance screens' payer list is not the desk's to read; its own list of companies is: the active
        // corporate payers, with the contact the batch form fills in.
        using (var refused = await desk.SendRawAsync(HttpMethod.Get, "/api/v1/insurance/payers?category=Corporate"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var companies = await desk.GetAsync<List<CorporatePayer>>("/api/v1/health-checkups/corporate-payers");
        companies.ShouldContain(new CorporatePayer(company.Id, company.Name, "Meena Iyer", company.Phone));
        companies.ShouldNotContain(c => c.Id == closed.Id, "an inactive company is not booked for");
        companies.ShouldNotContain(c => c.Name.StartsWith("Star Health", StringComparison.Ordinal), "an insurer is not a company");
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
    private static Task<PayerDetail> CompanyAsync(ApiClient admin, bool isActive, string? name = null) =>
        admin.PostAsync<PayerDetail>("/api/v1/insurance/payers", new SavePayerRequest(
            $"CO{Letters(8).ToUpperInvariant()}", name is null ? $"Acme Steel {Letters(6).ToUpperInvariant()} Ltd" : $"{name} {Letters(4).ToUpperInvariant()}",
            PayerCategory.Corporate, "Meena Iyer", "9876543210", null, null, null, null,
            CreditDays: 45, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: isActive, Notes: null, Version: null));

    /// <summary>A package for adults from 30, with one consultation.</summary>
    private static Task<PackageDto> PackageFromThirtyAsync(ApiClient admin) =>
        admin.PostAsync<PackageDto>("/api/v1/health-checkups/packages", new SavePackageRequest(
            $"HC-CO{Letters(8).ToUpperInvariant()}", "Employee Annual Checkup", null, PackageGender.Any, MinAgeYears: 30, MaxAgeYears: null, Price: 2400m, IsActive: true,
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

    /// <summary>
    /// A women's check-up package of the run's own (women of 18 to 55; a blood count for everyone, mammography from 40 and
    /// a general medicine consultation), made from the seeded women's package's tests, with the mammography's name.
    /// </summary>
    private static async Task<(PackageDto Package, string Mammography)> WomensPackageAsync(ApiClient frontOffice)
    {
        var seeded = (await frontOffice.GetAsync<List<PackageDto>>("/api/v1/health-checkups/packages?search=HC-WOMEN")).Single(p => p.Code == "HC-WOMEN");
        var bloodCount = seeded.Items.Single(i => i.ServiceCode == "LAB-CBC");
        var mammography = seeded.Items.Single(i => i.ServiceCode == "RAD-MAMMO");
        var package = await frontOffice.PostAsync<PackageDto>("/api/v1/health-checkups/packages", new SavePackageRequest(
            "HC-IT-" + Letters(8).ToUpperInvariant(), "Working women's check-up", null, PackageGender.Female, MinAgeYears: 18, MaxAgeYears: 55,
            Price: 2500m, IsActive: true,
            [
                new PackageItemRequest(PackageItemKind.Investigation, bloodCount.ServiceItemId, null, PackageGender.Any, null),
                new PackageItemRequest(PackageItemKind.Investigation, mammography.ServiceItemId, null, PackageGender.Female, MinAgeYears: 40),
                new PackageItemRequest(PackageItemKind.Consultation, null, "GM", PackageGender.Any, null),
            ],
            Version: null));
        return (package, mammography.Name);
    }

    /// <summary>A new patient (an employee) who is <paramref name="years"/> old on <paramref name="at"/>.</summary>
    private async Task<Guid> RegisterAsync(Gender gender, int years, DateOnly at)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Employee", MiddleName: null, LastName: "Corporate" + Letters(8), Gender: gender,
            DateOfBirth: at.AddYears(-years).AddMonths(-6), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

    private static string Letters(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private sealed record BatchDetail(CorporateBatchDto Batch, List<BookingDto> Bookings);
}
