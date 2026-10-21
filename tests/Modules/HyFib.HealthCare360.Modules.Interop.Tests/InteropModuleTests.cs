using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class InteropModuleTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddAuthorization();
        new InteropModule().AddServices(services, new ConfigurationBuilder().Build());
        return services;
    }

    [Fact]
    public void Module_uses_its_schema_name_as_key() => new InteropModule().Name.ShouldBe("interop");

    [Fact]
    public void Declares_fhir_read_for_hospital_administrators_only()
    {
        var permission = new InteropModule().Permissions.Single(p => p.Name == InteropPermissions.FhirRead);

        permission.Name.ShouldBe("fhir.read");
        permission.DefaultRoles.ShouldBe([SystemRoles.HospitalAdmin]);
        permission.Group.ShouldNotBeNullOrWhiteSpace();
        permission.Description.ShouldContain("consent");
    }

    [Fact]
    public void Declares_abdm_export_for_administrators_and_medical_records()
    {
        var permission = new InteropModule().Permissions.Single(p => p.Name == InteropPermissions.AbdmExport);

        permission.Name.ShouldBe("abdm.export");
        permission.DefaultRoles.ShouldBe([SystemRoles.HospitalAdmin, SystemRoles.MrdOfficer]);
        permission.Group.ShouldBe("Interoperability");
        permission.Description.ShouldContain("consent");
        permission.Description.ShouldContain("audited");
        new InteropModule().Permissions.Select(p => p.Name).ShouldBe([InteropPermissions.FhirRead, InteropPermissions.AbdmExport]);
    }

    [Fact]
    public void Default_roles_are_built_in_roles_and_never_patients()
    {
        var builtIn = SystemRoles.All.Select(r => r.Name).ToHashSet();

        foreach (var permission in new InteropModule().Permissions)
        {
            permission.DefaultRoles.ShouldNotBeNull();
            permission.DefaultRoles.ShouldNotBeEmpty();
            permission.DefaultRoles.ShouldAllBe(role => builtIn.Contains(role));
            permission.DefaultRoles.ShouldNotContain(SystemRoles.Patient);
        }
    }

    [Theory]
    [InlineData(typeof(FhirApi))]
    [InlineData(typeof(PatientRecords))]
    [InlineData(typeof(PatientSharingService))]
    [InlineData(typeof(FhirAuditTrail))]
    [InlineData(typeof(ReferenceDirectoryLoader))]
    [InlineData(typeof(AbdmDocumentService))]
    [InlineData(typeof(AbdmAuditTrail))]
    public void Registers_its_services_once(Type service) => Services().Count(d => d.ServiceType == service).ShouldBe(1);

    [Fact]
    public void Registers_one_provider_per_clinical_resource_type()
    {
        var providers = Services().Where(d => d.ServiceType == typeof(IFhirResourceProvider)).Select(d => d.ImplementationType!.Name).ToList();

        providers.Count.ShouldBe(7);
        providers.Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public void Decorates_the_authorization_result_handler_already_registered()
    {
        using var provider = Services().BuildServiceProvider();

        var handler = provider.GetRequiredService<IAuthorizationMiddlewareResultHandler>().ShouldBeOfType<FhirAuthorizationResultHandler>();

        handler.Inner.ShouldBeOfType<AuthorizationMiddlewareResultHandler>();
    }
}
