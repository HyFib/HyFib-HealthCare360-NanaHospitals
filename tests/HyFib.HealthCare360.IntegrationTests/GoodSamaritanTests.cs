using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A Good Samaritan who brings an injured person to the emergency department is not compelled to give their name, address
/// or other personal details, for the medico-legal case form either (Motor Vehicles Act 1988, s.134A; Central Motor
/// Vehicles Rules 1989, rule 168). The medico-legal case is entered without them; a relative, the police or an ambulance
/// crew are still named.
/// </summary>
public sealed class GoodSamaritanTests(HospitalServer server)
{
    [Fact]
    public async Task A_case_brought_in_by_a_member_of_the_public_is_entered_without_their_name()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (_, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var caseId = (await doctor.GetAsync<ErBoard>("/api/v1/emergency/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;
        var path = $"/api/v1/emergency/cases/{caseId}/mlc";

        // A relative is still named.
        using (var unnamed = await doctor.SendRawAsync(HttpMethod.Put, path, Mlc(MlcBroughtBy.Relative)))
        {
            unnamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // The passer-by who brought the patient in gives no name, and the case is entered.
        var entered = await doctor.PutAsync<EmergencyCaseDetail>(path, Mlc(MlcBroughtBy.Public));
        var mlc = entered.Mlc.ShouldNotBeNull();
        (mlc.BroughtBy, mlc.BroughtByName).ShouldBe((MlcBroughtBy.Public, (string?)null));
    }

    private static SaveMlcRequest Mlc(MlcBroughtBy broughtBy) => new(
        broughtBy, null, MlcCause.RoadTrafficAccident, DateTimeOffset.UtcNow.AddHours(-1), "OMR, Thoraipakkam", "Abrasion over the right knee, simple",
        true, false, "Adyar", "TNMC 45678", null);
}
