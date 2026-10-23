namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum PackageGender
{
    Any = 0,
    Male,
    Female,
}

public enum PackageItemKind
{
    Investigation = 0,
    Consultation,
}

public enum BookingStatus
{
    Booked = 0,
    CheckedIn,
    InProgress,
    Completed,
    Cancelled,
    NoShow,
}

public enum ItineraryStatus
{
    Pending = 0,
    Done,
    Skipped,
}

/// <summary>
/// A master health checkup package (MHC-01). It is also registered as a billable <see cref="ServiceItem"/>
/// (category HealthCheckup) so Billing charges the package price instead of the individual tests.
/// </summary>
public sealed class HealthPackage : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PackageGender Gender { get; set; }
    public int? MinAgeYears { get; set; }
    public int? MaxAgeYears { get; set; }
    public decimal Price { get; set; }
    public Guid? ServiceItemId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<HealthPackageItem> Items { get; set; } = [];

    public Result CheckEligibility(Gender gender, int? ageYears)
    {
        if (!IsActive)
        {
            return Error.Rule("package.inactive", "This package is no longer offered.");
        }

        if (!GenderMatches(Gender, gender))
        {
            return Error.Rule("package.gender", $"This package is for {Gender.ToString().ToLowerInvariant()} patients only.");
        }

        if (ageYears is null && (MinAgeYears is not null || MaxAgeYears is not null))
        {
            return Error.Rule("package.age_unknown", "Record the patient's date of birth or age to check eligibility.");
        }

        if (ageYears < MinAgeYears || ageYears > MaxAgeYears)
        {
            return Error.Rule("package.age", $"This package is for ages {MinAgeYears?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}–{MaxAgeYears?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any"}.");
        }

        return Result.Success();
    }

    internal static bool GenderMatches(PackageGender rule, Gender gender) => rule switch
    {
        PackageGender.Male => gender == SharedKernel.Gender.Male,
        PackageGender.Female => gender == SharedKernel.Gender.Female,
        _ => true,
    };
}

/// <summary>A test or consultation of a package, with optional item-level rules (for example mammography from 40).</summary>
public sealed class HealthPackageItem : Entity
{
    public Guid PackageId { get; set; }
    public PackageItemKind Kind { get; set; }
    public Guid? ServiceItemId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ServiceCategory Category { get; set; }

    /// <summary>Department code for consultations and itinerary grouping (GM, OBG, CARD…).</summary>
    public string? DepartmentCode { get; set; }

    public PackageGender Gender { get; set; }
    public int? MinAgeYears { get; set; }
    public int Sequence { get; set; }

    public bool AppliesTo(Gender gender, int? ageYears) =>
        HealthPackage.GenderMatches(Gender, gender) && (MinAgeYears is null || ageYears is null || ageYears >= MinAgeYears);
}

/// <summary>One stop of the checkup day (sample collection, imaging, ECG, consultation…).</summary>
public sealed record ItineraryStop(Guid? DepartmentId, string DepartmentName, string Description);

