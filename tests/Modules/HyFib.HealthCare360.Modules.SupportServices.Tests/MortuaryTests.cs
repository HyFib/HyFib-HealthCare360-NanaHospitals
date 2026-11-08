using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>A body in the mortuary: unclaimed after 72 hours, and released only on the authority its case needs.</summary>
public sealed class MortuaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static BodyRecord Body(bool mlc = false, bool identified = true)
    {
        var body = new BodyRecord { Number = "NHMB26-00001", Name = "Ravi Kumar", Identified = identified, IsMlc = mlc, ReceivedAt = Now.AddHours(-5) };
        body.Store(new MortuarySlot { Id = Guid.NewGuid(), Code = "A-1" });
        return body;
    }

    private static BodyRelease To(ReleaseTo to, string? relationship = null, string? idProof = null, string? clearance = null) =>
        new(to, "Suresh Kumar", relationship, idProof, clearance, true, null);

    private static readonly Guid Nurse = Guid.NewGuid();
    private static readonly BodyReleaseWitness Witness = new(Guid.NewGuid(), "Sister Mary");

    [Fact]
    public void A_release_on_the_polices_clearance_is_witnessed_by_someone_else()
    {
        // Maker-checker, slice 9: a medico-legal body to the relatives, or an unclaimed body to the municipality.
        var mlc = Body(mlc: true);
        mlc.NeedsPoliceClearance(ReleaseTo.Relatives).ShouldBeTrue();
        mlc.Release(To(ReleaseTo.Relatives, "Son", "Aadhaar", "PM 123/2026"), Nurse, "Nurse", null, Now).Error!.Code.ShouldBe("mortuary.witness_required");
        mlc.Release(To(ReleaseTo.Relatives, "Son", "Aadhaar", "PM 123/2026"), Nurse, "Nurse", new BodyReleaseWitness(Nurse, "Nurse"), Now).Error!.Code
            .ShouldBe("mortuary.witness_is_releaser");
        mlc.Status.ShouldBe(BodyStatus.InStorage);
        mlc.Release(To(ReleaseTo.Relatives, "Son", "Aadhaar", "PM 123/2026"), Nurse, "Nurse", Witness, Now).IsSuccess.ShouldBeTrue();
        (mlc.ReleasedByUserId, mlc.ReleaseWitnessUserId, mlc.ReleaseWitnessName).ShouldBe(((Guid?)Nurse, (Guid?)Witness.UserId, "Sister Mary"));

        Body(identified: false).Release(To(ReleaseTo.Municipality, clearance: "Letter 88/2026"), Nurse, "Nurse", null, Now).Error!.Code
            .ShouldBe("mortuary.witness_required");

        // A release that needs no clearance needs no witness.
        Body().NeedsPoliceClearance(ReleaseTo.Relatives).ShouldBeFalse();
        Body(mlc: true).NeedsPoliceClearance(ReleaseTo.PoliceForPostMortem).ShouldBeFalse();
    }

    [Fact]
    public void A_body_kept_beyond_72_hours_is_unclaimed_until_released()
    {
        var body = Body();
        body.ReceivedAt = Now.AddHours(-73);
        body.Unclaimed(Now).ShouldBeTrue();
        body.ReceivedAt = Now.AddHours(-71);
        body.Unclaimed(Now).ShouldBeFalse();
    }

    [Fact]
    public void Relatives_take_a_body_with_their_relationship_and_identity_proof()
    {
        Body().Release(To(ReleaseTo.Relatives), Nurse, "Nurse", null, Now).Error!.Fields!.Keys.ShouldContain("idProof");

        var body = Body();
        body.Release(To(ReleaseTo.Relatives, "Son", "Aadhaar XXXX-1234"), Nurse, "Nurse", null, Now).IsSuccess.ShouldBeTrue();
        body.Status.ShouldBe(BodyStatus.Released);
        body.SlotId.ShouldBeNull();
        body.SlotCode.ShouldBe("A-1");
        body.ReleasedTo.ShouldBe(ReleaseTo.Relatives);
    }

    [Fact]
    public void A_medico_legal_body_goes_to_relatives_only_on_police_clearance()
    {
        Body(mlc: true).Release(To(ReleaseTo.Relatives, "Son", "Aadhaar"), Nurse, "Nurse", null, Now).Error!.Fields!.Keys.ShouldContain("policeClearance");
        Body(mlc: true).Release(To(ReleaseTo.Relatives, "Son", "Aadhaar", "PM 123/2026"), Nurse, "Nurse", Witness, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_unidentified_body_does_not_go_to_relatives()
    {
        Body(identified: false).Release(To(ReleaseTo.Relatives, "Son", "Aadhaar"), Nurse, "Nurse", null, Now).Error!.Code.ShouldBe("mortuary.unidentified");
    }

    [Fact]
    public void The_police_take_a_body_on_the_officers_details()
    {
        Body(mlc: true).Release(To(ReleaseTo.PoliceForPostMortem), Nurse, "Nurse", null, Now).Error!.Fields!.Keys.ShouldContain("idProof");
        Body(mlc: true).Release(To(ReleaseTo.PoliceForPostMortem, idProof: "SI Kannan, 4521, T Nagar"), Nurse, "Nurse", null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_unclaimed_body_goes_to_the_municipality_on_police_clearance()
    {
        Body(identified: false).Release(To(ReleaseTo.Municipality), Nurse, "Nurse", null, Now).Error!.Fields!.Keys.ShouldContain("policeClearance");
        Body(identified: false).Release(To(ReleaseTo.Municipality, clearance: "Letter 88/2026"), Nurse, "Nurse", Witness, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_medico_legal_body_cannot_be_donated()
    {
        Body(mlc: true).Release(To(ReleaseTo.BodyDonation), Nurse, "Nurse", null, Now).Error!.Code.ShouldBe("mortuary.mlc_donation");
        Body().Release(To(ReleaseTo.BodyDonation), Nurse, "Nurse", null, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_body_is_released_once()
    {
        var body = Body();
        body.Release(To(ReleaseTo.BodyDonation), Nurse, "Nurse", null, Now).IsSuccess.ShouldBeTrue();
        body.Release(To(ReleaseTo.BodyDonation), Nurse, "Nurse", null, Now).Error!.Code.ShouldBe("mortuary.released");
    }
}
