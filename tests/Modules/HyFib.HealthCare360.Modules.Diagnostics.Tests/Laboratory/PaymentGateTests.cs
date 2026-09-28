using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

public sealed class PaymentGateTests
{
    private static readonly PaymentLine[] Lines = [new("cbc", "CBC"), new("lft", "LFT")];

    [Fact]
    public void Out_patients_must_pay_before_the_sample_is_drawn()
    {
        var result = PaymentGate.Check(PatientClass.OutPatient, true, Lines, States(ChargeBillingState.Paid, ChargeBillingState.Billed));

        result.Error!.Code.ShouldBe("lab.payment_pending");
        result.Error.Type.ShouldBe(ErrorType.BusinessRule);
        result.Error.Message.ShouldContain("LFT");
        result.Error.Message.ShouldNotContain("CBC");
        result.Error.Data.ShouldNotBeNull();
    }

    [Fact]
    public void Paid_tests_are_collected() =>
        PaymentGate.Check(PatientClass.OutPatient, true, Lines, States(ChargeBillingState.Paid, ChargeBillingState.Paid)).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(PatientClass.InPatient)]
    [InlineData(PatientClass.Emergency)]
    [InlineData(PatientClass.DayCare)]
    public void In_patient_emergency_and_day_care_work_is_never_held_at_the_counter(PatientClass patientClass) =>
        PaymentGate.Check(patientClass, true, Lines, States(ChargeBillingState.Unbilled, ChargeBillingState.NotFound)).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Walk_in_and_outside_referral_patients_pay_first() =>
        PaymentGate.Check(PatientClass.External, true, Lines, States(ChargeBillingState.Unbilled, ChargeBillingState.Paid)).IsFailure.ShouldBeTrue();

    [Fact]
    public void Missing_or_cancelled_charges_are_not_treated_as_paid()
    {
        PaymentGate.Check(PatientClass.OutPatient, true, Lines, new Dictionary<string, ChargeBillingState>()).IsFailure.ShouldBeTrue();
        PaymentGate.Check(PatientClass.OutPatient, true, Lines, States(ChargeBillingState.Paid, ChargeBillingState.Cancelled)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Health_checkup_package_tests_are_settled_with_the_package_not_per_test()
    {
        PaymentLine[] lines = [new("cbc", "CBC", CoveredByPackage: true), new("lft", "LFT")];

        PaymentGate.Check(PatientClass.OutPatient, true, lines, new Dictionary<string, ChargeBillingState> { ["lft"] = ChargeBillingState.Paid })
            .IsSuccess.ShouldBeTrue();
        PaymentGate.Check(PatientClass.OutPatient, true, lines, new Dictionary<string, ChargeBillingState>()).Error!.Message
            .ShouldNotContain("CBC");
        PaymentGate.Check(PatientClass.OutPatient, true, [lines[0]], new Dictionary<string, ChargeBillingState>()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_facility_can_switch_the_gate_off() =>
        PaymentGate.Check(PatientClass.OutPatient, false, Lines, States(ChargeBillingState.Unbilled, ChargeBillingState.Unbilled)).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(VisitType.OutPatient, false, PatientClass.OutPatient)]
    [InlineData(VisitType.Telehealth, false, PatientClass.OutPatient)]
    [InlineData(VisitType.HealthCheckup, false, PatientClass.OutPatient)]
    [InlineData(VisitType.Emergency, false, PatientClass.Emergency)]
    [InlineData(VisitType.DayCare, false, PatientClass.DayCare)]
    [InlineData(VisitType.InPatient, false, PatientClass.InPatient)]
    [InlineData(VisitType.OutPatient, true, PatientClass.InPatient)]
    public void Patient_class_follows_the_visit_and_an_admission_always_wins(VisitType visitType, bool admitted, PatientClass expected) =>
        PatientClasses.From(visitType, admitted).ShouldBe(expected);

    [Fact]
    public void Orders_without_a_visit_are_external() =>
        PatientClasses.From(null, hasAdmission: false).ShouldBe(PatientClass.External);

    private static Dictionary<string, ChargeBillingState> States(ChargeBillingState cbc, ChargeBillingState lft) =>
        new() { ["cbc"] = cbc, ["lft"] = lft };
}
