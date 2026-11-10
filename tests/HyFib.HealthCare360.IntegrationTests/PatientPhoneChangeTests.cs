using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Changing a patient's registered mobile number, which the patient portal signs in with, over the real API: the edit
/// refuses a new number; a code goes by SMS to the new number, none to the old one, and the number changes when staff
/// enter it. Wrong codes count, a code works for 10 minutes and 5 tries, a number gets 3 codes in 15 minutes, and a change
/// is made only on the number the record had when the code was sent. What the old number's portal account saw follows
/// the patient when nobody else keeps the old number, and the account is signed out once the old number reaches nobody.
/// </summary>
public sealed class PatientPhoneChangeTests(HospitalServer server)
{
    private const string PortalBase = "https://portal.nanahospitals.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_edit_refuses_a_new_number_and_still_keeps_or_removes_the_one_on_file()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterAsync(frontOffice, "Kamala");
        var detail = await DetailAsync(frontOffice, patient.Id);

        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Put, PatientPath(patient.Id), Edit(detail) with { Phone = NewMobile() }))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("patient.phone_needs_code");
            problem.GetProperty("errors").GetProperty("phone")[0].GetString().ShouldBe(PatientPhoneChangeService.NeedsCodeMessage);
        }

        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBe(phone, "nothing was saved");

        // The number on file sent back, written another way, is no change; other details are saved with it.
        await frontOffice.PutAsync(PatientPath(patient.Id), Edit(detail) with { Phone = phone[3..], Occupation = "Weaver" });
        detail = await DetailAsync(frontOffice, patient.Id);
        (detail.Phone, detail.Occupation).ShouldBe((phone, "Weaver"));

        // A form loaded before the number changed is out of date: a conflict, so it reloads.
        await ChangeAsync(frontOffice, patient.Id, NewMobile());
        using (var stale = await frontOffice.SendRawAsync(HttpMethod.Put, PatientPath(patient.Id), Edit(detail)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // Removing the number is still an edit.
        detail = await DetailAsync(frontOffice, patient.Id);
        await frontOffice.PutAsync(PatientPath(patient.Id), Edit(detail) with { Phone = null });
        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBeNull();
    }

    [Fact]
    public async Task A_code_sent_to_the_new_number_changes_it_and_stops_the_link_sent_to_the_old_one()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, oldPhone) = await RegisterAsync(frontOffice, "Bhavani");
        await frontOffice.PostAsync<PortalLinkSent>($"/api/v1/patients/{patient.Id}/portal-link", new SendPortalLinkRequest([NotificationChannel.Sms]));
        var newPhone = Phone.Normalize(NewMobile())!;
        var before = DateTimeOffset.UtcNow;

        (await PendingAsync(frontOffice, patient.Id)).ShouldBeNull();
        var sent = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(newPhone[3..]));
        (sent.MaskedPhone, sent.AttemptsLeft).ShouldBe((Phone.Mask(newPhone), PatientPhoneChange.MaxAttempts));
        sent.ExpiresAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
        var pending = (await PendingAsync(frontOffice, patient.Id)).ShouldNotBeNull("a dialog opened again carries on with it");
        (pending.ChangeId, pending.MaskedPhone, pending.AttemptsLeft).ShouldBe((sent.ChangeId, sent.MaskedPhone, sent.AttemptsLeft));
        pending.ExpiresAt.ShouldBe(sent.ExpiresAt, TimeSpan.FromMilliseconds(1));

        // The code goes by SMS to the new number; nothing at all goes to the old one.
        var message = (await SmsAsync(newPhone, before)).ShouldHaveSingleItem();
        (message.TemplateKey, message.Channel).ShouldBe((PatientPhoneChangeService.Template, NotificationChannel.Sms));
        var code = message.Values["code"];
        code.Length.ShouldBe(6);
        (await SmsAsync(oldPhone, before)).ShouldBeEmpty();
        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBe(oldPhone, "nothing changes before the code is entered");

        // A wrong code counts.
        var wrong = await ConfirmRefusedAsync(frontOffice, patient.Id, sent.ChangeId, WrongCode(code), HttpStatusCode.BadRequest);
        wrong.GetProperty("errors").GetProperty("code")[0].GetString().ShouldBe("The code is incorrect. 4 tries left.");
        wrong.GetProperty("data").GetProperty("attemptsLeft").GetInt32().ShouldBe(4);
        (await PendingAsync(frontOffice, patient.Id))!.AttemptsLeft.ShouldBe(4);

        var confirmed = await frontOffice.PostAsync<PhoneChangeConfirmed>($"{ChangePath(patient.Id)}/{sent.ChangeId}/confirm", new ConfirmPhoneChangeRequest(code));
        confirmed.MaskedPhone.ShouldBe(Phone.Mask(newPhone));
        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBe(newPhone);
        (await PendingAsync(frontOffice, patient.Id)).ShouldBeNull();
        await ConfirmRefusedAsync(frontOffice, patient.Id, sent.ChangeId, code, HttpStatusCode.UnprocessableEntity, "patient.phone_code_expired");

        // The link sent to the old number stops, and says why.
        var link = (await frontOffice.GetAsync<PatientPortalLinksDto>($"/api/v1/patients/{patient.Id}/portal-links")).Links.ShouldHaveSingleItem();
        (link.State, link.RevokedReason).ShouldBe((PortalLinkState.Revoked, PortalLink.NumberChangedReason));

        // Audited on the record: the code sent and the change, with the numbers masked and never the code.
        var me = await frontOffice.GetAsync<AuthUser>("/api/v1/auth/me");
        var codeSent = (await AuditAsync(patient.Id, PatientPhoneChangeService.CodeSentAction)).ShouldHaveSingleItem();
        codeSent.Summary.ShouldNotBeNull().ShouldContain(Phone.Mask(newPhone));
        var changed = (await AuditAsync(patient.Id, PatientPhoneChangeService.ChangedAction)).ShouldHaveSingleItem();
        changed.Severity.ShouldBe(AuditSeverity.Warning);
        changed.Summary.ShouldBe(
            $"Mobile number changed from {Phone.Mask(oldPhone)} to {Phone.Mask(newPhone)} with a code sent to the new number; asked for by {me.DisplayName}, confirmed by {me.DisplayName}");
        using (var details = JsonDocument.Parse(changed.Details!))
        {
            details.RootElement.GetProperty("changeId").GetGuid().ShouldBe(sent.ChangeId);
            details.RootElement.GetProperty("askedById").GetGuid().ShouldBe(me.Id);
            details.RootElement.GetProperty("confirmedById").GetGuid().ShouldBe(me.Id);
        }

        foreach (var entry in await AuditOfPatientAsync(patient.Id, sent.ChangeId))
        {
            var text = $"{entry.Summary} {entry.Details}";
            text.ShouldNotContain(code);
            text.ShouldNotContain(PatientPhoneChange.HashOf(sent.ChangeId, code), Case.Insensitive);
            if (entry.Action is PatientPhoneChangeService.CodeSentAction or PatientPhoneChangeService.ChangedAction)
            {
                text.ShouldNotContain(newPhone[3..]);
                text.ShouldNotContain(oldPhone[3..]);
            }
        }
    }

    [Fact]
    public async Task A_code_works_for_ten_minutes_and_five_tries_counted_one_at_a_time()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, oldPhone) = await RegisterAsync(frontOffice, "Saroja");

        // Expired: refused, and no longer waiting.
        var firstPhone = Phone.Normalize(NewMobile())!;
        var first = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(firstPhone));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientPhoneChange>()
                .Where(c => c.Id == first.ChangeId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        }

        (await PendingAsync(frontOffice, patient.Id)).ShouldBeNull();
        var code = (await SmsAsync(firstPhone, DateTimeOffset.MinValue)).ShouldHaveSingleItem().Values["code"];
        await ConfirmRefusedAsync(frontOffice, patient.Id, first.ChangeId, code, HttpStatusCode.UnprocessableEntity, "patient.phone_code_expired");

        // Five tries, sent all at once: five are judged, wrong, and the rest find the code used up; then even the right
        // code is refused.
        var newPhone = Phone.Normalize(NewMobile())!;
        var second = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(newPhone));
        var right = (await SmsAsync(newPhone, DateTimeOffset.MinValue)).ShouldHaveSingleItem().Values["code"];
        var tries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            frontOffice.SendRawAsync(HttpMethod.Post, $"{ChangePath(patient.Id)}/{second.ChangeId}/confirm", new ConfirmPhoneChangeRequest(WrongCode(right)))));
        try
        {
            tries.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(PatientPhoneChange.MaxAttempts, "every wrong code counts");
            tries.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(8 - PatientPhoneChange.MaxAttempts);
        }
        finally
        {
            foreach (var response in tries)
            {
                response.Dispose();
            }
        }

        await ConfirmRefusedAsync(frontOffice, patient.Id, second.ChangeId, right, HttpStatusCode.UnprocessableEntity, "patient.phone_code_expired");
        (await PendingAsync(frontOffice, patient.Id)).ShouldBeNull();
        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBe(oldPhone);
        (await StatusAsync(first.ChangeId, second.ChangeId)).ShouldBe([PatientPhoneChangeStatus.Expired, PatientPhoneChangeStatus.Expired]);
    }

    [Fact]
    public async Task A_change_is_made_only_on_the_number_the_record_had_when_the_code_was_sent()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, _) = await RegisterAsync(frontOffice, "Ponnammal");
        var newPhone = Phone.Normalize(NewMobile())!;

        // The number on file is refused as a new one, under the field.
        using (var same = await frontOffice.SendRawAsync(HttpMethod.Post, ChangePath(patient.Id),
                   new RequestPhoneChangeRequest((await DetailAsync(frontOffice, patient.Id)).Phone!)))
        {
            same.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await same.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("errors").GetProperty("phone")[0].GetString()
                .ShouldBe("The patient is registered with this number already.");
        }

        // Only an Indian mobile number: any other goes in the alternate number.
        using (var landline = await frontOffice.SendRawAsync(HttpMethod.Post, ChangePath(patient.Id), new RequestPhoneChangeRequest("020 2612 3456")))
        {
            landline.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await landline.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("errors").GetProperty("phone")[0].GetString()
                .ShouldBe(PatientPhoneChange.IndianMobileMessage);
        }

        var sent = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(newPhone));
        var code = (await SmsAsync(newPhone, DateTimeOffset.MinValue)).ShouldHaveSingleItem().Values["code"];

        // Meanwhile the number is removed at the desk: the code is for a change of a number the record no longer has.
        var detail = await DetailAsync(frontOffice, patient.Id);
        await frontOffice.PutAsync(PatientPath(patient.Id), Edit(detail) with { Phone = null });

        await ConfirmRefusedAsync(frontOffice, patient.Id, sent.ChangeId, code, HttpStatusCode.Conflict, "patient.phone_changed_meanwhile");
        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBeNull();
        (await StatusAsync(sent.ChangeId)).ShouldBe([PatientPhoneChangeStatus.Superseded]);

        // A newer code replaces one still waiting; the older one no longer works.
        var again = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(NewMobile()));
        var latest = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(newPhone));
        (await PendingAsync(frontOffice, patient.Id))!.ChangeId.ShouldBe(latest.ChangeId);
        (await StatusAsync(again.ChangeId)).ShouldBe([PatientPhoneChangeStatus.Superseded]);
        var latestCode = (await SmsAsync(newPhone, DateTimeOffset.MinValue))[0].Values["code"];
        await frontOffice.PostAsync<PhoneChangeConfirmed>($"{ChangePath(patient.Id)}/{latest.ChangeId}/confirm", new ConfirmPhoneChangeRequest(latestCode));
        (await DetailAsync(frontOffice, patient.Id)).Phone.ShouldBe(newPhone, "a patient with no number gets one the same way");
    }

    [Fact]
    public async Task A_number_is_sent_three_codes_in_fifteen_minutes_whichever_patients_they_are_for()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, _) = await RegisterAsync(frontOffice, "Rukmini");
        var (other, _) = await RegisterAsync(frontOffice, "Janaki");
        var target = Phone.Normalize(NewMobile())!;

        for (var i = 0; i < PatientPhoneChange.MaxCodesPerNumber; i++)
        {
            await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(i == 0 ? other.Id : patient.Id), new RequestPhoneChangeRequest(target));
        }

        foreach (var id in new[] { patient.Id, other.Id })
        {
            using var throttled = await frontOffice.SendRawAsync(HttpMethod.Post, ChangePath(id), new RequestPhoneChangeRequest(target));
            throttled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await throttled.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe(PatientPhoneChange.ThrottledCode);
            problem.GetProperty("data").GetProperty("retryAfterSeconds").GetInt32().ShouldBeInRange(1, 15 * 60);
        }

        (await SmsAsync(target, DateTimeOffset.MinValue)).Count.ShouldBe(PatientPhoneChange.MaxCodesPerNumber, "the refused ones sent nothing");

        // Another number is not held up.
        await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(NewMobile()));
    }

    [Fact]
    public async Task Family_members_follow_the_patient_to_the_new_number_unless_someone_else_keeps_the_old_one()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // Nobody else keeps the old number: the relatives its account saw are seen from the new number.
        var (mother, _) = await RegisterAsync(frontOffice, "Annapurna");
        var (son, oldPhone) = await RegisterAsync(frontOffice, "Gopalan");
        var (aunt, _) = await RegisterAsync(frontOffice, "Meenambal");
        await ShareAsync(frontOffice, mother.Id, oldPhone);
        await ShareAsync(frontOffice, aunt.Id, oldPhone);
        var newPhone = Phone.Normalize(NewMobile())!;
        await ShareAsync(frontOffice, aunt.Id, newPhone); // the new number sees the aunt already
        await ShareAsync(frontOffice, son.Id, newPhone); // and the son himself, through a link

        var confirmed = await ChangeAsync(frontOffice, son.Id, newPhone);

        confirmed.FamilyLinksMoved.ShouldBe(1);
        var motherLink = (await AccessAsync(frontOffice, mother.Id)).Links.ShouldHaveSingleItem();
        (motherLink.AccountPhone, motherLink.IsActive).ShouldBe((newPhone, true));
        var auntLinks = (await AccessAsync(frontOffice, aunt.Id)).Links;
        auntLinks.Single(l => l.IsActive).AccountPhone.ShouldBe(newPhone, "the duplicate held by the new number is the one kept");
        auntLinks.Single(l => !l.IsActive).AccountPhone.ShouldBe(oldPhone);
        (await AccessAsync(frontOffice, son.Id)).Links.ShouldAllBe(l => !l.IsActive, "the new number sees the son as its own now");

        var newAccount = await server.SignInToPortalAsync(newPhone);
        (await newAccount.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).Select(p => p.Id)
            .ShouldBe([son.Id, mother.Id, aunt.Id], ignoreOrder: true);
        (await AuditAsync(mother.Id, "portal.family.moved")).ShouldHaveSingleItem().Summary.ShouldNotBeNull().ShouldContain(Phone.Mask(newPhone));

        // Another patient still has the old number: its account is theirs, and keeps the relatives it saw.
        var (wife, sharedPhone) = await RegisterAsync(frontOffice, "Sundari");
        var (husband, _) = await RegisterAsync(frontOffice, "Velayudham", sharedPhone);
        var (grandmother, _) = await RegisterAsync(frontOffice, "Kaveri");
        await ShareAsync(frontOffice, grandmother.Id, sharedPhone);

        var kept = await ChangeAsync(frontOffice, husband.Id, NewMobile());

        kept.FamilyLinksMoved.ShouldBe(0);
        var grandmotherLink = (await AccessAsync(frontOffice, grandmother.Id)).Links.ShouldHaveSingleItem();
        (grandmotherLink.AccountPhone, grandmotherLink.IsActive).ShouldBe((sharedPhone, true));
        var shared = await server.SignInToPortalAsync(sharedPhone);
        (await shared.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).Select(p => p.Id)
            .ShouldBe([wife.Id, grandmother.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task The_old_numbers_portal_is_signed_out_once_it_reaches_nobody_and_kept_while_it_reaches_someone()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // Two patients share a number; one moves to a number of her own: the other still signs in with the old one.
        var (sister, sharedPhone) = await RegisterAsync(frontOffice, "Kalyani");
        var (brother, _) = await RegisterAsync(frontOffice, "Murugesan", sharedPhone);
        var family = await server.SignInToPortalAsync(sharedPhone);
        (await family.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).Count.ShouldBe(2);

        var moved = await ChangeAsync(frontOffice, sister.Id, NewMobile());

        moved.OldNumberSignedOut.ShouldBeFalse();
        (await family.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).Select(p => p.Id).ShouldBe([brother.Id]);

        // The brother moves too: the old number reaches nobody's records, so its session ends, and the account forgets him.
        var last = await ChangeAsync(frontOffice, brother.Id, NewMobile());

        last.OldNumberSignedOut.ShouldBeTrue();
        using (var refused = await family.SendRawAsync(HttpMethod.Get, "/api/v1/portal/profiles"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the old number's session ended");
        }

        var account = await PortalAccountAsync(sharedPhone);
        (account.DisplayName, account.PatientId, account.IsActive).ShouldBe((Phone.Mask(sharedPhone), null, true));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>().AsNoTracking()
                    .Where(s => s.UserId == account.Id).Select(s => s.RevokedReason).ToListAsync(Ct))
                .ShouldAllBe(reason => reason == PatientPhoneChangeService.SessionEndedReason);
        }

        // Whoever signs in with the old number later sees nobody's records, and not the brother's name.
        var nextHolder = await server.SignInToPortalAsync(sharedPhone);
        (await nextHolder.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldBeEmpty();
        var signedInAgain = await PortalAccountAsync(sharedPhone);
        (signedInAgain.Id, signedInAgain.DisplayName, signedInAgain.PatientId).ShouldBe((account.Id, Phone.Mask(sharedPhone), null));
    }

    [Fact]
    public async Task Only_staff_who_edit_patients_change_their_number()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, _) = await RegisterAsync(frontOffice, "Thilaga");
        var sent = await frontOffice.PostAsync<PhoneChangeDto>(ChangePath(patient.Id), new RequestPhoneChangeRequest(NewMobile()));

        // A doctor reads the record and its contacts, but does not edit registrations.
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, ChangePath(patient.Id), null),
                     (HttpMethod.Post, ChangePath(patient.Id), new RequestPhoneChangeRequest(NewMobile())),
                     (HttpMethod.Post, $"{ChangePath(patient.Id)}/{sent.ChangeId}/confirm", new ConfirmPhoneChangeRequest("123456")),
                 })
        {
            using var refused = await doctor.SendRawAsync(method, path, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        }

        (await PendingAsync(frontOffice, patient.Id))!.AttemptsLeft.ShouldBe(PatientPhoneChange.MaxAttempts, "the doctor's try was not judged");
        using var badCode = await frontOffice.SendRawAsync(HttpMethod.Post, $"{ChangePath(patient.Id)}/{sent.ChangeId}/confirm", new ConfirmPhoneChangeRequest("12a456"));
        badCode.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PendingAsync(frontOffice, patient.Id))!.AttemptsLeft.ShouldBe(PatientPhoneChange.MaxAttempts, "a code of the wrong shape is no try");
    }

    private static string PatientPath(Guid patientId) => $"/api/v1/patients/{patientId}";

    private static string ChangePath(Guid patientId) => $"/api/v1/patients/{patientId}/phone-change";

    private static Task<PatientDetail> DetailAsync(ApiClient staff, Guid patientId) => staff.GetAsync<PatientDetail>(PatientPath(patientId));

    private static Task<PatientPortalAccessDto> AccessAsync(ApiClient staff, Guid patientId) =>
        staff.GetAsync<PatientPortalAccessDto>($"/api/v1/patients/{patientId}/portal-family");

    private Task ShareAsync(ApiClient staff, Guid patientId, string accountPhone) =>
        server.LinkFamilyAtDeskAsync(staff, patientId, new LinkFamilyMemberRequest(accountPhone, FamilyRelation.Other, "Patient", true));

    /// <summary>The change waiting for its code, or null (204).</summary>
    private static async Task<PhoneChangeDto?> PendingAsync(ApiClient staff, Guid patientId)
    {
        using var response = await staff.SendRawAsync(HttpMethod.Get, ChangePath(patientId));
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<PhoneChangeDto>(ApiClient.Json, Ct);
    }

    /// <summary>Changes the patient's number as the desk does: a code to the new number, read from the outbox, entered.</summary>
    private async Task<PhoneChangeConfirmed> ChangeAsync(ApiClient staff, Guid patientId, string newPhone)
    {
        var normalized = Phone.Normalize(newPhone)!;
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var sent = await staff.PostAsync<PhoneChangeDto>(ChangePath(patientId), new RequestPhoneChangeRequest(newPhone));
        var code = (await SmsAsync(normalized, before)).ShouldHaveSingleItem().Values["code"];
        return await staff.PostAsync<PhoneChangeConfirmed>($"{ChangePath(patientId)}/{sent.ChangeId}/confirm", new ConfirmPhoneChangeRequest(code));
    }

    private static async Task<JsonElement> ConfirmRefusedAsync(ApiClient staff, Guid patientId, Guid changeId, string code, HttpStatusCode status, string? problemCode = null)
    {
        using var response = await staff.SendRawAsync(HttpMethod.Post, $"{ChangePath(patientId)}/{changeId}/confirm", new ConfirmPhoneChangeRequest(code));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        if (problemCode is not null)
        {
            problem.GetProperty("code").GetString().ShouldBe(problemCode);
        }

        return problem;
    }

    private static string WrongCode(string code) => code == "000000" ? "111111" : "000000";

    // The test server sends no SMS: the messages wait in the outbox, newest first.
    private async Task<List<NotificationDelivery>> SmsAsync(string phone, DateTimeOffset since)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt)
            .Take(300)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.To == phone).ToList();
    }

    /// <summary>The portal account that signs in with <paramref name="phone"/>.</summary>
    private async Task<User> PortalAccountAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>().AsNoTracking()
            .SingleAsync(u => u.Type == UserType.Patient && u.Phone == phone, Ct);
    }

    private async Task<List<PatientPhoneChangeStatus>> StatusAsync(params Guid[] changeIds)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var statuses = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientPhoneChange>().AsNoTracking()
            .Where(c => changeIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Status })
            .ToListAsync(Ct);
        return [.. changeIds.Select(id => statuses.Single(s => s.Id == id).Status)];
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .ToListAsync(Ct);
    }

    /// <summary>Every audit row about the patient or the change: the actions, and the data audit of the change's own row.</summary>
    private async Task<List<AuditEvent>> AuditOfPatientAsync(Guid patientId, Guid changeId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = changeId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId || (e.EntityType == nameof(PatientPhoneChange) && e.EntityId == id))
            .ToListAsync(Ct);
    }

    /// <summary>The portal's public address, for this test only: other tests run without one.</summary>
    private async Task<IAsyncDisposable> PortalAddressAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(PortalAddress.BaseUrlSetting, PortalBase, cancellationToken: Ct);
        return new Cleanup(async () =>
        {
            await using var cleanup = server.Services.CreateAsyncScope();
            await cleanup.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(PortalAddress.BaseUrlSetting, null, CancellationToken.None);
        });
    }

    /// <summary>A patient of this test alone, on a number of its own, or on <paramref name="phone"/> shared with another patient.</summary>
    private static async Task<(PatientCreated Patient, string Phone)> RegisterAsync(ApiClient frontOffice, string firstName, string? phone = null)
    {
        var mobile = phone ?? NewMobile();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Number", Gender: Gender.Female,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: phone is not null));
        return (patient, Phone.Normalize(mobile)!);
    }

    private static UpdatePatientRequest Edit(PatientDetail p) => new(
        p.Title, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.DateOfBirth, null, null, p.Phone, p.AlternatePhone,
        p.Email, p.Address, p.BloodGroup, p.MaritalStatus, p.GuardianName, p.GuardianRelation, p.EmergencyContactName,
        p.EmergencyContactPhone, p.EmergencyContactRelation, p.Occupation, p.Religion, p.Nationality, p.PreferredLanguage,
        p.Category, null, p.AbhaAddress, null, p.PhotoFileId, p.IsVip, p.IsDeceased, p.DeceasedAt, p.Notes,
        p.IsActive, p.Version);

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private sealed class Cleanup(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose();
    }
}
