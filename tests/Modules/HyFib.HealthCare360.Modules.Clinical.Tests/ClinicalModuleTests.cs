using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class ClinicalModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new ClinicalModule().Name.ShouldBe("clinical");

    [Fact]
    public void Module_declares_its_permissions() => new ClinicalModule().Permissions.ShouldBe(ClinicalPermissions.All);

    [Fact]
    public void Permission_names_are_unique_and_prefixed()
    {
        var names = ClinicalPermissions.All.Select(p => p.Name).ToList();

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Count);
        names.ShouldAllBe(n => n.StartsWith("clinical.", StringComparison.Ordinal) || n.StartsWith("mrd.", StringComparison.Ordinal)
                               || n.StartsWith("healthcheck.", StringComparison.Ordinal));
        names.ShouldContain(ClinicalPermissions.EmrRead);
        names.ShouldContain(ClinicalPermissions.PrescriptionsSign);
        names.ShouldContain(ClinicalPermissions.MrdCoding);
        names.ShouldContain(ClinicalPermissions.HealthCheckManage);
    }

    [Fact]
    public void Every_permission_has_default_roles_and_a_description() =>
        ClinicalPermissions.All.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0 && p.Description.Length > 0);

    [Fact]
    public void Nurses_can_read_record_vitals_and_allergies_but_not_prescribe()
    {
        string[] nurse = [.. ClinicalPermissions.All.Where(p => p.DefaultRoles!.Contains(SystemRoles.Nurse)).Select(p => p.Name)];

        nurse.ShouldContain(ClinicalPermissions.EmrRead);
        nurse.ShouldContain(ClinicalPermissions.VitalsRecord);
        nurse.ShouldContain(ClinicalPermissions.AllergiesManage);
        nurse.ShouldNotContain(ClinicalPermissions.PrescriptionsSign);
        nurse.ShouldNotContain(ClinicalPermissions.OrdersPlace);
    }

    [Theory]
    [InlineData(SystemRoles.LabTechnician)]
    [InlineData(SystemRoles.Pharmacist)]
    [InlineData(SystemRoles.Radiographer)]
    [InlineData(SystemRoles.OtCoordinator)]
    [InlineData(SystemRoles.DialysisTechnician)]
    [InlineData(SystemRoles.Physiotherapist)]
    [InlineData(SystemRoles.BloodBankOfficer)]
    [InlineData(SystemRoles.Dietitian)]
    public void Staff_who_treat_or_test_the_patient_see_the_banner_flags_without_the_chart(string role)
    {
        string[] granted = [.. ClinicalPermissions.All.Where(p => p.DefaultRoles!.Contains(role)).Select(p => p.Name)];

        granted.ShouldContain(ClinicalPermissions.FlagsRead);
        granted.ShouldNotContain(ClinicalPermissions.EmrRead);
    }

    [Theory]
    [InlineData(SystemRoles.FrontOffice)]
    [InlineData(SystemRoles.Billing)]
    [InlineData(SystemRoles.Cashier)]
    [InlineData(SystemRoles.InsuranceDesk)]
    [InlineData(SystemRoles.CrmExecutive)]
    public void Desk_and_billing_staff_do_not_see_clinical_flags_by_default(string role) =>
        ClinicalPermissions.All.Single(p => p.Name == ClinicalPermissions.FlagsRead).DefaultRoles!.ShouldNotContain(role);

    [Fact]
    public void Coding_belongs_to_the_mrd_officer() =>
        ClinicalPermissions.All.Single(p => p.Name == ClinicalPermissions.MrdCoding).DefaultRoles!.ShouldBe([SystemRoles.MrdOfficer]);

    [Fact]
    public void Importing_a_catalogue_belongs_to_administrators_not_coders()
    {
        var import = ClinicalPermissions.All.Single(p => p.Name == ClinicalPermissions.CatalogueImport);

        import.Name.ShouldBe("mrd.catalogue.import");
        import.DefaultRoles!.ShouldBe([SystemRoles.HospitalAdmin]);
    }
}
