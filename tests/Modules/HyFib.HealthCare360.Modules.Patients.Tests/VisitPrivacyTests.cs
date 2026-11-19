using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// What the visit lists, a visit's details and the OPD queue show of a patient whose chart the user may not open, as the
/// doctor's worklist shows it (SECURITY.md, Restricted records and Sensitive categories).
/// </summary>
public sealed class VisitPrivacyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_restricted_patient_in_the_queue_is_named_by_initials_and_loses_the_complaint()
    {
        var masked = VisitPrivacy.Mask(Entry(), ChartWithholding.Restricted);

        masked.ShouldSatisfyAllConditions(
            e => e.PatientName.ShouldBe("R. K. S. (restricted)"),
            e => e.ChiefComplaint.ShouldBeNull(),
            e => e.Uhid.ShouldBe("NH26000901", "the UHID and the token still say whom to call"),
            e => e.Label.ShouldBe("GM-012"),
            e => e.Age.ShouldBe("61 Y"));
    }

    [Fact]
    public void A_confidential_patient_in_the_queue_keeps_the_name_and_loses_the_complaint()
    {
        var entry = Entry();

        var masked = VisitPrivacy.Mask(entry, ChartWithholding.Confidential);

        masked.PatientName.ShouldBe("Ravi Kumar Sharma", "the name stays, as on the bed board");
        masked.ChiefComplaint.ShouldBeNull();
        masked.ShouldBe(entry with { ChiefComplaint = null });
    }

    [Fact]
    public void Only_a_restricted_record_loses_its_name()
    {
        VisitPrivacy.Name("Ravi Kumar Sharma", ChartWithholding.Restricted).ShouldBe("R. K. S. (restricted)");
        VisitPrivacy.Name("Ravi Kumar Sharma", ChartWithholding.Confidential).ShouldBe("Ravi Kumar Sharma");
        VisitPrivacy.Name("Ravi Kumar Sharma", null).ShouldBe("Ravi Kumar Sharma");
    }

    [Fact]
    public void A_visit_of_a_closed_chart_loses_its_complaint_notes_and_MLC_number_and_keeps_the_rest()
    {
        var visit = Visit();

        var withheld = VisitPrivacy.Withhold(visit);

        withheld.ShouldSatisfyAllConditions(
            v => v.ChiefComplaint.ShouldBeNull(),
            v => v.Notes.ShouldBeNull(),
            v => v.MlcNumber.ShouldBeNull(),
            v => v.IsMlc.ShouldBeTrue("that the visit is medico-legal stays, as the visit list shows it"));
        withheld.ShouldBe(visit with { ChiefComplaint = null, Notes = null, MlcNumber = null });
    }

    private static QueueEntry Entry() => new(
        Guid.CreateVersion7(), "GM-012", TokenStatus.Waiting, TokenPriority.Normal, Guid.CreateVersion7(), Guid.CreateVersion7(), "NH26000901",
        "Ravi Kumar Sharma", Gender.Male, "61 Y", null, null, Now, null, 12, "Weight loss and night sweats", IsFollowUp: false);

    private static VisitDetail Visit() => new(
        Guid.CreateVersion7(), "OP2600981", Guid.CreateVersion7(), VisitType.OutPatient, VisitStatus.Open, Guid.CreateVersion7(), null, null, Now, null,
        IsFollowUp: false, IsFreeFollowUp: false, PreviousVisitId: null, PayerType.Self, PayerId: null, ReferralSource.Self, ReferredBy: null,
        ChiefComplaint: "Assaulted last night", IsMlc: true, MlcNumber: "MLC/2026/0042", Notes: "Brought by the police", CancelReason: null, Token: null, Version: 3);
}
