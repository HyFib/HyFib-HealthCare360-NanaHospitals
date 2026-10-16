using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class ProcedureRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 5, 30, 0, TimeSpan.Zero);

    private static ProcedureDetails Details(string display = "Wound suturing", string? code = null, string? outcome = null) =>
        new(Now.AddHours(-1), code, code is null ? null : CodeReleases.Icd10Pcs, display, Guid.CreateVersion7(), null, ProcedureSide.NotApplicable, null,
            outcome, null);

    [Fact]
    public void A_recorded_procedure_keeps_cleaned_values()
    {
        var details = new ProcedureDetails(Now, " 0dtj4zz ", CodeReleases.Icd10Pcs, "  Laparoscopic appendicectomy ", null, "  ", ProcedureSide.Right,
            " ", " Uneventful ", "");

        var record = ProcedureRecord.Record(Guid.CreateVersion7(), details, Guid.CreateVersion7());

        record.Code.ShouldBe("0DTJ4ZZ");
        record.CodeSystem.ShouldBe(CodeReleases.Icd10Pcs);
        record.Display.ShouldBe("Laparoscopic appendicectomy");
        record.PerformerName.ShouldBeNull();
        record.BodySite.ShouldBeNull();
        record.Outcome.ShouldBe("Uneventful");
        record.Notes.ShouldBeNull();
        record.Side.ShouldBe(ProcedureSide.Right);
        record.Status.ShouldBe(ProcedureStatus.Completed);
        record.Source.ShouldBe(ProcedureSource.Recorded);
    }

    [Fact]
    public void A_procedure_without_a_code_has_no_code_system() =>
        ProcedureRecord.Record(Guid.CreateVersion7(), Details() with { CodeSystem = CodeReleases.Icd10Pcs }, null).CodeSystem.ShouldBeNull();

    [Fact]
    public void An_operation_from_the_theatre_names_its_booking()
    {
        var booking = Guid.CreateVersion7();

        var record = ProcedureRecord.FromSurgery(Guid.CreateVersion7(), booking, Details("Laparoscopic cholecystectomy"));

        record.Source.ShouldBe(ProcedureSource.OperationTheatre);
        record.SurgeryBookingId.ShouldBe(booking);
        record.RecordedByUserId.ShouldBeNull();
    }

    [Fact]
    public void A_correction_replaces_the_details_and_keeps_the_reason()
    {
        var record = ProcedureRecord.Record(Guid.CreateVersion7(), Details(), null);
        var user = Guid.CreateVersion7();

        record.Correct(Details("Incision and drainage", outcome: "Pus drained"), "  Wrong procedure picked ", user, Now).IsSuccess.ShouldBeTrue();

        record.Display.ShouldBe("Incision and drainage");
        record.Outcome.ShouldBe("Pus drained");
        record.Status.ShouldBe(ProcedureStatus.Amended);
        record.StatusReason.ShouldBe("Wrong procedure picked");
        record.StatusChangedAt.ShouldBe(Now);
        record.StatusChangedByUserId.ShouldBe(user);
    }

    [Fact]
    public void A_record_entered_in_error_can_be_neither_corrected_nor_voided_again()
    {
        var record = ProcedureRecord.Record(Guid.CreateVersion7(), Details(), null);

        record.MarkEnteredInError("Wrong patient", null, Now).IsSuccess.ShouldBeTrue();

        record.IsEnteredInError.ShouldBeTrue();
        record.StatusReason.ShouldBe("Wrong patient");
        record.Correct(Details("Something else"), "Second thoughts", null, Now).Error!.Code.ShouldBe("procedure.entered_in_error");
        record.MarkEnteredInError("Again", null, Now).Error!.Code.ShouldBe("procedure.already_voided");
        record.Display.ShouldBe("Wound suturing");
    }

    [Fact]
    public void A_request_needs_a_description_or_a_code_and_a_time_not_in_the_future()
    {
        var validator = new RecordProcedureRequestValidator(new FixedClock(Now));
        RecordProcedureRequest Request(string? code, string? display, DateTimeOffset at) =>
            new(null, at, code, display, null, null, ProcedureSide.NotApplicable, null, null, null);

        validator.Validate(Request(null, "Wound suturing", Now)).IsValid.ShouldBeTrue();
        validator.Validate(Request("0DTJ4ZZ", null, Now)).IsValid.ShouldBeTrue();
        validator.Validate(Request(null, " ", Now)).Errors.ShouldContain(e => e.PropertyName == "Display");
        validator.Validate(Request(null, "Wound suturing", Now.AddMinutes(4))).IsValid.ShouldBeTrue("Clocks differ a little.");
        validator.Validate(Request(null, "Wound suturing", Now.AddHours(1))).Errors.ShouldContain(e => e.PropertyName == "PerformedAt");
    }

    [Fact]
    public void A_correction_needs_a_reason()
    {
        var validator = new CorrectProcedureRequestValidator(new FixedClock(Now));

        validator.Validate(new CorrectProcedureRequest(Now, null, "Suturing", null, null, ProcedureSide.Left, null, null, null, ""))
            .Errors.ShouldContain(e => e.PropertyName == "Reason");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
