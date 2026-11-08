using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Nobody takes over a colleague's account alone (MAKER-CHECKER.md, slice 4). One administrator could change a nurse's
/// e-mail address, reset her password and be shown it, reset her two-step verification, and sign in as her; link their own
/// account to a consultant's practitioner record and sign as him; or enrol any PC as an administration desktop. Now a
/// temporary password goes to the account's own mobile number, never to the administrator; an account with no mobile number
/// or e-mail address waits for a second administrator, who hands it over; and a two-step verification reset, another user's
/// contact details, a practitioner link, a linked doctor's registration and another administration desktop wait for a second
/// administrator. Here the hospital administrator asks and the Super Administrator approves, as in the production-style run.
/// </summary>
public sealed class AccountAdministrationApprovalTests(HospitalServer server)
{
    private const string Users = "/api/v1/admin/users";
    private const string Practitioners = "/api/v1/masters/practitioners";
    private const string EnrollmentCodes = "/api/v1/admin/devices/enrollment-codes";

    private static int _addresses;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_password_reset_sends_the_temporary_password_to_the_nurses_own_mobile_and_never_to_the_administrator()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var mobile = NewMobile();
        var name = "it-reset-sms-" + Letters(6);
        var created = await CreateUserAsync(admin, name, email: null, phone: mobile);

        using var response = await admin.SendRawAsync(HttpMethod.Post, $"{Users}/{created.UserId}/reset-password");
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var reset = JsonSerializer.Deserialize<PasswordResetResponse>(body, ApiClient.Json)!;
        reset.TemporaryPassword.ShouldBeNull();
        reset.SentTo.ShouldBe([$"mobile {Phone.Mask(Phone.Normalize(mobile))}"]);

