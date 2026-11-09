using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The emergency department's bays and the wound certificate, as pure rules: a bay holds a patient until they move or
/// leave; the certificate's injuries name their nature under the Bharatiya Nyaya Sanhita 2023, and once signed it does
/// not change.
/// </summary>
public sealed class EmergencyMedicoLegalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Facility = Guid.CreateVersion7();

    [Fact]
    public void A_patient_is_put_in_a_bay_moved_and_the_bay_freed_when_they_leave()
    {
        var c = new EmergencyCase { FacilityId = Facility };
        var a1 = new ErBay { FacilityId = Facility, Code = "A1" };
        var a2 = new ErBay { FacilityId = Facility, Code = "A2" };

        c.PutInBay(a1, Now).IsSuccess.ShouldBeTrue();
        (c.BayId, c.BayAssignedAt).ShouldBe((a1.Id, Now));
        c.PutInBay(a2, Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        c.BayId.ShouldBe(a2.Id);
        c.PutInBay(new ErBay { FacilityId = Facility, Code = "X", IsActive = false }, Now).Error!.Code.ShouldBe("er.bay_inactive");
        c.PutInBay(new ErBay { FacilityId = Guid.CreateVersion7(), Code = "Z" }, Now).IsFailure.ShouldBeTrue("another hospital's bay");

        c.Dispose(EmergencyDisposition.Died, "Declared dead at 10:40", null, "Dr A", Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        c.BayId.ShouldBeNull("leaving frees the bay");
        c.PutInBay(a1, Now.AddHours(2)).Error!.Code.ShouldBe("er.closed");
    }

    [Fact]
    public void A_bay_is_released_only_when_the_patient_is_in_one()
    {
        var c = new EmergencyCase { FacilityId = Facility };
        c.ReleaseBay().Error!.Code.ShouldBe("er.no_bay");
        c.PutInBay(new ErBay { FacilityId = Facility, Code = "M1" }, Now);
        c.ReleaseBay().IsSuccess.ShouldBeTrue();
        c.BayId.ShouldBeNull();
    }

    [Fact]
    public void A_grievous_hurt_names_its_clause_a_reserved_opinion_what_it_waits_for_and_a_fracture_is_never_simple()
    {
        WoundCertificate.Check([Injury(1, InjuryKind.Laceration, InjuryNature.Simple)]).IsSuccess.ShouldBeTrue();
        WoundCertificate.Check([]).IsFailure.ShouldBeTrue();
        WoundCertificate.Check([Injury(1, InjuryKind.Laceration, InjuryNature.Simple), Injury(3, InjuryKind.Abrasion, InjuryNature.Simple)])
            .Error!.Message.ShouldContain("without gaps");
        WoundCertificate.Check([Injury(1, InjuryKind.StabWound, InjuryNature.Grievous)]).Error!.Message.ShouldContain("s.116");
        WoundCertificate.Check([Injury(1, InjuryKind.StabWound, InjuryNature.Grievous) with { Clause = GrievousClause.EndangersLifeOrFifteenDays }]).IsSuccess.ShouldBeTrue();
        WoundCertificate.Check([Injury(1, InjuryKind.Contusion, InjuryNature.Reserved)]).Error!.Message.ShouldContain("waits for");
        WoundCertificate.Check([Injury(1, InjuryKind.Fracture, InjuryNature.Simple)]).Error!.Message.ShouldContain("s.116(g)");
    }

    [Fact]
    public void The_nature_is_described_as_the_law_has_it()
    {
        WoundCertificate.Describe(Injury(1, InjuryKind.Fracture, InjuryNature.Grievous) with { Clause = GrievousClause.FractureOrDislocation })
            .ShouldBe("Grievous (BNS s.116(g): fracture or dislocation of a bone or tooth)");
        WoundCertificate.Describe(Injury(1, InjuryKind.Contusion, InjuryNature.Reserved) with { ReservedFor = "X-ray of the skull" })
            .ShouldBe("Opinion reserved pending X-ray of the skull");
        WoundCertificate.ClauseLetter(GrievousClause.Emasculation).ShouldBe("(a)");
        WoundCertificate.ClauseLetter(GrievousClause.EndangersLifeOrFifteenDays).ShouldBe("(h)");
    }

    [Fact]
    public void A_signed_certificate_does_not_change()
    {
        var w = new WoundCertificate();
        w.Sign(Guid.CreateVersion7(), Guid.CreateVersion7(), "Dr A", null, Now).Error!.Code.ShouldBe("wound_certificate.no_injuries");
        w.Write(Now.AddHours(-1), "A mole on the left cheek; a scar on the right knee", null, [Injury(1, InjuryKind.Laceration, InjuryNature.Simple)], null, Now)
            .IsSuccess.ShouldBeTrue();
        w.Sign(Guid.CreateVersion7(), Guid.CreateVersion7(), "Dr A", "TNMC 1", Now).IsSuccess.ShouldBeTrue();
        w.Write(Now, "x", null, [Injury(1, InjuryKind.Abrasion, InjuryNature.Simple)], null, Now).Error!.Code.ShouldBe("wound_certificate.signed");
        w.Injuries.ShouldHaveSingleItem().Kind.ShouldBe(InjuryKind.Laceration);
    }

    [Fact]
    public void A_confidential_patient_keeps_their_name_on_the_worklist_but_not_the_clinical_text()
    {
        var patient = Guid.CreateVersion7();
        var worklist = new EmrWorklist(DateOnly.FromDateTime(Now.DateTime), null, null, [], [],
            [new WorklistEncounter(Guid.CreateVersion7(), patient, "NH1", "Asha Rao", Domain.EncounterType.OutPatient, Now, "Low mood")],
            [], [],
            [new WorklistResult(Guid.CreateVersion7(), Guid.CreateVersion7(), Platform.Contracts.DiagnosticKind.Laboratory, "HIV-1/2 antibody", true, Now, patient, "NH1", "Asha Rao")],
            []);

        var masked = WorklistRestriction.Mask(worklist, new Dictionary<Guid, ChartWithholding> { [patient] = ChartWithholding.Confidential });

        masked.OpenEncounters.Single().ShouldSatisfyAllConditions(e => e.PatientName.ShouldBe("Asha Rao"), e => e.ChiefComplaint.ShouldBeNull());
        masked.ResultsToReview.Single().ShouldSatisfyAllConditions(
            r => r.PatientName.ShouldBe("Asha Rao"), r => r.Title.ShouldBe("Confidential record"), r => r.HasCriticalFindings.ShouldBeTrue());
    }

    private static WoundInjury Injury(int number, InjuryKind kind, InjuryNature nature) =>
        new(number, kind, "Left forearm, 5 cm above the wrist", "4 x 1 x 0.5 cm", "Margins irregular, bleeding", InjuryWeapon.Blunt, "Fresh, within 6 hours",
            nature, null, null);
}