/// <summary>
/// A booked health checkup (MHC-02..04): the day's itinerary across departments, the diagnostic order placed at
/// booking, and the consolidated summary written by the reviewing doctor before completion.
/// </summary>
public sealed class PackageBooking : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string BookingNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid PackageId { get; set; }
    public string PackageCode { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public DateOnly ScheduledDate { get; set; }
    public BookingStatus Status { get; private set; }
    public Guid? VisitId { get; private set; }
    public Guid? OrderId { get; set; }
    public Guid? CorporateBatchId { get; set; }
    public string? CorporateName { get; set; }

    /// <summary>
    /// The company of a corporate batch, as a corporate payer of Billing's payer master: the check-up is billed to it
    /// (see <see cref="CorporateCheckupBatch.PayerId"/>).
    /// </summary>
    public Guid? CorporatePayerId { get; set; }

    /// <summary>
    /// <see cref="VisitId"/> is the health check-up visit the check-in opened for the company, not one the desk linked: it
    /// is the check-up's alone, so it is completed with the check-up and cancelled with it.
    /// </summary>
    public bool VisitOpenedForCheckup { get; private set; }

    public string? Notes { get; set; }
    public DateTimeOffset? CheckedInAt { get; private set; }
    public string? Summary { get; private set; }
    public string? Recommendations { get; private set; }
    public Guid? ReviewedByPractitionerId { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public List<ItineraryItem> Itinerary { get; set; } = [];

    public bool IsOpen => Status is BookingStatus.Booked or BookingStatus.CheckedIn or BookingStatus.InProgress;

    public void PlanItinerary(IEnumerable<ItineraryStop> stops)
    {
        Itinerary.Clear();
        var sequence = 0;
        foreach (var stop in stops)
        {
            Itinerary.Add(new ItineraryItem
            {
                BookingId = Id,
                Sequence = ++sequence,
                DepartmentId = stop.DepartmentId,
                DepartmentName = stop.DepartmentName,
                Description = stop.Description,
            });
        }
    }

    /// <summary>
    /// Guard for <see cref="CheckIn"/>. The package is charged at check-in, and Billing is asked only once this passes,
    /// so a booking that cannot be checked in never leaves a staged charge behind.
    /// </summary>
    public Result EnsureCanCheckIn() =>
        Status == BookingStatus.Booked ? Result.Success() : Error.Rule("booking.not_booked", "Only a booked checkup can be checked in.");

    public Result CheckIn(Guid? visitId, DateTimeOffset now)
    {
        var allowed = EnsureCanCheckIn();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = BookingStatus.CheckedIn;
        VisitId = visitId ?? VisitId;
        CheckedInAt = now;
        return Result.Success();
    }

    /// <summary>The visit the booking was checked in on was opened for it (see <see cref="VisitOpenedForCheckup"/>).</summary>
    public void MarkVisitOpenedForCheckup() => VisitOpenedForCheckup = VisitId is not null;

    public Result CompleteStop(Guid itemId, Guid? userId, string? notes, DateTimeOffset now) =>
        UpdateStop(itemId, ItineraryStatus.Done, userId, notes, now);

    public Result SkipStop(Guid itemId, Guid? userId, string reason, DateTimeOffset now) =>
        UpdateStop(itemId, ItineraryStatus.Skipped, userId, reason, now);

    public Result RecordSummary(string summary, string? recommendations, Guid practitionerId, DateTimeOffset now)
    {
        if (Status is BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow)
        {
            return Error.Rule("booking.closed", "This checkup is closed.");
        }

        Summary = summary.Trim();
        Recommendations = string.IsNullOrWhiteSpace(recommendations) ? null : recommendations.Trim();
        ReviewedByPractitionerId = practitionerId;
        ReviewedAt = now;
        return Result.Success();
    }

    /// <summary>A checkup is complete only when every stop is done or explicitly skipped and a doctor has summarised it.</summary>
    public Result Complete(DateTimeOffset now)
    {
        if (Status is not (BookingStatus.CheckedIn or BookingStatus.InProgress))
        {
            return Error.Rule("booking.not_started", "Check the patient in before completing the checkup.");
        }

        var pending = Itinerary.Count(i => i.Status == ItineraryStatus.Pending);
        if (pending > 0)
        {
            return Error.Rule("booking.pending_stops", $"{pending} itinerary stop(s) are still pending. Complete or skip them first.");
        }

        if (string.IsNullOrWhiteSpace(Summary))
        {
            return Error.Rule("booking.summary_missing", "The reviewing doctor's consolidated summary is required.");
        }

        Status = BookingStatus.Completed;
        CompletedAt = now;
        Raise(new HealthCheckupCompleted(Id, PatientId, FacilityId, PackageId, CorporateBatchId, now));
        return Result.Success();
    }

    /// <summary>Guard for <see cref="Cancel"/>, checked before Billing is asked to cancel the package charge.</summary>
    public Result EnsureCanCancel() => IsOpen ? Result.Success() : Error.Rule("booking.closed", "This checkup is already closed.");

    public Result Cancel(string reason, DateTimeOffset now)
    {
        var allowed = EnsureCanCancel();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = BookingStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Guard for <see cref="MarkNoShow"/>, checked before Billing is asked to cancel any package charge.</summary>
    public Result EnsureCanMarkNoShow() =>
        Status == BookingStatus.Booked
            ? Result.Success()
            : Error.Rule("booking.not_booked", "Only a booked checkup that never started can be marked as no-show.");

    public Result MarkNoShow(DateTimeOffset now)
    {
        var allowed = EnsureCanMarkNoShow();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        Status = BookingStatus.NoShow;
        CancelledAt = now;
        return Result.Success();
    }

    private Result UpdateStop(Guid itemId, ItineraryStatus status, Guid? userId, string? notes, DateTimeOffset now)
    {
        if (Status is not (BookingStatus.CheckedIn or BookingStatus.InProgress))
        {
            return Error.Rule("booking.not_started", "Check the patient in first.");
        }

        var item = Itinerary.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return Error.NotFound("Itinerary item", itemId);
        }

        if (item.Status != ItineraryStatus.Pending)
        {
            return Error.Rule("itinerary.closed", "This stop is already closed.");
        }

        item.Status = status;
        item.CompletedAt = now;
        item.CompletedByUserId = userId;
        item.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Status = BookingStatus.InProgress;
        return Result.Success();
    }
}

public sealed class ItineraryItem : Entity
{
    public Guid BookingId { get; set; }
    public int Sequence { get; set; }
    public Guid? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ItineraryStatus Status { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>A corporate health-checkup drive (MHC-05): one package booked for many employees on one date.</summary>
public sealed class CorporateCheckupBatch : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// The company: a corporate payer of Billing's payer master, which every employee's check-up is billed to. Null on
    /// batches booked before a batch named its payer, whose company is only <see cref="CorporateName"/>.
    /// </summary>
    public Guid? PayerId { get; set; }

    /// <summary>The company's name, as the payer master had it when the batch was booked.</summary>
    public string CorporateName { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? ContactPhone { get; set; }

    /// <summary>Purchase order / agreement reference.</summary>
    public string? Reference { get; set; }

    public Guid PackageId { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public string? Notes { get; set; }
    public int BookedCount { get; set; }
    public int SkippedCount { get; set; }
}

/// <summary>Raised when a checkup is completed (report hand-over, CRM follow-up, corporate billing).</summary>
public sealed record HealthCheckupCompleted(Guid BookingId, Guid PatientId, Guid FacilityId, Guid PackageId, Guid? CorporateBatchId, DateTimeOffset CompletedAt) : DomainEvent;