        // It went to her mobile by SMS, and only there: the administrator's answer does not hold it.
        var password = await server.TemporaryPasswordSentAsync(created.UserId);
        body.ShouldNotContain(password);
        (await SmsAsync("auth.temporary_password", Phone.Normalize(mobile)!)).Count.ShouldBe(1);
        using var signedIn = await LogInAsync(name, password);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await signedIn.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.Restriction.ShouldBe(AuthService.RestrictionPasswordChange);
    }

    [Fact]
    public async Task A_password_reset_for_an_account_with_no_contact_waits_and_is_handed_to_the_approver_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var name = "it-reset-handover-" + Letters(6);
        var created = await CreateUserAsync(admin, name, email: null, phone: null);

        using var response = await admin.SendRawAsync(HttpMethod.Post, $"{Users}/{created.UserId}/reset-password");
        var body = await response.Content.ReadAsStringAsync(Ct);

        // Nowhere to send it: it waits, and nothing changes; the password she was given still works.
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        var asked = JsonSerializer.Deserialize<ApprovalRequestDto>(body, ApiClient.Json)!;
        (asked.Kind, asked.Status, asked.SubjectId).ShouldBe((AccountApprovals.PasswordResetKind, ApprovalStatus.Pending, created.UserId));
        using (var still = await LogInAsync(name, created.TemporaryPassword))
        {
            still.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Never by the one who asked; the Super Administrator approves, and is shown it once, to hand over.
        (await RefusalAsync(admin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        var approved = await superAdmin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Handed over at the nursing station.", asked.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved);
        var password = approved.Handover.ShouldNotBeNull().Secret;
        approved.Outcome.ShouldNotBeNull().ShouldNotContain(password);
        (await superAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Handover.ShouldBeNull();
        (await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Handover.ShouldBeNull();

        using (var old = await LogInAsync(name, created.TemporaryPassword))
        {
            old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var signedIn = await LogInAsync(name, password);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuditAsync(created.UserId)).ShouldContain(e => e.Action == "user.password_reset"
            && e.Summary != null && e.Summary.Contains("asked for by", StringComparison.Ordinal) && e.Summary.Contains("approved by", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_two_step_verification_reset_waits_and_changes_nothing_until_approved()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var name = "it-mfa-reset-" + Letters(6);
        await server.CreateStaffAccountAsync(name, SystemRoles.Nurse);
        var (session, _) = await server.SignInNewSessionAsync(name);
        var userId = await TurnOnTwoStepVerificationAsync(name);

        using var response = await admin.SendRawAsync(HttpMethod.Post, $"{Users}/{userId}/reset-mfa");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        asked.Kind.ShouldBe(AccountApprovals.MfaResetKind);
        (await UserAsync(userId)).MfaEnabled.ShouldBeTrue();
        using (var stillSignedIn = await session.SendRawAsync(HttpMethod.Get, "/api/v1/auth/me"))
        {
            stillSignedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await NoticesAsync(userId)).ShouldNotContain(CredentialNotices.MfaReset);

        (await RefusalAsync(admin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await superAdmin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Phone lost on the ward.", asked.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);

        var user = await UserAsync(userId);
        (user.MfaEnabled, user.MfaSecret).ShouldBe((false, (string?)null));
        using (var ended = await session.SendRawAsync(HttpMethod.Get, "/api/v1/auth/me"))
        {
            ended.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await NoticesAsync(userId)).ShouldContain(CredentialNotices.MfaReset);
        (await AuditAsync(userId)).ShouldContain(e => e.Action == "user.mfa_reset" && e.Summary != null && e.Summary.Contains("approved by", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_users_e_mail_change_waits_and_the_old_address_is_told_once_it_changes()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var oldEmail = $"it-old-{Letters(8)}@example.test";
        var newEmail = $"it-new-{Letters(8)}@example.test";
        var created = await CreateUserAsync(admin, "it-contact-" + Letters(6), oldEmail, phone: null);
        var user = await admin.GetAsync<UserDetail>($"{Users}/{created.UserId}");

        using var response = await admin.SendRawAsync(HttpMethod.Put, $"{Users}/{user.Id}", Update(user) with { Email = newEmail, Reason = "Moved to the hospital's own mail." });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        asked.Kind.ShouldBe(AccountApprovals.ContactChangeKind);
        asked.Details.ShouldContain(new ApprovalDetail("E-mail address", $"{oldEmail} → {newEmail}"));
        var waiting = await admin.GetAsync<UserDetail>($"{Users}/{user.Id}");
        waiting.Email.ShouldBe(oldEmail);
        waiting.PendingChanges.ShouldNotBeNull().ShouldContain(c => c.ApprovalId == asked.Id);
        (await EmailsAsync("auth.contact_changed", oldEmail)).ShouldBeEmpty();

        await server.ApproveGrantAsync(asked.Id);

        (await admin.GetAsync<UserDetail>($"{Users}/{user.Id}")).Email.ShouldBe(newEmail);
        var told = (await EmailsAsync("auth.contact_changed", oldEmail)).ShouldHaveSingleItem();
        told.Values["change"].ShouldContain(Masking.MaskEmail(newEmail)!);
        told.Values["change"].ShouldNotContain(newEmail);
    }

    [Fact]
    public async Task Nobody_approves_a_change_to_their_own_account_even_one_another_administrator_asked_for()
    {
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var holder = (await superAdmin.GetAsync<PagedResult<UserListItem>>($"{Users}?search={HospitalServer.Users.HospitalAdmin}"))
            .Items.Single(u => u.UserName == HospitalServer.Users.HospitalAdmin);
        var account = await superAdmin.GetAsync<UserDetail>($"{Users}/{holder.Id}");

        using var response = await superAdmin.SendRawAsync(HttpMethod.Put, $"{Users}/{account.Id}",
            Update(account) with { Email = $"it-own-{Letters(8)}@example.test", Reason = "Moved to the hospital's own mail." });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        try
        {
            asked.Kind.ShouldBe(AccountApprovals.ContactChangeKind);
            (await RefusalAsync(hospitalAdmin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
                .ShouldBe((HttpStatusCode.Forbidden, AccountApprovals.OwnAccountCode));
            (await superAdmin.GetAsync<UserDetail>($"{Users}/{account.Id}")).Email.ShouldBe(account.Email);
        }
        finally
        {
            // The seeded hospital administrator's account is shared by the other tests: nothing is left waiting on it.
            var left = await superAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
            await superAdmin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/withdraw", new WithdrawApprovalRequest(left.Version));
        }
    }

    [Fact]
    public async Task Linking_an_account_to_a_practitioner_waits_and_an_approval_over_an_account_changed_since_fails_stale()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await CreateUserAsync(admin, "it-link-" + Letters(6), email: null, phone: null);
        var first = await DoctorAsync(admin);
        var second = await DoctorAsync(admin);
        var user = await admin.GetAsync<UserDetail>($"{Users}/{created.UserId}");

        // Linked to a consultant, the account would sign his notes and prescriptions: it waits, and stays unlinked meanwhile.
        using (var response = await admin.SendRawAsync(HttpMethod.Put, $"{Users}/{user.Id}", Update(user) with { PractitionerId = first }))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
            var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
            asked.Kind.ShouldBe(AccountApprovals.PractitionerLinkKind);
            asked.Details.ShouldContain(d => d.Label == "Linked now" && d.Value == "No practitioner");
            asked.Details.ShouldContain(d => d.Label == "Asked for" && d.Value.Contains("Consultant", StringComparison.Ordinal));
            (await admin.GetAsync<UserDetail>($"{Users}/{user.Id}")).PractitionerId.ShouldBeNull();

            await server.ApproveGrantAsync(asked.Id);
        }

        user = await admin.GetAsync<UserDetail>($"{Users}/{user.Id}");
        user.PractitionerId.ShouldBe(first);

        // Re-linked to another doctor, then unlinked at once while that waits: the approval finds the account changed.
        using var relink = await admin.SendRawAsync(HttpMethod.Put, $"{Users}/{user.Id}", Update(user) with { PractitionerId = second });
        relink.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var relinked = (await relink.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        user = await admin.GetAsync<UserDetail>($"{Users}/{user.Id}");
        using (var unlink = await admin.SendRawAsync(HttpMethod.Put, $"{Users}/{user.Id}", Update(user) with { PractitionerId = null }))
        {
            unlink.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        (await RefusalAsync(superAdmin, $"/api/v1/approvals/{relinked.Id}/approve", new ApproveRequest(null, relinked.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await superAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{relinked.Id}")).Status.ShouldBe(ApprovalStatus.Failed);
        (await admin.GetAsync<UserDetail>($"{Users}/{user.Id}")).PractitionerId.ShouldBeNull();
    }

    [Fact]
    public async Task A_linked_doctors_registration_number_waits_and_an_unlinked_practitioners_changes_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var linked = await DoctorAsync(admin);
        var unlinked = await DoctorAsync(admin);
        await CreateUserAsync(admin, "it-signs-as-" + Letters(6), email: null, phone: null, linked);

        // The doctor signs in: the new number waits, and so does the fee changed with it (a doctor's fee waits for someone
        // who approves prices, slice 6); the answer names the identity's request, and the record shows both.
        var doctor = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{linked}");
        doctor.LinkedAccounts.ShouldNotBeNull().ShouldNotBeEmpty();
        var number = Digits(7);
        using var response = await admin.SendRawAsync(HttpMethod.Put, $"{Practitioners}/{linked}", Save(doctor, number) with { ConsultationFeeNew = 750m });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        asked.Kind.ShouldBe(PractitionerIdentityApprovals.Kind);
        asked.Details.ShouldContain(new ApprovalDetail("Registration number", $"{doctor.RegistrationNumber} → {number}"));
        var waiting = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{linked}");
        (waiting.RegistrationNumber, waiting.ConsultationFeeNew).ShouldBe((doctor.RegistrationNumber, doctor.ConsultationFeeNew));
        waiting.PendingIdentity.ShouldNotBeNull().Asked.RegistrationNumber.ShouldBe(number);
        waiting.PendingFee.ShouldNotBeNull().Asked.NewVisit.ShouldBe(750m);

        (await RefusalAsync(admin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        await server.ApproveGrantAsync(asked.Id);
        (await admin.GetAsync<PractitionerDto>($"{Practitioners}/{linked}")).RegistrationNumber.ShouldBe(number);

        // Nobody signs in as this one: nothing is signed in their name, so the number changes at once.
        var other = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{unlinked}");
        var otherNumber = Digits(7);
        using var saved = await admin.SendRawAsync(HttpMethod.Put, $"{Practitioners}/{unlinked}", Save(other, otherNumber));
        saved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync(Ct));
        (await admin.GetAsync<PractitionerDto>($"{Practitioners}/{unlinked}")).RegistrationNumber.ShouldBe(otherNumber);
    }

    [Fact]
    public async Task A_second_workstation_waits_and_its_code_appears_only_once_approved_while_the_first_and_a_kiosk_are_at_once()
    {
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // The test server's workstation is enrolled: another waits.
        using var response = await superAdmin.SendRawAsync(HttpMethod.Post, EnrollmentCodes, new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, "Billing office PC 3", 30));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        var asked = JsonSerializer.Deserialize<ApprovalRequestDto>(body, ApiClient.Json)!;
        asked.Kind.ShouldBe(DeviceEnrollmentApprovals.Kind);
        body.ShouldNotContain("\"code\":\"", Case.Insensitive);
        (await superAdmin.GetAsync<List<EnrollmentCodeRequestDto>>($"{EnrollmentCodes}/requests")).ShouldContain(r => r.ApprovalId == asked.Id && !r.Ready);
        (await RefusalAsync(superAdmin, $"{EnrollmentCodes}/requests/{asked.Id}/code", new { })).ShouldBe((HttpStatusCode.Conflict, "devices.enrollment_waiting"));
        (await RefusalAsync(superAdmin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // Approved, the code is shown once to the administrator who asked, and enrols the PC once.
        var approved = await hospitalAdmin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version));
        (approved.Status, approved.Handover).ShouldBe((ApprovalStatus.Approved, (ApprovalHandoverDto?)null));
        (await superAdmin.GetAsync<List<EnrollmentCodeRequestDto>>($"{EnrollmentCodes}/requests")).ShouldContain(r => r.ApprovalId == asked.Id && r.Ready);
        var code = await superAdmin.PostAsync<EnrollmentCodeResponse>($"{EnrollmentCodes}/requests/{asked.Id}/code");
        code.ExpiresAt.ShouldBe(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(1));
        (await RefusalAsync(superAdmin, $"{EnrollmentCodes}/requests/{asked.Id}/code", new { })).ShouldBe((HttpStatusCode.UnprocessableEntity, "devices.enrollment_code_shown"));
        var enrolled = await server.AnonymousFrom(NewAddress()).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Billing office PC 3 " + Letters(4), "BILL-PC-3", "Windows 11", "1.0.0"));
        await superAdmin.PostAsync($"/api/v1/admin/devices/{enrolled.DeviceId}/revoke");

        // A kiosk administers nothing: its code is made at once.
        var kiosk = await superAdmin.PostAsync<EnrollmentCodeResponse>(EnrollmentCodes, new CreateEnrollmentCodeRequest(DeviceKind.Kiosk, null, "OPD self check-in", 30));
        kiosk.Code.ShouldNotBeNullOrWhiteSpace();

        // Nor does the first workstation wait, at installation: the enrolled ones are set aside for it.
        var setAside = await SetAsideAsync(revoked: true, []);
        try
        {
            var firstCode = await superAdmin.PostAsync<EnrollmentCodeResponse>(EnrollmentCodes, new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, "Server room PC", 30));
            firstCode.Code.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            await SetAsideAsync(revoked: false, setAside);
        }
    }

    private async Task<TemporaryPasswordResponse> CreateUserAsync(ApiClient admin, string name, string? email, string? phone, Guid? practitionerId = null)
    {
        var roleId = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == SystemRoles.Nurse).Id;
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId;
        return await server.CreateUserAsync(admin, new CreateUserRequest(
            name, $"Test {name}", email, phone, [roleId], facilityId is { } f ? [f] : [], facilityId, practitionerId, TemporaryPassword: null));
    }

    /// <summary>A consultant added to the practitioner master by the hospital administrator, with a council registration and no account.</summary>
    private static async Task<Guid> DoctorAsync(ApiClient admin)
    {
        var created = await admin.PostAsync<JsonElement>(Practitioners, new SavePractitionerRequest(
            Code: $"AC{Letters(8)}", Title: "Dr.", FirstName: "Arun", LastName: "Acct" + Letters(6), Gender: Gender.Male, Qualification: "MD (General Medicine)",
            RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: Digits(6), HprId: null, SpecialtyId: null, DepartmentId: null,
            Kind: PractitionerKind.Consultant, Phone: null, Email: null, ConsultationFeeNew: 0m, ConsultationFeeFollowUp: 0m, FollowUpValidityDays: 7,
            Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
            FacilityIds: null, IsActive: true, Version: null));
        return created.GetProperty("id").GetGuid();
    }

    /// <summary>The practitioner as read, saved again with another registration number.</summary>
    private static SavePractitionerRequest Save(PractitionerDto p, string registrationNumber) => new(
        p.Code, p.Title, p.FirstName, p.LastName, p.Gender, p.Qualification, p.RegistrationCouncil, registrationNumber, p.HprId,
        p.SpecialtyId, p.DepartmentId, p.Kind, p.Phone, p.Email, p.ConsultationFeeNew, p.ConsultationFeeFollowUp, p.FollowUpValidityDays,
        p.Languages, p.Bio, p.PhotoFileId, p.SignatureFileId, p.AvailableForOnlineBooking, p.AvailableForTelehealth, p.FacilityIds,
        p.IsActive, p.Version);

    /// <summary>The user as read, saved again as it is.</summary>
    private static UpdateUserRequest Update(UserDetail u) =>
        new(u.DisplayName, u.Email, u.Phone, u.RoleIds, u.FacilityIds, u.DefaultFacilityId, u.PractitionerId, u.IsActive, u.Version);

    /// <summary>
    /// Two-step verification turned on for the account, as if its holder had set it up: a secret made now, kept encrypted as
    /// the server keeps it. Their open sessions go on.
    /// </summary>
    private async Task<Guid> TurnOnTwoStepVerificationAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
        var secret = protector.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)));
        var users = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>();
        await users.Where(u => u.NormalizedUserName == name).ExecuteUpdateAsync(u => u.SetProperty(x => x.MfaEnabled, true).SetProperty(x => x.MfaSecret, secret), Ct);
        return await users.Where(u => u.NormalizedUserName == name).Select(u => u.Id).SingleAsync(Ct);
    }

    private async Task<User> UserAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>().AsNoTracking().SingleAsync(u => u.Id == id, Ct);
    }

    private async Task<List<string>> NoticesAsync(Guid userId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.UserId == userId).Select(n => n.Title).ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid userId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = userId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityType == nameof(User) && e.EntityId == key).ToListAsync(Ct);
    }

    /// <summary>The messages of this template waiting in the outbox for <paramref name="to"/>: the test server sends none.</summary>
    private async Task<List<NotificationDelivery>> SmsAsync(string template, string to) => await DeliveriesAsync(NotificationService.SmsType, template, to);

    private async Task<List<NotificationDelivery>> EmailsAsync(string template, string to) => await DeliveriesAsync(NotificationService.EmailType, template, to);

    private async Task<List<NotificationDelivery>> DeliveriesAsync(string type, string template, string to)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == type).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(Ct);
        return [.. messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == template && d.To == to)];
    }

    /// <summary>
    /// Marks the enrolled workstations and tablets revoked in the database, without ending their sessions as revoking them
    /// would, and returns them; with <paramref name="revoked"/> false, puts <paramref name="devices"/> back.
    /// </summary>
    private async Task<List<Guid>> SetAsideAsync(bool revoked, List<Guid> devices)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var all = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>();
        if (revoked)
        {
            devices = await all.Where(d => !d.IsRevoked && (d.Kind == DeviceKind.Workstation || d.Kind == DeviceKind.Tablet))
                .Select(d => d.Id).ToListAsync(Ct);
        }

        await all.Where(d => devices.Contains(d.Id)).ExecuteUpdateAsync(d => d.SetProperty(x => x.IsRevoked, revoked), Ct);
        return devices;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private Task<HttpResponseMessage> LogInAsync(string name, string password) =>
        server.AnonymousFrom(NewAddress()).SendRawAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(name, password));

    private static string NewAddress()
    {
        var n = Interlocked.Increment(ref _addresses);
        return $"10.252.{n / 250}.{(n % 250) + 1}";
    }

    private static string NewMobile() => "9" + Digits(9);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
