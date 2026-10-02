using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Donor side of the blood bank (BB-01..04): registration, screening against the NBTC criteria, collection,
/// component separation and grouping/TTI results that release (or discard) units.
/// </summary>
public sealed class DonorService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    IAuditLogger audit,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<Guid>> RegisterAsync(SaveDonorRequest request, CancellationToken ct)
    {
        var phone = Phone.Normalize(request.Phone)!;
        var firstName = request.FirstName.Trim();
        var duplicate = await db.Set<Donor>().AsNoTracking()
            .Where(d => d.Phone == phone && d.DateOfBirth == request.DateOfBirth && EF.Functions.ILike(d.FirstName, firstName))
            .Select(d => d.DonorNumber)
            .FirstOrDefaultAsync(ct);
        if (duplicate is not null)
        {
            return Error.Conflict("bloodbank.donor_exists", $"This donor is already registered as {duplicate}.");
        }

        var donor = new Donor();
        Apply(donor, request, phone);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        donor.DonorNumber = await numbers.NextAsync(SpecialtyCareNumberSeries.BloodDonor, null, ct);
        db.Add(donor);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return donor.Id;
    }

    public async Task<Result> UpdateAsync(Guid donorId, SaveDonorRequest request, CancellationToken ct)
    {
        var donor = await db.Set<Donor>().FirstOrDefaultAsync(d => d.Id == donorId, ct);
        if (donor is null)
        {
            return Error.NotFound(nameof(Donor), donorId);
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(donor, version);
        }

        Apply(donor, request, Phone.Normalize(request.Phone)!);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DeferAsync(Guid donorId, DeferDonorRequest request, CancellationToken ct)
    {
        var donor = await db.Set<Donor>().FirstOrDefaultAsync(d => d.Id == donorId, ct);
        if (donor is null)
        {
            return Error.NotFound(nameof(Donor), donorId);
        }

        donor.Defer(request.Until, request.Permanent, request.Reason);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Screens the donor; a deferral found here is also written to the donor so every branch sees it.</summary>
    public async Task<Result<DonorScreening>> ScreenAsync(Guid donorId, ScreenDonorRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var donor = await db.Set<Donor>().FirstOrDefaultAsync(d => d.Id == donorId, ct);
        if (donor is null)
        {
            return Error.NotFound(nameof(Donor), donorId);
        }

        var today = time.Today();
        if (!donor.IsActive || donor.IsDeferredOn(today))
        {
            return Error.Rule("bloodbank.donor_deferred", donor.IsPermanentlyDeferred
                ? $"This donor is permanently deferred: {donor.DeferralReason}"
                : $"This donor is deferred until {donor.DeferredUntil:dd-MM-yyyy}: {donor.DeferralReason}");
        }

        var missing = DonorEligibility.MissingAnswers(donor.Gender, request.Answers);
        if (missing.Count > 0)
        {
            return Error.Field("answers", $"Answer every screening question ({string.Join(", ", missing)}).");
        }

        var result = DonorEligibility.Evaluate(new EligibilityInput(
            donor.Gender, donor.DateOfBirth, donor.DonationCount == 0,
            donor.LastDonationAt is { } last ? SpecialtyClock.ToLocalDate(last) : null,
            new DonorVitals(request.HaemoglobinGdl, request.WeightKg, request.Systolic, request.Diastolic, request.Pulse, request.TemperatureC),
            request.Answers, today));

        var stamp = currentUser.Stamp(time.GetUtcNow());
        var screening = new DonorScreening
        {
            FacilityId = facilityId,
            DonorId = donor.Id,
            ScreenedAt = stamp.At,
            ScreenedByName = stamp.Name,
            HaemoglobinGdl = request.HaemoglobinGdl,
            WeightKg = request.WeightKg,
            Systolic = request.Systolic,
            Diastolic = request.Diastolic,
            Pulse = request.Pulse,
            TemperatureC = request.TemperatureC,
            Answers = [.. request.Answers],
            Outcome = result.Outcome,
            Reasons = [.. result.Reasons],
            DeferredUntil = result.DeferredUntil,
            MaxBagVolumeMl = result.MaxBagVolumeMl,
        };
        db.Add(screening);

        if (!result.IsEligible)
        {
            donor.Defer(result.DeferredUntil, result.Outcome == DonorEligibilityOutcome.PermanentlyDeferred, string.Join(" ", result.Reasons));
        }

        await db.SaveChangesAsync(ct);
        return screening;
    }

    public async Task<Result<Guid>> CollectAsync(Guid donorId, CollectDonationRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var donor = await db.Set<Donor>().FirstOrDefaultAsync(d => d.Id == donorId, ct);
        var screening = await db.Set<DonorScreening>().FirstOrDefaultAsync(s => s.Id == request.ScreeningId && s.DonorId == donorId, ct);
        if (donor is null || screening is null)
        {
            return Error.NotFound(nameof(DonorScreening), request.ScreeningId);
        }

        var now = time.GetUtcNow();
        var usable = screening.EnsureUsableForDonation(request.VolumeMl, now);
        if (usable.IsFailure)
        {
            return usable.Error!;
        }

        if (request.ReplacementForPatientId is { } patientId)
        {
            var patient = await SpecialtyLookups.EnsurePatientAsync(db, patientId, ct);
            if (patient.IsFailure)
            {
                return patient.Error!;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var bagNumber = string.IsNullOrWhiteSpace(request.BagNumber)
            ? await numbers.NextAsync(SpecialtyCareNumberSeries.BloodBag, facilityId, ct)
            : request.BagNumber.Trim().ToUpperInvariant();
        if (await db.Set<Donation>().IgnoreQueryFilters().AnyAsync(d => d.BagNumber == bagNumber, ct))
        {
            return Error.Conflict("bloodbank.bag_in_use", $"Bag number {bagNumber} is already used.");
        }

        var collectedAt = request.CollectedAt ?? now;
        var donation = new Donation
        {
            FacilityId = facilityId,
            DonorId = donor.Id,
            ScreeningId = screening.Id,
            BagNumber = bagNumber,
            BagType = request.BagType,
            VolumeMl = request.VolumeMl,
            Type = request.Type,
            ReplacementForPatientId = request.Type == DonationType.Replacement ? request.ReplacementForPatientId : null,
            CollectedAt = collectedAt,
            CollectedByName = currentUser.Stamp(now).Name,
            Status = DonationStatus.Collected,
            DonorReaction = string.IsNullOrWhiteSpace(request.DonorReaction) ? null : request.DonorReaction.Trim(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        db.Add(donation);
        screening.DonationId = donation.Id;
        donor.RecordDonation(collectedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishAsync(facilityId, ct);
        return donation.Id;
    }

    /// <summary>Prepares component units (in quarantine until grouping and TTI are complete).</summary>
    public async Task<Result<IReadOnlyList<BloodComponent>>> SeparateAsync(Guid donationId, SeparateComponentsRequest request, CancellationToken ct)
    {
        var donation = await db.Set<Donation>().FirstOrDefaultAsync(d => d.Id == donationId, ct);
        if (donation is null)
        {
            return Error.NotFound(nameof(Donation), donationId);
        }

        var now = time.GetUtcNow();
        var allowed = donation.EnsureCanSeparate();
        var plan = allowed.IsFailure
            ? allowed
            : ComponentShelfLife.ValidateSeparation(donation.BagType, request.Components.Select(c => c.Type).ToList(), donation.CollectedAt, now);
        if (plan.IsFailure)
        {
            return plan.Error!;
        }

        var components = request.Components
            .Select(c => BloodComponent.Prepare(donation, c.Type, c.VolumeMl, string.IsNullOrWhiteSpace(c.StorageLocation) ? null : c.StorageLocation.Trim(), now))
            .ToList();
        if (donation.Tti.IsComplete)
        {
            components.ForEach(c => c.ApplyLabResults(donation.BloodGroup, donation.Tti, now));
        }

        db.AddRange(components);
        donation.MarkSeparated(now);
        await db.SaveChangesAsync(ct);
        await PublishAsync(donation.FacilityId, ct);
        return components;
    }

    /// <summary>
    /// Records grouping and TTI screening for a donation and every component made from it. Reactive results discard
    /// the units, defer the donor and alert blood bank officers so the donor can be recalled for counselling.
    /// </summary>
    public async Task<Result> RecordLabResultsAsync(Guid donationId, RecordLabResultsRequest request, CancellationToken ct)
    {
        var donation = await db.Set<Donation>().FirstOrDefaultAsync(d => d.Id == donationId, ct);
        if (donation is null)
        {
            return Error.NotFound(nameof(Donation), donationId);
        }

        var donor = await db.Set<Donor>().FirstAsync(d => d.Id == donation.DonorId, ct);
        var now = time.GetUtcNow();
        var tti = request.ToPanel();
        var recorded = donation.RecordLabResults(request.BloodGroup, tti, now);
        if (recorded.IsFailure)
        {
            return recorded;
        }

        var grouped = donor.ConfirmBloodGroup(request.BloodGroup);
        if (grouped.IsFailure)
        {
            return grouped;
        }

        var components = await db.Set<BloodComponent>().Where(c => c.DonationId == donationId).ToListAsync(ct);
        components.ForEach(c => c.ApplyLabResults(request.BloodGroup, tti, now));

        var deferral = DonorEligibility.DeferralForReactiveTti(tti);
        if (deferral is { } d)
        {
            donor.Defer(d.Days is { } days ? time.Today().AddDays(days) : null, d.Permanent, d.Reason);
            audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.tti_reactive", $"Reactive TTI screen on donation {donation.BagNumber}")
            {
                EntityType = nameof(Donation),
                EntityId = donation.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        await db.SaveChangesAsync(ct);
        if (deferral is not null)
        {
            // Donor identity and marker names stay out of the notification text; officers open the donation record.
            await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankDonors, donation.FacilityId,
                "Reactive TTI screen: donor recall needed", $"Donation {donation.BagNumber} is reactive. Units discarded; recall the donor for counselling.",
                $"/blood-bank/donations/{donation.Id}", "bloodbank", "warning", ct);
        }

        await PublishAsync(donation.FacilityId, ct);
        return Result.Success();
    }

    private static void Apply(Donor donor, SaveDonorRequest request, string phone)
    {
        donor.SetName(request.FirstName, request.LastName);
        donor.Gender = request.Gender;
        donor.DateOfBirth = request.DateOfBirth;
        if (!donor.BloodGroupConfirmed && request.BloodGroup is { } group)
        {
            donor.BloodGroup = group;
        }

        donor.Phone = phone;
        donor.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        donor.Address = request.Address ?? new Address();
        donor.Occupation = string.IsNullOrWhiteSpace(request.Occupation) ? null : request.Occupation.Trim();
        donor.ConsentToNotify = request.ConsentToNotify;
        donor.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        donor.IsActive = request.IsActive;
    }

    private Task PublishAsync(Guid facilityId, CancellationToken ct) =>
        realtime.FacilityChangedAsync(facilityId, SpecialtyRealtime.BloodBankChanged, new { area = "inventory" }, ct);
}
