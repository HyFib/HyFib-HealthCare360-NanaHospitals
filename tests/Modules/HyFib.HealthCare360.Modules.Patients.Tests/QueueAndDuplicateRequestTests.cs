using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// SRS SEC-06: a room called to fits the board's column (a longer one was a database error, a 500), and the duplicate
/// check, asked while the form is half filled, holds each detail to its length without refusing it half typed.
/// </summary>
public sealed class QueueAndDuplicateRequestTests
{
    [Fact]
    public void A_patient_is_called_to_a_room_the_board_can_show()
    {
        new CallTokenRequestValidator().Validate(new CallTokenRequest("Consultation room 4")).IsValid.ShouldBeTrue();
        new CallTokenRequestValidator().Validate(new CallTokenRequest(null)).IsValid.ShouldBeTrue();
        new CallTokenRequestValidator().Validate(new CallTokenRequest(new string('r', 33))).IsValid.ShouldBeFalse();
        new CallNextRequestValidator().Validate(new CallNextRequest(Guid.Empty, Guid.Empty, new string('r', 33))).Errors
            .Select(e => e.PropertyName).Distinct().ShouldBe(["DepartmentId", "PractitionerId", "Room"], ignoreOrder: true);
    }

    [Fact]
    public void The_duplicate_check_takes_details_half_typed_but_not_too_long()
    {
        var validator = new DuplicateCheckRequestValidator();

        validator.Validate(new DuplicateCheckRequest("Ra", null, null, "+91 98765 43210", "91-1234", "1234 5678")).IsValid.ShouldBeTrue();
        validator.Validate(new DuplicateCheckRequest(new string('a', 101), new string('b', 101), null, new string('9', 21), null, null))
            .Errors.Select(e => e.PropertyName).ShouldBe(["FirstName", "LastName", "Phone"], ignoreOrder: true);
    }
}
