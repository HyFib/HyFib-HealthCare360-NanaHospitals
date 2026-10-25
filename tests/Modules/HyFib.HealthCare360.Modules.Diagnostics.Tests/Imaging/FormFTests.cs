using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>PCPNDT Form F: what it needs before the ultrasound, what the doctor adds after it, and that it then cannot change.</summary>
public sealed class FormFTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 5, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void The_woman_s_details_need_the_period_of_pregnancy_the_referral_and_an_indication()
    {
        var form = new FormF();

        form.Declare(Declaration() with { LastMenstrualPeriod = null, WeeksOfPregnancy = null }, null, Now).Error!.Fields!.Keys.ShouldBe(["lastMenstrualPeriod"]);
        form.Declare(Declaration() with { WeeksOfPregnancy = 60 }, null, Now).Error!.Fields!.Keys.ShouldBe(["weeksOfPregnancy"]);
        form.Declare(Declaration() with { ReferringDoctorAddress = " " }, null, Now).Error!.Fields!.Keys.ShouldBe(["referringDoctorName"]);
        form.Declare(Declaration() with { Indications = [] }, null, Now).Error!.Fields!.Keys.ShouldBe(["indications"]);
        form.Status.ShouldBe((FormFStatus)0, "nothing was recorded");

        // A self-referral by the doctor who scans names no referring doctor.
        var self = form.Declare(Declaration() with { Referral = FormFReferral.Self, ReferringDoctorName = null, ReferringDoctorAddress = null }, null, Now);
        self.IsSuccess.ShouldBeTrue();
        (form.Status, form.Referral, form.ReferringDoctorName).ShouldBe((FormFStatus.Declared, FormFReferral.Self, (string?)null));
    }

    [Fact]
    public void The_doctor_completes_it_once_with_the_result_and_then_it_cannot_change()
    {
        var form = new FormF();
        form.Declare(Declaration() with { Indications = [FormFIndication.GestationalAge, FormFIndication.GestationalAge, FormFIndication.IntraUterineOrEctopicPregnancy] },
            Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
        form.Indications.ShouldBe([FormFIndication.IntraUterineOrEctopicPregnancy, FormFIndication.GestationalAge], "once each, in the form's order");

        var doctor = Guid.NewGuid();
        form.Complete(Result() with { ConveyedOn = Today.AddDays(-1) }, doctor, "Dr. Meera", Today, Now).Error!.Fields!.Keys.ShouldBe(["conveyedOn"]);
        form.Complete(Result() with { MtpIndicated = true, MtpIndication = null }, doctor, "Dr. Meera", Today, Now).Error!.Fields!.Keys.ShouldBe(["mtpIndication"]);

        form.Complete(Result(), doctor, " Dr. Meera ", Today, Now).IsSuccess.ShouldBeTrue();
        (form.Status, form.DoctorUserId, form.DoctorName, form.ProcedureDate, form.CompletedAt).ShouldBe((FormFStatus.Completed, (Guid?)doctor, "Dr. Meera", (DateOnly?)Today, (DateTimeOffset?)Now));

        form.Declare(Declaration(), null, Now).Error!.Code.ShouldBe("form_f.completed");
        form.Complete(Result(), doctor, "Dr. Meera", Today, Now).Error!.Code.ShouldBe("form_f.completed");
    }

    [Fact]
    public void Every_indication_prints_as_the_form_words_it_and_obstetric_scans_ask_for_form_f()
    {
        Enum.GetValues<FormFIndication>().ShouldAllBe(i => FormFDocument.Indication(i) != i.ToString());
        Enum.GetValues<FormFIndication>().Length.ShouldBe(23, "Form F Section B lists 23 indications for an ultrasound");

        var obstetric = ImagingCatalog.Procedures.Where(p => p.RequiresFormF).ToList();
        obstetric.Select(p => p.Code).ShouldBe(["RAD-USG-OBS", "RAD-USG-ANOMALY"], ignoreOrder: true);
        obstetric.ShouldAllBe(p => p.Modality == Modality.US);
    }

    private static FormFDeclaration Declaration() => new(
        26, "Ravi Kumar", "4, Main Road, Salem", "9876543210", 1, 0, null, 12, FormFReferral.Doctor, "Dr. Lata", "Lata Clinic, Salem",
        [FormFIndication.GestationalAge], null, null);

    private static FormFResult Result() => new("Single live intrauterine foetus, 12 weeks.", "The woman", Today, false, null);
}
