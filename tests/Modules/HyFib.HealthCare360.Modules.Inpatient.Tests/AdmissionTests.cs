using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class AdmissionTests
{
    private static readonly DateTimeOffset AdmittedAt = At(20, 10);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 9, day), new TimeOnly(hour, minute));

    private static Admission NewAdmission() => new()
    {
        FacilityId = Guid.NewGuid(),
        AdmissionNumber = "NHIP26000042",
        PatientId = Guid.NewGuid(),
        VisitId = Guid.NewGuid(),
        Status = AdmissionStatus.Admitted,
        AdmittedAt = AdmittedAt,
        WardId = Guid.NewGuid(),
        BedId = Guid.NewGuid(),
        PayerType = PayerType.Insurance,
        AdmittingPractitionerId = Guid.NewGuid(),
    };

    [Fact]
    public void Admission_raises_PatientAdmitted_with_the_wards_bed_charge()
    {
        var admission = NewAdmission();
        var bedCharge = Guid.NewGuid();

        admission.MarkAdmitted(bedCharge);

        var admitted = admission.DomainEvents.OfType<PatientAdmitted>().Single();
        admitted.AdmissionId.ShouldBe(admission.Id);
        admitted.AdmissionNumber.ShouldBe("NHIP26000042");
        admitted.BedServiceItemId.ShouldBe(bedCharge);
        admitted.BedId.ShouldBe(admission.BedId);
        admitted.PayerType.ShouldBe(PayerType.Insurance);
        admitted.AdmittedAt.ShouldBe(AdmittedAt);
    }

    [Fact]
    public void Transfer_moves_the_patient_and_raises_PatientTransferred()
    {
        var admission = NewAdmission();
        var fromBed = admission.BedId;
        var toWard = Guid.NewGuid();
        var toBed = Guid.NewGuid();
        var icuCharge = Guid.NewGuid();

        admission.TransferTo(toWard, toBed, icuCharge, At(21, 20)).IsSuccess.ShouldBeTrue();

        admission.WardId.ShouldBe(toWard);
        admission.BedId.ShouldBe(toBed);
        var transferred = admission.DomainEvents.OfType<PatientTransferred>().Single();
        transferred.FromBedId.ShouldBe(fromBed);
        transferred.ToBedId.ShouldBe(toBed);
        transferred.ToBedServiceItemId.ShouldBe(icuCharge);
        transferred.ToWardId.ShouldBe(toWard);
    }

    [Fact]
    public void Transfer_to_the_same_bed_or_before_admission_is_refused()
    {
        var admission = NewAdmission();

        admission.TransferTo(admission.WardId!.Value, admission.BedId!.Value, null, At(21, 9)).Error!.Code.ShouldBe("admission.same_bed");
        admission.TransferTo(Guid.NewGuid(), Guid.NewGuid(), null, At(19, 9)).Error!.Code.ShouldBe("admission.transfer_before_admission");
    }

    [Fact]
    public void A_patient_in_discharge_is_not_transferred()
    {
        var admission = NewAdmission();
        admission.InitiateDischarge(null, null, At(22, 9));

        admission.TransferTo(Guid.NewGuid(), Guid.NewGuid(), null, At(22, 10)).Error!.Code.ShouldBe("admission.not_transferable");
    }

    [Fact]
    public void Routine_discharge_is_initiated_then_completed()
    {
        var admission = NewAdmission();

        admission.InitiateDischarge(Guid.NewGuid(), new DateOnly(2026, 9, 22), At(22, 9)).IsSuccess.ShouldBeTrue();
        admission.CompleteDischarge(DischargeType.Normal, Guid.NewGuid(), "Stable", At(22, 13)).IsSuccess.ShouldBeTrue();

        admission.Status.ShouldBe(AdmissionStatus.Discharged);
        admission.IsActive.ShouldBeFalse();
        admission.DomainEvents.OfType<DischargeInitiated>().Single().InitiatedAt.ShouldBe(At(22, 9));
        var discharged = admission.DomainEvents.OfType<PatientDischarged>().Single();
        discharged.DischargeType.ShouldBe("Normal");
        discharged.VisitId.ShouldBe(admission.VisitId);
        discharged.DischargedAt.ShouldBe(At(22, 13));
    }

    [Fact]
    public void Routine_discharge_without_initiation_is_refused_so_billing_can_prepare_the_final_bill() =>
        NewAdmission().CompleteDischarge(DischargeType.Normal, null, null, At(22, 13)).Error!.Code.ShouldBe("discharge.not_initiated");

    [Theory]
    [InlineData(DischargeType.Expired, AdmissionStatus.Expired)]
    [InlineData(DischargeType.Absconded, AdmissionStatus.Absconded)]
    public void Death_and_abscondence_are_recorded_immediately(DischargeType type, AdmissionStatus expected)
    {
        var admission = NewAdmission();

        admission.CompleteDischarge(type, null, "Informed family and police", At(21, 3)).IsSuccess.ShouldBeTrue();

        admission.Status.ShouldBe(expected);
        admission.DischargeInitiatedAt.ShouldBe(At(21, 3));
    }

    [Theory]
    [InlineData(DischargeType.Normal, AdmissionStatus.Discharged)]
    [InlineData(DischargeType.Referred, AdmissionStatus.Discharged)]
    [InlineData(DischargeType.Lama, AdmissionStatus.Lama)]
    [InlineData(DischargeType.Dama, AdmissionStatus.Dama)]
    [InlineData(DischargeType.Expired, AdmissionStatus.Expired)]
    [InlineData(DischargeType.Absconded, AdmissionStatus.Absconded)]
    public void The_discharge_type_decides_the_final_status(DischargeType type, AdmissionStatus expected)
    {
        var admission = NewAdmission();
        admission.InitiateDischarge(null, null, At(22, 9));

        admission.CompleteDischarge(type, null, "notes", At(22, 12));

        admission.Status.ShouldBe(expected);
        admission.DischargeType.ShouldBe(type);
    }

    [Fact]
    public void Discharge_cannot_predate_the_admission_or_be_repeated()
    {
        var admission = NewAdmission();
        admission.InitiateDischarge(null, null, At(22, 9));

        admission.CompleteDischarge(DischargeType.Normal, null, null, At(19, 9)).Error!.Code.ShouldBe("discharge.before_admission");
        admission.CompleteDischarge(DischargeType.Normal, null, null, At(22, 12)).IsSuccess.ShouldBeTrue();
        admission.CompleteDischarge(DischargeType.Normal, null, null, At(22, 13)).Error!.Code.ShouldBe("admission.closed");
    }

    [Fact]
    public void Revoking_a_discharge_keeps_the_stay_going_and_says_so_to_billing()
    {
        var admission = NewAdmission();
        admission.InitiateDischarge(null, null, At(22, 9));
        admission.ClearDomainEvents();

        admission.RevokeDischarge(At(22, 11)).IsSuccess.ShouldBeTrue();

        admission.Status.ShouldBe(AdmissionStatus.Admitted);
        admission.DischargeInitiatedAt.ShouldBeNull();
        var revoked = admission.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<DischargeRevoked>();
        (revoked.AdmissionId, revoked.PatientId, revoked.RevokedAt).ShouldBe((admission.Id, admission.PatientId, At(22, 11)));
        admission.RevokeDischarge(At(22, 12)).Error!.Code.ShouldBe("discharge.not_initiated");
    }

    [Fact]
    public void Only_an_admission_in_progress_can_be_cancelled()
    {
        var admission = NewAdmission();
        admission.Cancel("  Wrong patient selected ", At(20, 11)).IsSuccess.ShouldBeTrue();
        admission.Status.ShouldBe(AdmissionStatus.Cancelled);
        var cancelled = admission.DomainEvents.OfType<AdmissionCancelled>().Single();
        cancelled.AdmissionId.ShouldBe(admission.Id);
        cancelled.VisitId.ShouldBe(admission.VisitId);
        cancelled.FacilityId.ShouldBe(admission.FacilityId);
        cancelled.Reason.ShouldBe("Wrong patient selected");
        cancelled.CancelledAt.ShouldBe(At(20, 11));

        var inDischarge = NewAdmission();
        inDischarge.InitiateDischarge(null, null, At(22, 9));
        inDischarge.Cancel("x", At(22, 10)).Error!.Code.ShouldBe("admission.not_cancellable");
    }

    [Theory]
    [InlineData(DischargeType.Normal, true, true, false)]
    [InlineData(DischargeType.Referred, true, true, false)]
    [InlineData(DischargeType.Lama, true, true, true)]
    [InlineData(DischargeType.Dama, true, true, true)]
    [InlineData(DischargeType.Absconded, false, false, true)]
    [InlineData(DischargeType.Expired, false, false, false)]
    public void Discharge_rules_by_type(DischargeType type, bool needsClearance, bool notifiesPatient, bool needsNotes)
    {
        DischargeRules.RequiresBillingClearance(type).ShouldBe(needsClearance);
        DischargeRules.RequiresInitiation(type).ShouldBe(needsClearance);
        DischargeRules.NotifiesPatient(type).ShouldBe(notifiesPatient);
        DischargeRules.RequiresNotes(type).ShouldBe(needsNotes);
    }

    [Fact]
    public void Nursing_charting_stays_open_for_24_hours_after_discharge()
    {
        var discharged = At(22, 12);

        ChartingRules.IsOpen(AdmissionStatus.Admitted, null, At(25, 12)).ShouldBeTrue();
        ChartingRules.IsOpen(AdmissionStatus.Discharged, discharged, discharged.AddHours(23)).ShouldBeTrue();
        ChartingRules.IsOpen(AdmissionStatus.Discharged, discharged, discharged.AddHours(25)).ShouldBeFalse();
        ChartingRules.IsOpen(AdmissionStatus.Cancelled, discharged, discharged.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void Length_of_stay_counts_calendar_days_with_a_minimum_of_one()
    {
        LengthOfStay.Days(At(20, 10), At(20, 18)).ShouldBe(1);
        LengthOfStay.Days(At(20, 23), At(21, 1)).ShouldBe(1);
        LengthOfStay.Days(At(20, 23), At(23, 9)).ShouldBe(3);
    }

    [Fact]
    public void Single_sex_wards_refuse_only_the_opposite_sex()
    {
        var today = new DateOnly(2026, 9, 25);

        WardEligibility.Check(WardType.General, WardGenderPolicy.MaleOnly, Gender.Female, null, today).Error!.Code.ShouldBe("ward.gender");
        WardEligibility.Check(WardType.General, WardGenderPolicy.FemaleOnly, Gender.Male, null, today).Error!.Code.ShouldBe("ward.gender");
        WardEligibility.Check(WardType.General, WardGenderPolicy.MaleOnly, Gender.Male, null, today).IsSuccess.ShouldBeTrue();
        WardEligibility.Check(WardType.General, WardGenderPolicy.MaleOnly, Gender.Unknown, null, today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Maternity_wards_admit_women_only() =>
        WardEligibility.Check(WardType.Maternity, WardGenderPolicy.Any, Gender.Male, null, new DateOnly(2026, 9, 25)).Error!.Code.ShouldBe("ward.maternity");

    [Fact]
    public void Nicu_admits_neonates_and_picu_admits_children()
    {
        var today = new DateOnly(2026, 9, 25);

        WardEligibility.Check(WardType.Nicu, WardGenderPolicy.Any, Gender.Male, today.AddDays(-10), today).IsSuccess.ShouldBeTrue();
        WardEligibility.Check(WardType.Nicu, WardGenderPolicy.Any, Gender.Male, today.AddDays(-29), today).Error!.Code.ShouldBe("ward.neonatal_only");
        WardEligibility.Check(WardType.Picu, WardGenderPolicy.Any, Gender.Female, today.AddYears(-12), today).IsSuccess.ShouldBeTrue();
        WardEligibility.Check(WardType.Picu, WardGenderPolicy.Any, Gender.Female, today.AddYears(-30), today).Error!.Code.ShouldBe("ward.paediatric_only");
        WardEligibility.Check(WardType.Nicu, WardGenderPolicy.Any, Gender.Male, null, today).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(WardType.Icu, true)]
    [InlineData(WardType.Nicu, true)]
    [InlineData(WardType.Picu, true)]
    [InlineData(WardType.Hdu, true)]
    [InlineData(WardType.General, false)]
    [InlineData(WardType.Private, false)]
    public void Critical_care_wards_bill_the_icu_visit_tariff(WardType type, bool criticalCare)
    {
        new Ward { Type = type }.IsCriticalCare.ShouldBe(criticalCare);
        new ConsultantVisit { IsCriticalCare = criticalCare }.ServiceCode.ShouldBe(criticalCare ? "VISIT-DR-ICU" : "VISIT-DR");
    }
}
