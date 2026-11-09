using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Clinical.Domain.TelemedicinePrescribing;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Telemedicine Practice Guidelines 2020, section 3.7.4: List O in any teleconsultation, List A in a first one only by
/// video, List B only added on in a follow-up, the Prohibited List (Schedule X and NDPS) never. A medicine on no list is
/// kept out of a first teleconsultation.
/// </summary>
public sealed class TelemedicinePrescribingTests
{
    private static readonly Consult FirstByAudio = new(ByVideo: false, IsFollowUp: false);
    private static readonly Consult FirstByVideo = new(ByVideo: true, IsFollowUp: false);
    private static readonly Consult FollowUpByAudio = new(ByVideo: false, IsFollowUp: true);
    private static readonly Consult FollowUpByVideo = new(ByVideo: true, IsFollowUp: true);

    public static TheoryData<TelemedicineList?, bool, bool, string?> Rules() => new()
    {
        // List O: any teleconsultation.
        { TelemedicineList.O, false, false, null },
        { TelemedicineList.O, true, false, null },
        { TelemedicineList.O, false, true, null },

        // List A: a first teleconsultation only by video; a follow-up however held.
        { TelemedicineList.A, false, false, ListANeedsVideoCode },
        { TelemedicineList.A, true, false, null },
        { TelemedicineList.A, false, true, null },
        { TelemedicineList.A, true, true, null },

        // List B: only a follow-up, video or not.
        { TelemedicineList.B, false, false, ListBNeedsFollowUpCode },
        { TelemedicineList.B, true, false, ListBNeedsFollowUpCode },
        { TelemedicineList.B, false, true, null },

        // On no list: as List B, so never a first teleconsultation.
        { null, true, false, UnlistedCode },
        { null, false, true, null },

        // Prohibited: never.
        { TelemedicineList.Prohibited, true, false, ProhibitedCode },
        { TelemedicineList.Prohibited, true, true, ProhibitedCode },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_list_is_prescribed_only_in_the_teleconsultations_the_guidelines_allow(TelemedicineList? list, bool byVideo, bool followUp, string? refusal)
    {
        var refused = Refused([new("Medicine 1", DrugSchedule.H, false, list)], new Consult(byVideo, followUp));

        if (refusal is null)
        {
            refused.ShouldBeEmpty();
        }
        else
        {
            refused.ShouldHaveSingleItem().Code.ShouldBe(refusal);
        }
    }

    [Fact]
    public void Schedule_X_and_NDPS_medicines_are_prohibited_whatever_list_the_item_master_has()
    {
        var refused = Refused(
            [
                new("Paracetamol 500 mg", DrugSchedule.None, false, TelemedicineList.O),
                new("Alprazolam 0.5 mg", DrugSchedule.X, false, TelemedicineList.O),
                new("Tramadol 50 mg", DrugSchedule.H1, true, TelemedicineList.A),
                new("Alprazolam 0.5 mg", DrugSchedule.X, false, TelemedicineList.O),
            ],
            FollowUpByVideo);

        refused.Select(r => (r.Name, r.List, r.Code)).ShouldBe([
            ("Alprazolam 0.5 mg", "Prohibited", ProhibitedCode),
            ("Tramadol 50 mg", "Prohibited", ProhibitedCode),
        ]);
    }

    [Fact]
    public void The_refusal_names_each_medicine_and_why_and_carries_the_gravest_code()
    {
        var refused = Refused(
            [
                new("Amoxicillin 500 mg", DrugSchedule.H, false, TelemedicineList.B),
                new("Morphine 10 mg", DrugSchedule.X, true),
                new("Cetirizine 10 mg", DrugSchedule.None, false, TelemedicineList.A),
                new("Typed-in syrup", DrugSchedule.None, false),
            ],
            FirstByAudio);

        var refusal = Refusal(refused, FirstByAudio);

        refusal.Code.ShouldBe(ProhibitedCode);
        refusal.Type.ShouldBe(ErrorType.BusinessRule);
        refusal.Message.ShouldContain("Amoxicillin 500 mg cannot be prescribed in this teleconsultation: it is on List B");
        refusal.Message.ShouldContain("Morphine 10 mg cannot be prescribed in a teleconsultation");
        refusal.Message.ShouldContain("See the patient in person");
        refusal.Message.ShouldContain("Cetirizine 10 mg cannot be prescribed in this teleconsultation: it is on List A");
        refusal.Message.ShouldContain("Typed-in syrup cannot be prescribed in a first teleconsultation: it is on no list");
        refused.Select(r => r.List).ShouldBe(["B", "Prohibited", "A", "None"]);

        Refusal([refused[0], refused[3]], FirstByAudio).Code.ShouldBe(UnlistedCode);
        Refusal([refused[2]], FirstByAudio).Code.ShouldBe(ListANeedsVideoCode);
    }

    [Fact]
    public void Nothing_is_refused_when_every_medicine_may_be_prescribed()
    {
        Refused([new("Paracetamol 500 mg", DrugSchedule.None, false, TelemedicineList.O)], FirstByAudio).ShouldBeEmpty();
        Refused([new("Cetirizine 10 mg", DrugSchedule.None, false, TelemedicineList.A)], FirstByVideo).ShouldBeEmpty();
        Refused([new("Metformin 500 mg", DrugSchedule.H, false, TelemedicineList.B)], FollowUpByAudio).ShouldBeEmpty();
    }
}
