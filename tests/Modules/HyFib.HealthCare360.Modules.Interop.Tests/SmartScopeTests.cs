using HyFib.HealthCare360.Modules.Interop.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>SMART App Launch 2.0 scopes: parsed strictly and narrowed to what this read-only server grants.</summary>
public sealed class SmartScopeTests
{
    [Theory]
    [InlineData("patient/*.rs", SmartContext.Patient, "*", true, true)]
    [InlineData("patient/*.read", SmartContext.Patient, "*", true, true)]
    [InlineData("user/Observation.r", SmartContext.User, "Observation", true, false)]
    [InlineData("system/Patient.s", SmartContext.System, "Patient", false, true)]
    [InlineData("system/*.cruds", SmartContext.System, "*", true, true)]
    public void Read_and_search_are_granted(string value, SmartContext context, string type, bool read, bool search)
    {
        SmartScopes.TryParse(value, out var scope).ShouldBeTrue();
        scope.ShouldBe(new SmartScope(context, type, read, search));
    }

    [Theory]
    [InlineData("patient/*.write")]
    [InlineData("patient/*.*")]
    [InlineData("patient/*.cud")]
    [InlineData("patient/*.sr")]
    [InlineData("patient/*.rr")]
    [InlineData("patient/Observation.rs?category=laboratory")]
    [InlineData("patient/Claim.rs")]
    [InlineData("group/*.rs")]
    [InlineData("patient/.rs")]
    [InlineData("openid")]
    public void Anything_else_is_not(string value) => SmartScopes.TryParse(value, out _).ShouldBeFalse();

    [Fact]
    public void A_request_is_narrowed_to_the_contexts_and_the_registered_scopes()
    {
        var registered = SmartScopes.ParseStored("patient/*.rs");
        var grant = SmartScopes.Narrow("launch/patient patient/Observation.rs patient/Patient.cud user/*.rs openid", [SmartContext.Patient, SmartContext.User], registered);

        grant.LaunchPatient.ShouldBeTrue();
        grant.Scopes.ShouldBe([new SmartScope(SmartContext.Patient, "Observation", true, true)]);
        SmartScopes.Format(grant.Scopes, grant.LaunchPatient).ShouldBe("launch/patient patient/Observation.rs");

        // A registered read-only scope does not grant search.
        SmartScopes.Narrow("system/Patient.rs", [SmartContext.System], SmartScopes.ParseStored("system/Patient.r")).Scopes.ShouldBeEmpty();
        SmartScopes.Narrow("system/*.rs", [SmartContext.Patient], SmartScopes.ParseStored("system/*.rs")).Scopes.ShouldBeEmpty();
    }

    [Fact]
    public void A_patient_scope_reaches_only_the_chosen_patient()
    {
        var chosen = Guid.NewGuid();
        var access = new SmartAccess(Guid.NewGuid(), SmartScopes.ParseStored("patient/*.rs"), chosen);

        access.Allows("Observation", search: true, chosen).ShouldBeTrue();
        access.Allows("Observation", search: true, Guid.NewGuid()).ShouldBeFalse();
        access.Allows("Observation", search: true, null).ShouldBeTrue("the type, before the patient is known");
        access.LimitedToPatient("Observation", search: true).ShouldBeTrue();

        var wider = access with { Scopes = SmartScopes.ParseStored("patient/*.rs user/Observation.rs") };
        wider.Allows("Observation", search: true, Guid.NewGuid()).ShouldBeTrue("a user/ scope reads what the account may");
        wider.LimitedToPatient("Observation", search: true).ShouldBeFalse();
        wider.LimitedToPatient("Condition", search: true).ShouldBeTrue();

        var readOnly = new SmartAccess(Guid.NewGuid(), SmartScopes.ParseStored("system/Patient.r"), null);
        readOnly.Allows("Patient", search: false, null).ShouldBeTrue();
        readOnly.Allows("Patient", search: true, null).ShouldBeFalse();
        readOnly.Allows("Observation", search: false, null).ShouldBeFalse();
    }
}
