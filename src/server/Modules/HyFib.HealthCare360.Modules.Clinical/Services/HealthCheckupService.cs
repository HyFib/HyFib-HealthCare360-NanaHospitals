using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Master health checkup (MHC-01..05): package master (registered as a HealthCheckup service item for billing),
/// age/sex eligibility, booking with a department itinerary and one diagnostic order for the included tests,
/// station-by-station progress, the reviewing doctor's consolidated summary and corporate batch bookings. The package
/// is charged once, at check-in, through Billing (see <see cref="PackageBilling"/>): to the visit's payer, and for a
/// corporate batch to the company, a corporate payer of Billing's payer master.
/// </summary>
public sealed class HealthCheckupService(
    HospitalDbContext db,
    INumberSeries numbers,
    ClinicalAccess access,
    ClinicalOrderService orders,
    IChargePoster charges,
    IPayerDirectory payers,
    ICurrentUser currentUser,
    PatientContactAccess contacts,
    TimeProvider time)
{
    private readonly PackageBilling billing = new(charges);

    public const string DefaultConsultationCode = "CONS-NEW";

    /// <summary>The Patients module keeps a visit's cancel reason to 300 characters.</summary>
    private const int MaxVisitCancelReason = 300;

    private static readonly ServiceCategory[] DiagnosticCategories = [ServiceCategory.Laboratory, ServiceCategory.Radiology, ServiceCategory.Cardiology];

    // ------------------------------------------------------------------ packages

    public async Task<Result<PackageDto>> SavePackageAsync(Guid? id, SavePackageRequest request, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        HealthPackage? package = null;
        if (id is { } existingId)
        {
            package = await db.Set<HealthPackage>().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == existingId, ct);
            if (package is null)
            {
                return Error.NotFound(nameof(HealthPackage), existingId);
            }

            if (request.Version is { } version)
            {
                db.SetExpectedVersion(package, version);
            }
        }

        if (await db.Set<HealthPackage>().AnyAsync(p => p.Code == code && p.Id != id, ct)
            || await db.Set<ServiceItem>().AnyAsync(s => s.Code == code && (package == null || s.Id != package.ServiceItemId), ct))
        {
            return Error.Conflict("package.code_in_use", $"The code {code} is already used by another package or service.");
        }

        var items = await BuildItemsAsync(request.Items, ct);
        if (items.IsFailure)
        {
            return items.Error!;
        }

        if (package is null)
        {
            package = new HealthPackage();
            db.Add(package);
        }

        package.Code = code;
        package.Name = request.Name.Trim();
        package.Description = request.Description?.Trim();
        package.Gender = request.Gender;
        package.MinAgeYears = request.MinAgeYears;
        package.MaxAgeYears = request.MaxAgeYears;
        package.Price = Money.Round(request.Price);
        package.IsActive = request.IsActive;
        package.Items.Clear();
        foreach (var item in items.Value)
        {
            item.PackageId = package.Id;
            package.Items.Add(item);
        }

        await SyncServiceItemAsync(package, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(package);
    }

    public async Task<Result<PackageEligibility>> EligibilityAsync(Guid packageId, Guid patientId, CancellationToken ct)
    {
        var package = await db.Set<HealthPackage>().AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == packageId, ct);
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => new { p.Gender, p.DateOfBirth }).FirstOrDefaultAsync(ct);
        if (package is null || patient is null)
        {
            return Error.NotFound(package is null ? nameof(HealthPackage) : nameof(Patient), package is null ? packageId : patientId);
        }

        var age = patient.DateOfBirth is { } dob ? Age.InYears(dob, time.Today()) : (int?)null;
        var check = package.CheckEligibility(patient.Gender, age);
        return new PackageEligibility(check.IsSuccess, check.Error?.Message,
            package.Items.Where(i => !i.AppliesTo(patient.Gender, age)).Select(i => i.Name).ToList());
    }

    // ------------------------------------------------------------------ bookings

    public async Task<Result<BookingDto>> BookAsync(BookCheckupRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await access.EnsureCanRecordAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (request.VisitId is { } walkInVisitId && !await VisitBelongsToAsync(walkInVisitId, request.PatientId, ct))
        {
            return Error.Field("visitId", "This visit does not belong to the patient.");
        }

        var package = await db.Set<HealthPackage>().AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == request.PackageId, ct);
        if (package is null)
        {
            return Error.NotFound(nameof(HealthPackage), request.PackageId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var booking = await BookCoreAsync(facilityId, request.PatientId, package, request.ScheduledDate, request.VisitId, request.Notes, null, ct);
        if (booking.IsFailure)
        {
            return booking.Error!;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToDtoAsync(booking.Value, ct);
    }

    /// <summary>
    /// Corporate drive (MHC-05): books every eligible employee; ineligible ones are reported, not silently dropped. The
    /// company is a corporate payer of the payer master, which each employee's check-up is billed to at check-in.
    /// </summary>
    public async Task<Result<CorporateBatchResult>> BookCorporateAsync(CorporateBatchRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var company = await payers.FindAsync(request.PayerId, ct);
        if (company is null)
        {
            return Error.Field("payerId", "Choose the company from the payer master.");
        }

        if (company.PayerType != PayerType.Corporate)
        {
            return Error.Field("payerId", $"{company.Name} is not a corporate payer. Choose the company among the corporates of the payer master.");
        }

        if (!company.IsActive)
        {
            return Error.Field("payerId", $"{company.Name} is inactive in the payer master.");
        }

        var package = await db.Set<HealthPackage>().AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == request.PackageId, ct);
        if (package is null)
        {
            return Error.NotFound(nameof(HealthPackage), request.PackageId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var batch = new CorporateCheckupBatch
        {
            FacilityId = facilityId,
            BatchNumber = await numbers.NextAsync(ClinicalNumberSeries.CorporateCheckupBatch, facilityId, ct),
            PayerId = company.Id,
            CorporateName = company.Name,
            ContactPerson = request.ContactPerson?.Trim(),
            ContactPhone = Phone.Normalize(request.ContactPhone),
            Reference = request.Reference?.Trim(),
            PackageId = package.Id,
            ScheduledDate = request.ScheduledDate,
            Notes = request.Notes?.Trim(),
        };
        db.Add(batch);

        var skipped = new List<BatchSkip>();
        foreach (var patientId in request.PatientIds.Distinct())
        {
            var allowed = await access.EnsureCanRecordAsync(patientId, ct);
            var booking = allowed.IsFailure
                ? allowed.Error!
                : await BookCoreAsync(facilityId, patientId, package, request.ScheduledDate, null, request.Notes, batch, ct);
            if (booking.IsFailure)
            {
                skipped.Add(new BatchSkip(patientId, booking.Error!.Message));
            }
        }

        batch.SkippedCount = skipped.Count;
        batch.BookedCount = request.PatientIds.Distinct().Count() - skipped.Count;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CorporateBatchResult(batch.Id, batch.BatchNumber, batch.BookedCount, skipped);
    }

    /// <summary>
    /// Checks the patient in and charges the package in the same unit of work. If Billing refuses the charge (package no
    /// longer offered, merged UHID…) the check-in is refused and the booking stays as it was. A check-up a company pays
    /// for is charged on a visit billed to the company: the visit the desk links, which must be billed to it, or else a
    /// health check-up visit opened with the check-in.
    /// </summary>
    public async Task<Result<BookingDto>> CheckInAsync(Guid id, Guid? visitId, CancellationToken ct)
    {
        // A visit opened here takes its number in the check-in's transaction, so a refused check-in leaves no trace.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var checkedIn = await ChangeAsync(id, async b =>
        {
            if (visitId is { } vid && !await VisitBelongsToAsync(vid, b.PatientId, ct))
            {
                return Error.Field("visitId", "This visit does not belong to the patient.");
            }

            var ready = b.EnsureCanCheckIn();
            if (ready.IsFailure)
            {
                return ready;
            }

            var opensVisit = b.CorporatePayerId is not null && visitId is null;
            if (b.CorporatePayerId is { } companyId)
            {
                var companyVisit = await CompanyVisitAsync(b, companyId, visitId, ct);
                if (companyVisit.IsFailure)
                {
                    return companyVisit.Error!;
                }

                visitId = companyVisit.Value;
            }

            var packageServiceItemId = await db.Set<HealthPackage>().Where(p => p.Id == b.PackageId).Select(p => p.ServiceItemId).FirstOrDefaultAsync(ct);
            var checkedIn = await billing.CheckInAsync(b, packageServiceItemId, visitId, time.GetUtcNow(), ct);
            if (checkedIn.IsSuccess && opensVisit)
            {
                b.MarkVisitOpenedForCheckup();
            }

            return checkedIn;
        }, ct);
        if (checkedIn.IsSuccess)
        {
            await transaction.CommitAsync(ct);
        }

        return checkedIn;
    }

    public Task<Result<BookingDto>> CompleteStopAsync(Guid id, Guid itemId, string? notes, CancellationToken ct) =>
        ChangeAsync(id, b => Task.FromResult(b.CompleteStop(itemId, currentUser.UserId, notes, time.GetUtcNow())), ct);

    public Task<Result<BookingDto>> SkipStopAsync(Guid id, Guid itemId, string reason, CancellationToken ct) =>
        ChangeAsync(id, b => Task.FromResult(b.SkipStop(itemId, currentUser.UserId, reason, time.GetUtcNow())), ct);

    public Task<Result<BookingDto>> RecordSummaryAsync(Guid id, CheckupSummaryRequest request, CancellationToken ct) =>
        ChangeAsync(id, b =>
        {
            db.SetExpectedVersion(b, request.Version);
            return Task.FromResult(currentUser.PractitionerId is { } practitionerId
                ? b.RecordSummary(request.Summary, request.Recommendations, practitionerId, time.GetUtcNow())
                : Error.Rule("practitioner.required", "Only a doctor linked to a practitioner record can write the checkup summary."));
        }, ct);

    /// <summary>Completes the check-up, and the health check-up visit its check-in opened for the company.</summary>
    public Task<Result<BookingDto>> CompleteAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, async b =>
        {
            var completed = b.Complete(time.GetUtcNow());
            if (completed.IsSuccess && await OwnVisitAsync(b, ct) is { } visit)
            {
                visit.Complete(time.GetUtcNow());
            }

            return completed;
        }, ct);

    /// <summary>Marks a no-show, cancelling any package charge and the ordered tests.</summary>
    public Task<Result<BookingDto>> NoShowAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, async b =>
        {
            var result = await billing.NoShowAsync(b, time.GetUtcNow(), ct);
            if (result.IsSuccess)
            {
                await CancelOrderAsync(b, "Health checkup no-show", ct);
            }

            return result;
        }, ct);

    /// <summary>
    /// Cancelling the booking cancels its package charge and the tests that have not been done (ServiceOrderCancelled),
    /// and the health check-up visit its check-in opened for the company (VisitCancelled: Billing withdraws the visit's
    /// draft and returns a registration fee charged on it to the patient's next visit). A charge already on a finalised
    /// bill leaves the booking unchanged (422 <c>booking.package_billed</c>).
    /// </summary>
    public Task<Result<BookingDto>> CancelAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeAsync(id, async b =>
        {
            var result = await billing.CancelAsync(b, reason, time.GetUtcNow(), ct);
            if (result.IsSuccess)
            {
                var why = $"Health checkup cancelled: {reason.Trim()}";
                await CancelOrderAsync(b, why, ct);
                if (await OwnVisitAsync(b, ct) is { } visit)
                {
                    visit.Cancel(why.Length <= MaxVisitCancelReason ? why : why[..MaxVisitCancelReason], time.GetUtcNow());
                }
            }

            return result;
        }, ct);

    public async Task<Result<CheckupReport>> ReportAsync(Guid id, CancellationToken ct)
    {
        var booking = await Bookings().AsNoTracking().Include(b => b.Itinerary).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(PackageBooking), id);
        }

        var patients = await PatientQueries.SummariesAsync(db, [booking.PatientId], time.Today(), await contacts.FullAsync(ct), ct);
        var order = booking.OrderId is { } orderId
            ? await db.Set<ClinicalOrder>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct)
            : null;
        return new CheckupReport(
            await ToDtoAsync(booking, ct),
            patients[booking.PatientId],
            await PrintQueries.LetterheadAsync(db, booking.FacilityId, ct),
            booking.ReviewedByPractitionerId is { } reviewer ? await PrintQueries.PrescriberAsync(db, reviewer, ct) : null,
            order is null ? null : await orders.ToDtoAsync(order, ct));
    }

    public IQueryable<PackageBooking> Bookings() => db.Set<PackageBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    public async Task<BookingDto> ToDtoAsync(PackageBooking b, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == b.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        return ToDto(b, patient?.Uhid, patient?.FullName);
    }

    public static BookingDto ToDto(PackageBooking b, string? uhid, string? patientName) => new(
        b.Id, b.BookingNumber, b.PatientId, uhid, patientName, b.PackageId, b.PackageCode, b.PackageName, b.ScheduledDate, b.Status, b.VisitId, b.OrderId,
        b.CorporateBatchId, b.CorporateName, b.CorporatePayerId, b.Notes, b.CheckedInAt, b.Summary, b.Recommendations, b.ReviewedByPractitionerId, b.ReviewedAt,
        b.CompletedAt, b.CancelReason,
        b.Itinerary.OrderBy(i => i.Sequence)
            .Select(i => new ItineraryItemDto(i.Id, i.Sequence, i.DepartmentId, i.DepartmentName, i.Description, i.Status, i.CompletedAt, i.Notes)).ToList(),
        b.Version);

    public static PackageDto ToDto(HealthPackage p) => new(
        p.Id, p.Code, p.Name, p.Description, p.Gender, p.MinAgeYears, p.MaxAgeYears, p.Price, p.ServiceItemId, p.IsActive,
        p.Items.OrderBy(i => i.Sequence)
            .Select(i => new PackageItemDto(i.Id, i.Kind, i.ServiceItemId, i.ServiceCode, i.Name, i.Category, i.DepartmentCode, i.Gender, i.MinAgeYears, i.Sequence))
            .ToList(),
        p.Version);

    /// <summary>
    /// Books one patient inside the caller's transaction: eligibility, itinerary, and the diagnostic order. A booking of a
    /// corporate batch is the company's, and is billed to it at check-in.
    /// </summary>
    private async Task<Result<PackageBooking>> BookCoreAsync(Guid facilityId, Guid patientId, HealthPackage package, DateOnly scheduledDate, Guid? visitId,
        string? notes, CorporateCheckupBatch? batch, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId)
            .Select(p => new { p.Gender, p.DateOfBirth, p.IsDeceased, p.IsActive }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (patient.IsDeceased || !patient.IsActive)
        {
            return Error.Rule("patient.inactive", "The patient record is inactive.");
        }

        var age = patient.DateOfBirth is { } dob ? Age.InYears(dob, scheduledDate) : (int?)null;
        var eligible = package.CheckEligibility(patient.Gender, age);
        if (eligible.IsFailure)
        {
            return eligible.Error!;
        }

        if (await Bookings().AnyAsync(b => b.PatientId == patientId && b.PackageId == package.Id && b.ScheduledDate == scheduledDate
                                           && (b.Status == BookingStatus.Booked || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.InProgress), ct))
        {
            return Error.Conflict("booking.duplicate", "This patient is already booked for this package on that date.");
        }

        var applicable = package.Items.Where(i => i.AppliesTo(patient.Gender, age)).OrderBy(i => i.Sequence).ToList();
        var booking = new PackageBooking
        {
            FacilityId = facilityId,
            BookingNumber = await numbers.NextAsync(ClinicalNumberSeries.HealthCheckupBooking, facilityId, ct),
            PatientId = patientId,
            PackageId = package.Id,
            PackageCode = package.Code,
            PackageName = package.Name,
            ScheduledDate = scheduledDate,
            CorporateBatchId = batch?.Id,
            CorporateName = batch?.CorporateName,
            CorporatePayerId = batch?.PayerId,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
        booking.PlanItinerary(await PlanStopsAsync(applicable, ct));
        if (visitId is not null)
        {
            // A walk-in is checked in as it is booked, so its package is charged now, exactly as at a later check-in.
            var checkedIn = await billing.CheckInAsync(booking, package.ServiceItemId, visitId, time.GetUtcNow(), ct);
            if (checkedIn.IsFailure)
            {
                return checkedIn.Error!;
            }
        }

        var diagnosticIds = applicable.Where(i => i.Kind == PackageItemKind.Investigation && i.ServiceItemId is not null && DiagnosticCategories.Contains(i.Category))
            .Select(i => i.ServiceItemId!.Value).Distinct().ToList();
        if (diagnosticIds.Count > 0)
        {
            var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => diagnosticIds.Contains(s.Id) && s.IsActive).ToListAsync(ct);
            // booking.Id rides on every ServiceOrderPlaced (PackageBookingId), so Billing charges these tests once, under the
            // package; the note says the same for anyone reading the order or the work item.
            var note = $"Health checkup {package.Code} ({booking.BookingNumber}): package component — bill under the package.";
            var order = await orders.CreateAsync(new OrderDraft(facilityId, patientId, null, visitId, null, null, OrderPriority.Routine, note,
                OrderSource.HealthCheckup, booking.Id, services.Select(s => new OrderLine(s, null)).ToList()), ct);
            if (order.IsFailure)
            {
                return order.Error!;
            }

            booking.OrderId = order.Value.Id;
        }

        db.Add(booking);
        return booking;
    }

    /// <summary>
    /// One stop per department: fasting samples first, then cardiac tests, imaging, other services and consultations last
    /// (the doctor reviews once results are available).
    /// </summary>
    private async Task<List<ItineraryStop>> PlanStopsAsync(IReadOnlyList<HealthPackageItem> items, CancellationToken ct)
    {
        var serviceIds = items.Where(i => i.ServiceItemId is not null).Select(i => i.ServiceItemId!.Value).ToList();
        var serviceDepartments = await db.Set<ServiceItem>().AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DepartmentId, ct);
        var departments = await db.Set<Department>().AsNoTracking().Select(d => new { d.Id, d.Code, d.Name }).ToListAsync(ct);

        (Guid? Id, string Name) Resolve(HealthPackageItem item)
        {
            var departmentId = item.ServiceItemId is { } sid ? serviceDepartments.GetValueOrDefault(sid) : null;
            var code = item.Kind == PackageItemKind.Consultation
                ? item.DepartmentCode
                : item.Category switch { ServiceCategory.Laboratory => "LAB", ServiceCategory.Radiology => "RAD", ServiceCategory.Cardiology => "CDX", _ => null };
            var department = departments.FirstOrDefault(d => item.Kind != PackageItemKind.Consultation && d.Id == departmentId)
                             ?? departments.FirstOrDefault(d => d.Code == code);
            return department is null ? (null, code ?? item.Category.ToString()) : (department.Id, department.Name);
        }

        static int Rank(HealthPackageItem item) => item.Kind == PackageItemKind.Consultation ? 9 : item.Category switch
        {
            ServiceCategory.Laboratory => 0,
            ServiceCategory.Cardiology => 1,
            ServiceCategory.Radiology => 2,
            _ => 5,
        };

        return items
            .Select(i => (Item: i, Department: Resolve(i)))
            .GroupBy(x => (x.Department.Id, x.Department.Name, IsConsultation: x.Item.Kind == PackageItemKind.Consultation))
            .OrderBy(g => g.Min(x => Rank(x.Item)))
            .Select(g => new ItineraryStop(g.Key.Id, g.Key.Name, g.Key.IsConsultation
                ? $"Consultation — {g.Key.Name}"
                : (g.First().Item.Category == ServiceCategory.Laboratory ? "Sample collection: " : string.Empty) + string.Join(", ", g.Select(x => x.Item.Name))))
            .ToList();
    }

    private async Task<Result<List<HealthPackageItem>>> BuildItemsAsync(IReadOnlyList<PackageItemRequest> requests, CancellationToken ct)
    {
        var ids = requests.Where(r => r.ServiceItemId is not null).Select(r => r.ServiceItemId!.Value).Distinct().ToList();
        var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var consultation = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Code == DefaultConsultationCode, ct);
        var departmentNames = await db.Set<Department>().AsNoTracking().Select(d => new { d.Code, d.Name }).ToListAsync(ct);

        var items = new List<HealthPackageItem>();
        var sequence = 0;
        foreach (var request in requests)
        {
            var service = request.ServiceItemId is { } sid ? services.GetValueOrDefault(sid) : null;
            if (request.Kind == PackageItemKind.Investigation && service is null)
            {
                return Error.Field("items", "One of the tests is not in the service catalogue.");
            }

            if (request.Kind == PackageItemKind.Consultation)
            {
                service ??= consultation;
                if (service is null)
                {
                    return Error.Field("items", $"Consultation service {DefaultConsultationCode} is missing from the catalogue.");
                }
            }

            var departmentCode = request.DepartmentCode?.Trim().ToUpperInvariant();
            var departmentName = departmentNames.FirstOrDefault(d => d.Code == departmentCode)?.Name ?? departmentCode;
            items.Add(new HealthPackageItem
            {
                Kind = request.Kind,
                ServiceItemId = service!.Id,
                ServiceCode = service.Code,
                Name = request.Kind == PackageItemKind.Consultation ? $"Consultation — {departmentName}" : service.Name,
                Category = service.Category,
                DepartmentCode = departmentCode,
                Gender = request.Gender,
                MinAgeYears = request.MinAgeYears,
                Sequence = ++sequence,
            });
        }

        return items;
    }

    /// <summary>Keeps the package's billable service item (category HealthCheckup) in step with its name and price.</summary>
    private async Task SyncServiceItemAsync(HealthPackage package, CancellationToken ct)
    {
        var service = package.ServiceItemId is { } sid ? await db.Set<ServiceItem>().FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        if (service is null)
        {
            service = new ServiceItem { Code = package.Code, Category = ServiceCategory.HealthCheckup, SacCode = "999312", IsDiscountAllowed = true };
            db.Add(service);
            package.ServiceItemId = service.Id;
        }

        service.Code = package.Code;
        service.Name = package.Name;
        service.ShortName = package.Code;
        service.BasePrice = package.Price;
        service.Description = package.Description;
        service.IsActive = package.IsActive;
    }

    /// <summary>Billing takes the facility and tariff from the visit, so a visit of another patient must never be used.</summary>
    private Task<bool> VisitBelongsToAsync(Guid visitId, Guid patientId, CancellationToken ct) =>
        db.Set<Visit>().AnyAsync(v => v.Id == visitId && v.PatientId == patientId, ct);

    /// <summary>
    /// The visit a company-paid check-up is charged on, as Billing bills a visit to its payer: the visit the desk linked,
    /// when it is billed to the company, or else a health check-up visit billed to the company, numbered and raised as the
    /// Patients module opens a visit (so Billing charges a first visit's registration fee on it, to the company too).
    /// </summary>
    private async Task<Result<Guid>> CompanyVisitAsync(PackageBooking booking, Guid companyId, Guid? linkedVisitId, CancellationToken ct)
    {
        if (linkedVisitId is { } linkedId)
        {
            var linked = await db.Set<Visit>().Where(v => v.Id == linkedId).Select(v => new { v.VisitNumber, v.PayerType, v.PayerId }).FirstAsync(ct);
            return linked.PayerType == PayerType.Corporate && linked.PayerId == companyId
                ? linkedId
                : Error.Field("visitId", $"Visit {linked.VisitNumber} is not billed to {booking.CorporateName}, and this check-up is. "
                                         + $"Check in without linking a visit: one billed to {booking.CorporateName} is opened for the check-up.");
        }

        var patient = await db.Set<Patient>().Where(p => p.Id == booking.PatientId)
            .Select(p => new { p.MergedIntoPatientId, p.IsDeceased, p.IsActive }).FirstAsync(ct);
        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Use the surviving record.");
        }

        if (patient.IsDeceased || !patient.IsActive)
        {
            return Error.Rule("patient.inactive", "Visits cannot be created for inactive or deceased patients.");
        }

        var visit = new Visit
        {
            FacilityId = booking.FacilityId,
            VisitNumber = await numbers.NextAsync(PatientsNumberSeries.Visit, booking.FacilityId, ct),
            PatientId = booking.PatientId,
            Type = VisitType.HealthCheckup,
            Status = VisitStatus.Open,
            VisitedAt = time.GetUtcNow(),
            PayerType = PayerType.Corporate,
            PayerId = companyId,
            ReferralSource = ReferralSource.Corporate,
            ReferredBy = booking.CorporateName,
            Notes = $"Corporate health check-up {booking.BookingNumber}",
        };
        visit.MarkCreated();
        db.Add(visit);
        return visit.Id;
    }

    /// <summary>
    /// The health check-up visit open at <paramref name="visitId"/>, at any facility: a booking is completed or cancelled
    /// from whichever facility its doctor or desk works at (<see cref="Bookings"/>), and its visit closes with it.
    /// </summary>
    internal static IQueryable<Visit> OpenVisit(HospitalDbContext db, Guid visitId) =>
        db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(v => v.Id == visitId && (v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress));

    /// <summary>The health check-up visit the check-in opened for the company, while it is still open.</summary>
    private async Task<Visit?> OwnVisitAsync(PackageBooking booking, CancellationToken ct) =>
        booking is { VisitOpenedForCheckup: true, VisitId: { } visitId } ? await OpenVisit(db, visitId).FirstOrDefaultAsync(ct) : null;

    private async Task CancelOrderAsync(PackageBooking booking, string reason, CancellationToken ct)
    {
        if (booking.OrderId is not { } orderId)
        {
            return;
        }

        var order = await db.Set<ClinicalOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is not null && order.Items.Any(i => i.IsOpen))
        {
            order.Cancel(reason, time.GetUtcNow());
        }
    }

    private async Task<Result<BookingDto>> ChangeAsync(Guid id, Func<PackageBooking, Task<Result>> change, CancellationToken ct)
    {
        var booking = await Bookings().Include(b => b.Itinerary).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(PackageBooking), id);
        }

        var allowed = await access.EnsureCanOpenAsync(booking.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var result = await change(booking);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(booking, ct);
    }
}
