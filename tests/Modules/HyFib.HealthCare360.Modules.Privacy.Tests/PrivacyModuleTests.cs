using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

public sealed class PrivacyModuleTests
{
    private static PermissionDefinition Permission(string name) => new PrivacyModule().Permissions.Single(p => p.Name == name);

    [Fact]
    public void Module_uses_its_schema_name_as_key() => new PrivacyModule().Name.ShouldBe("privacy");

    [Fact]
    public void The_front_desk_can_record_requests_but_not_answer_them()
    {
        Permission(PrivacyPermissions.RequestsLog).DefaultRoles!.ShouldContain(SystemRoles.FrontOffice);
        Permission(PrivacyPermissions.RequestsManage).DefaultRoles!.ShouldNotContain(SystemRoles.FrontOffice);
    }

    [Fact]
    public void Privacy_officers_default_to_administration_quality_and_medical_records() =>
        Permission(PrivacyPermissions.RequestsManage).DefaultRoles!.ShouldBe([SystemRoles.HospitalAdmin, SystemRoles.QualityManager, SystemRoles.MrdOfficer]);

    [Fact]
    public void Retention_and_settings_are_for_hospital_administrators()
    {
        Permission(PrivacyPermissions.RetentionManage).DefaultRoles!.ShouldBe([SystemRoles.HospitalAdmin]);
        Permission(PrivacyPermissions.SettingsManage).DefaultRoles!.ShouldBe([SystemRoles.HospitalAdmin]);
    }

    [Fact]
    public void No_privacy_permission_goes_to_patients() =>
        new PrivacyModule().Permissions.ShouldAllBe(p => !p.DefaultRoles!.Contains(SystemRoles.Patient));
}

public sealed class CorrectableFieldsTests
{
    [Fact]
    public void Every_field_has_a_label() => CorrectableFields.All.ShouldAllBe(f => CorrectableFields.Label(f) != f);

    [Fact]
    public void Staff_see_the_value_on_record_with_phone_numbers_masked()
    {
        var patient = new Patient
        {
            FullName = "Asha Rao",
            Phone = "+919876543210",
            DateOfBirth = new DateOnly(1980, 3, 12),
            EmergencyContactName = "Ravi Rao",
            EmergencyContactRelation = "Husband",
            EmergencyContactPhone = "+919812345678",
        };

        CorrectableFields.CurrentValue(patient, CorrectableFields.Name).ShouldBe("Asha Rao");
        CorrectableFields.CurrentValue(patient, CorrectableFields.DateOfBirth).ShouldBe("12 Mar 1980");
        CorrectableFields.CurrentValue(patient, CorrectableFields.Mobile).ShouldNotBeNull().ShouldNotContain("98765");
        CorrectableFields.CurrentValue(patient, CorrectableFields.EmergencyContact).ShouldNotBeNull().ShouldStartWith("Ravi Rao, Husband, ");
        CorrectableFields.CurrentValue(patient, CorrectableFields.HealthRecord).ShouldBeNull();
    }
}

public sealed class ErasureTextTests
{
    [Fact]
    public void The_erasure_line_says_what_went_and_what_the_law_keeps()
    {
        var text = ErasureService.Describe(
            [ErasureService.Messages, ErasureService.PortalAccount],
            [
                new ErasureHoldItem("records", "Medical records", "", new DateOnly(2031, 3, 12), false),
                new ErasureHoldItem("finance", "Bills and payments", "", new DateOnly(2034, 9, 30), false),
            ]);

        text.ShouldBe("Erased: optional messages stopped; portal account closed. "
                      + "Kept as the law requires: medical records until 12 Mar 2031; bills and payments until 30 Sep 2034.");
    }

    [Fact]
    public void Medico_legal_records_are_kept_permanently() =>
        ErasureService.Describe([ErasureService.NomineeItem], [new ErasureHoldItem("records", "Medical records", "", null, true)])
            .ShouldEndWith("Kept as the law requires: medical records permanently.");

    [Fact]
    public void Only_the_listed_items_can_be_erased()
    {
        ErasureService.IsKnownItem(ErasureService.Feedback).ShouldBeTrue();
        ErasureService.IsKnownItem("records").ShouldBeFalse();
    }
}
