using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Searches take a term's <c>%</c>, <c>_</c> and <c>\</c> as written. Npgsql writes <c>EF.Functions.ILike(column,
/// pattern)</c> as <c>ILIKE … ESCAPE ''</c>, so the backslashes <c>ToLikePattern</c> puts before them were matched as
/// themselves, and a search for a user name such as <c>dr_meera</c> or a name with <c>50%</c> in it found nothing.
/// </summary>
public sealed class LikeSearchTests(HospitalServer server)
{
    [Fact]
    public async Task A_user_name_with_an_underscore_is_found_and_the_underscore_matches_only_itself()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == SystemRoles.FrontOffice).Id;
        var suffix = Letters(6);
        var withUnderscore = $"fo_{suffix}";
        var withLetter = $"fox{suffix}";
        await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", NewUser(withUnderscore, frontOffice));
        await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", NewUser(withLetter, frontOffice));

        var found = await SearchUsersAsync(admin, withUnderscore);

        found.ShouldBe([withUnderscore], "an underscore is a character to find, not LIKE's any-one-character");
        (await SearchUsersAsync(admin, $"o_{suffix}")).ShouldBe([withUnderscore]);
        (await SearchUsersAsync(admin, $"ox{suffix}")).ShouldBe([withLetter]);
    }

    [Fact]
    public async Task A_name_with_a_percent_sign_or_a_backslash_is_found_by_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var suffix = Letters(6);
        await admin.PostAsync<Created>("/api/v1/masters/departments", Department($"P{suffix[..4].ToUpperInvariant()}", $"Day care 50% wing {suffix}"));
        await admin.PostAsync<Created>("/api/v1/masters/departments", Department($"Q{suffix[..4].ToUpperInvariant()}", $"Day care 500 wing {suffix}"));
        await admin.PostAsync<Created>("/api/v1/masters/departments", Department($"R{suffix[..4].ToUpperInvariant()}", $"Records\\archive {suffix}"));

        (await SearchDepartmentsAsync(admin, $"50% wing {suffix}")).ShouldBe([$"Day care 50% wing {suffix}"], "a percent sign is not LIKE's any-run-of-characters");
        (await SearchDepartmentsAsync(admin, $"0 wing {suffix}")).ShouldBe([$"Day care 500 wing {suffix}"]);
        (await SearchDepartmentsAsync(admin, $"Records\\archive {suffix}")).ShouldBe([$"Records\\archive {suffix}"]);
    }

    private static async Task<List<string>> SearchUsersAsync(ApiClient admin, string term) =>
        [.. (await admin.GetAsync<PagedResult<UserListItem>>($"/api/v1/admin/users?search={Uri.EscapeDataString(term)}&pageSize=50")).Items.Select(u => u.UserName)];

    private static async Task<List<string>> SearchDepartmentsAsync(ApiClient admin, string term) =>
        [.. (await admin.GetAsync<List<DepartmentDto>>($"/api/v1/masters/departments?search={Uri.EscapeDataString(term)}")).Select(d => d.Name)];

    private static CreateUserRequest NewUser(string userName, Guid roleId) =>
        new(userName, userName, Email: null, Phone: null, [roleId], FacilityIds: [], DefaultFacilityId: null, PractitionerId: null, TemporaryPassword: null);

    private static SaveDepartmentRequest Department(string code, string name) =>
        new(code, name, DepartmentKind.Clinical, null, null, null, null, null, 900, true, null);

    private sealed record Created(Guid Id);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
