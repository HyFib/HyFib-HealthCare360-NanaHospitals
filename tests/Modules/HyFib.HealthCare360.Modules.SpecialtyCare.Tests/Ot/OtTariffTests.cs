using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

public sealed class OtTariffTests
{
    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(60, 1.0)]
    [InlineData(61, 1.5)]
    [InlineData(90, 1.5)]
    [InlineData(91, 2.0)]
    [InlineData(125, 2.5)]
    [InlineData(240, 4.0)]
    public void Theatre_time_is_billed_per_hour_with_half_hour_slabs_after_the_first_hour(double minutes, double hours) =>
        OtTariff.BillableHours(minutes).ShouldBe((decimal)hours);

    [Theory]
    [InlineData(TheatreType.Minor, OtServiceCodes.MinorTheatreHour)]
    [InlineData(TheatreType.Major, OtServiceCodes.MajorTheatreHour)]
    [InlineData(TheatreType.Cardiac, OtServiceCodes.MajorTheatreHour)]
    [InlineData(TheatreType.Ortho, OtServiceCodes.MajorTheatreHour)]
    [InlineData(TheatreType.Emergency, OtServiceCodes.MajorTheatreHour)]
    public void Minor_theatres_bill_the_minor_rate(TheatreType type, string code) => OtTariff.HourlyChargeCode(type).ShouldBe(code);

    [Fact]
    public void Implant_without_lot_or_serial_is_refused_for_traceability()
    {
        SurgeryMaterial.Validate(MaterialKind.Implant, null, " ", null, TestData.Today).IsFailure.ShouldBeTrue();
        SurgeryMaterial.Validate(MaterialKind.Implant, "LOT-42", null, null, TestData.Today).IsSuccess.ShouldBeTrue();
        SurgeryMaterial.Validate(MaterialKind.Implant, null, "SN-9", null, TestData.Today).IsSuccess.ShouldBeTrue();
        SurgeryMaterial.Validate(MaterialKind.Consumable, null, null, null, TestData.Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Expired_implant_or_consumable_is_refused()
    {
        SurgeryMaterial.Validate(MaterialKind.Implant, "LOT-1", null, TestData.Today.AddDays(-1), TestData.Today).Error!.Code.ShouldBe("material.expired");
        SurgeryMaterial.Validate(MaterialKind.Consumable, null, null, TestData.Today, TestData.Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Material_amount_is_rounded_to_paise() =>
        new SurgeryMaterial { Quantity = 3, UnitPrice = 333.335m }.Amount.ShouldBe(1000.01m);

    [Fact]
    public void Signed_anaesthesia_record_is_locked()
    {
        var record = new AnaesthesiaRecord { AsaGrade = AsaGrade.II };
        record.Sign("Dr. Rao", TestData.Now).IsSuccess.ShouldBeTrue();

        record.AddVitals([new AnaesthesiaVitals(TestData.Now, 80, 120, 80, 99, 35, 36.8m, 14)]).Error!.Code.ShouldBe("anaesthesia.signed");
        record.Sign("Dr. Rao", TestData.Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Anaesthesia_record_needs_an_asa_grade_before_signing() =>
        new AnaesthesiaRecord().Sign("Dr. Rao", TestData.Now).Error!.Fields!.ShouldContainKey("asaGrade");

    [Fact]
    public void Vitals_chart_stays_in_time_order_when_rows_are_charted_late()
    {
        var record = new AnaesthesiaRecord();
        record.AddVitals([new AnaesthesiaVitals(TestData.Now.AddMinutes(10), 90, null, null, null, null, null, null)]).IsSuccess.ShouldBeTrue();
        record.AddVitals([new AnaesthesiaVitals(TestData.Now, 80, null, null, null, null, null, null)]).IsSuccess.ShouldBeTrue();

        record.Vitals.Select(v => v.HeartRate).ShouldBe([80, 90]);
    }

    [Fact]
    public void Operation_note_needs_the_procedure_before_signing_and_is_then_locked()
    {
        var note = new OperationNote();
        note.Sign("Dr. Pillai", TestData.Now).IsFailure.ShouldBeTrue();

        note.ProcedurePerformed = "Laparoscopic cholecystectomy";
        note.Sign("Dr. Pillai", TestData.Now).IsSuccess.ShouldBeTrue();
        note.EnsureEditable().Error!.Code.ShouldBe("operation_note.signed");
    }
}
