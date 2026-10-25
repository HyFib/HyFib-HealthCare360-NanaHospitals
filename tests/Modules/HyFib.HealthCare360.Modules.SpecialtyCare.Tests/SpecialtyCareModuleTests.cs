using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests;

public sealed class SpecialtyCareModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new SpecialtyCareModule().Name.ShouldBe("specialtycare");

    [Fact]
    public void Permissions_are_unique_and_scoped_to_the_five_areas()
    {
        var names = new SpecialtyCareModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => n.StartsWith("ot.", StringComparison.Ordinal) || n.StartsWith("dialysis.", StringComparison.Ordinal)
                               || n.StartsWith("physio.", StringComparison.Ordinal) || n.StartsWith("bloodbank.", StringComparison.Ordinal)
                               || n.StartsWith("mtp.", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_roles_exist_and_every_area_has_readers_and_actors()
    {
        var roles = SystemRoles.All.Select(r => r.Name).ToHashSet();
        var permissions = new SpecialtyCareModule().Permissions.ToList();

        // Certifying a termination is granted by the administrator to the gynaecologists the place's approval names (MTP Act s.5A).
        permissions.Where(p => p.Name != SpecialtyCarePermissions.MtpCertify)
            .ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0 && p.DefaultRoles.All(r => roles.Contains(r)));
        Grants(permissions, SpecialtyCarePermissions.OtSurgery).ShouldContain(SystemRoles.Doctor);
        Grants(permissions, SpecialtyCarePermissions.OtBookingsManage).ShouldContain(SystemRoles.OtCoordinator);
        Grants(permissions, SpecialtyCarePermissions.DialysisSessions).ShouldContain(SystemRoles.DialysisTechnician);
        Grants(permissions, SpecialtyCarePermissions.PhysioTreatment).ShouldContain(SystemRoles.Physiotherapist);
        Grants(permissions, SpecialtyCarePermissions.BloodBankIssue).ShouldContain(SystemRoles.BloodBankOfficer);
        Grants(permissions, SpecialtyCarePermissions.OtRead).ShouldContain(SystemRoles.HospitalAdmin);
    }

    [Fact]
    public void Donor_records_are_restricted_to_the_blood_bank() =>
        Grants(new SpecialtyCareModule().Permissions.ToList(), SpecialtyCarePermissions.BloodBankDonors).ShouldBe([SystemRoles.BloodBankOfficer]);

    [Fact]
    public void No_clinical_role_learns_of_a_termination_by_default()
    {
        var permissions = new SpecialtyCareModule().Permissions.ToList();

        Grants(permissions, SpecialtyCarePermissions.MtpCertify).ShouldBeEmpty();
        Grants(permissions, SpecialtyCarePermissions.MtpRead).ShouldBe([SystemRoles.MrdOfficer]);
        Grants(permissions, SpecialtyCarePermissions.MtpRegister).ShouldBe([SystemRoles.MrdOfficer]);
    }

    [Fact]
    public void Local_calendar_days_convert_to_ist_midnight()
    {
        var (from, to) = SpecialtyClock.RangeUtc(new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25));

        from.ShouldBe(new DateTimeOffset(2026, 9, 24, 18, 30, 0, TimeSpan.Zero));
        to.ShouldBe(new DateTimeOffset(2026, 9, 25, 18, 30, 0, TimeSpan.Zero));
        SpecialtyClock.ToLocalDate(new DateTimeOffset(2026, 9, 24, 19, 0, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 9, 25));
    }

    [Fact]
    public void Expiry_job_runs_just_after_the_next_ist_midnight()
    {
        var nineAmIst = new DateTimeOffset(2026, 9, 25, 3, 30, 0, TimeSpan.Zero);
        BloodUnitExpiryJob.DelayUntilNextRun(nineAmIst).ShouldBe(TimeSpan.FromHours(15) + TimeSpan.FromMinutes(5));

        var justBeforeRun = new DateTimeOffset(2026, 9, 24, 18, 34, 0, TimeSpan.Zero);
        BloodUnitExpiryJob.DelayUntilNextRun(justBeforeRun).ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Scheduling_lock_key_is_stable_per_resource()
    {
        var id = Guid.CreateVersion7();

        SchedulingLocks.Key(id).ShouldBe(SchedulingLocks.Key(id));
        SchedulingLocks.Key(id).ShouldNotBe(SchedulingLocks.Key(Guid.CreateVersion7()));
    }

    private static IReadOnlyCollection<string> Grants(IReadOnlyList<PermissionDefinition> permissions, string name) =>
        permissions.Single(p => p.Name == name).DefaultRoles ?? [];
}
