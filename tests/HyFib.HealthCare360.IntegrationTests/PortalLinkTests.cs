using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Links to the patient portal that staff send to the patient's registered number (PP-01), over the real API: the link
/// only lets whoever opens it ask for a sign-in code to that number, and the portal's own code check signs the patient in.
/// A link stops when a newer one is sent, when the record's number changes or the record is put in a protected category,
/// and after 7 days; anything that is not a working link gets the same 404.
/// </summary>
public sealed class PortalLinkTests(HospitalServer server)
{
    private const string PortalBase = "https://portal.nanahospitals.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Staff_send_a_link_and_the_patient_signs_in_with_the_code_it_sends_to_their_number()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterAsync(frontOffice, "Meenakshi");

        var sent = await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        sent.Channels.ShouldBe([NotificationChannel.Sms]);
        sent.MaskedPhone.ShouldBe(Phone.Mask(phone));
        sent.Notice.ShouldBeNull();
        sent.ExpiresAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7).AddMinutes(5));

        // The SMS waits in the outbox (the test server sends none) with the link to the portal's page.
        var message = (await LinkMessagesAsync(phone, NotificationService.SmsType)).ShouldHaveSingleItem();
        var link = message.Values["link"];
        link.ShouldStartWith(PortalBase + "/portal/link/");
        var token = link[(link.LastIndexOf('/') + 1)..];
        (await LinkMessagesAsync(phone, NotificationService.WhatsAppType)).ShouldBeEmpty();

        // The record shows the link, never the token or its hash.
        using (var listed = await frontOffice.SendRawAsync(HttpMethod.Get, ListPath(patient.Id)))
        {
            var body = await listed.Content.ReadAsStringAsync(Ct);
            listed.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.ShouldNotContain(token);
            body.ShouldNotContain(SecretTokens.Hash(token), Case.Insensitive);
        }

        var links = await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id));
        links.CannotSend.ShouldBeNull();
        links.MaskedPhone.ShouldBe(Phone.Mask(phone));
        var shown = links.Links.ShouldHaveSingleItem();
        (shown.Id, shown.State, shown.MaskedPhone, shown.OpenedAt).ShouldBe((sent.Id, PortalLinkState.Active, Phone.Mask(phone), null));
        shown.SentByName.ShouldBe((await frontOffice.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName);
        shown.Channels.ShouldBe([NotificationChannel.Sms]);

        // The patient opens the link on the portal's address: the hospital and the number masked, nothing else.
        var portal = server.AnonymousOnPortal(ownAddress: true);
        var opened = await portal.GetAsync<PublicPortalLinkDto>(PublicPath(token));
        opened.MaskedPhone.ShouldBe(Phone.Mask(phone));
        opened.Hospital.ShouldNotBeNullOrWhiteSpace();
        opened.ExpiresAt.ShouldBe(sent.ExpiresAt, TimeSpan.FromSeconds(1));
        (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id))).Links.Single().OpenedAt.ShouldBeNull("opening the page changes nothing");

        // Asking for a code through it sends one to the number, as the sign-in page does; the code signs the patient in there.
        var challenge = await portal.PostAsync<OtpChallengeResponse>(PublicPath(token) + "/otp");
        challenge.MaskedPhone.ShouldBe(Phone.Mask(phone));
        var signedIn = await portal.PostAsync<AuthResult>("/api/v1/auth/otp/verify", new OtpVerifyRequest(challenge.ChallengeId, await server.PortalCodeAsync(phone)));
        signedIn.AccessToken.ShouldNotBeNullOrEmpty();
        portal.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.AccessToken);
        (await portal.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldContain(p => p.Id == patient.Id);

        var afterSignIn = (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id))).Links.Single();
        afterSignIn.State.ShouldBe(PortalLinkState.Opened);
        afterSignIn.OpenedAt.ShouldNotBeNull();

        // The send is on the patient's record history; the link's own audit rows never hold the token or its hash.
        var history = await frontOffice.GetAsync<RecordHistoryDto>($"/api/v1/history/Patient/{patient.Id}");
        history.Entries.Items.ShouldContain(e => e.Action == "portal.link.sent" && e.What.Contains(Phone.Mask(phone), StringComparison.Ordinal));
        var trail = await AuditOfLinkAsync(sent.Id, patient.Id);
        trail.ShouldContain(e => e.Action == "PortalLink.create");
        trail.ShouldContain(e => e.Action == "PortalLink.update", "opening it is recorded");
        foreach (var entry in trail)
        {
            (entry.Details ?? string.Empty).ShouldNotContain(token);
            (entry.Details ?? string.Empty).ShouldNotContain(SecretTokens.Hash(token), Case.Insensitive);
            (entry.Summary ?? string.Empty).ShouldNotContain(token);
        }
    }

    [Fact]
    public async Task A_newer_link_replaces_the_older_and_a_fourth_in_a_day_is_refused()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterAsync(frontOffice, "Govindan");

        await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        var first = await LatestTokenAsync(phone);
        var second = await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id),
            new SendPortalLinkRequest([NotificationChannel.Sms, NotificationChannel.WhatsApp]));
        second.Channels.ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp]);
        var latest = await LatestTokenAsync(phone);
        (await LinkMessagesAsync(phone, NotificationService.WhatsAppType)).ShouldHaveSingleItem().Values["link"].ShouldEndWith("/" + latest);

        var portal = server.AnonymousOnPortal(ownAddress: true);
        await ShouldNotWorkAsync(portal, first, "a newer link replaced it");
        (await portal.GetAsync<PublicPortalLinkDto>(PublicPath(latest))).MaskedPhone.ShouldBe(Phone.Mask(phone));

        var links = (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id))).Links;
        links.Count.ShouldBe(2);
        links[0].Id.ShouldBe(second.Id, "newest first");
        (links[1].State, links[1].RevokedReason).ShouldBe((PortalLinkState.Revoked, PortalLink.ReplacedReason));

        await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        await ShouldBeRefusedAsync(frontOffice, patient.Id, [NotificationChannel.Sms], "portal.link_throttled");
        (await LinkMessagesAsync(phone, NotificationService.SmsType)).Count.ShouldBe(3, "the fourth sent nothing");
    }

    [Fact]
    public async Task Changing_the_patients_number_stops_the_link_sent_to_the_old_one()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterAsync(frontOffice, "Thangam");
        await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        var token = await LatestTokenAsync(phone);

        var detail = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        await frontOffice.PutAsync($"/api/v1/patients/{patient.Id}", Edit(detail) with { Phone = NewMobile() });

        await ShouldNotWorkAsync(server.AnonymousOnPortal(ownAddress: true), token, "the record no longer has the number it went to");
        var link = (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id))).Links.ShouldHaveSingleItem();
        (link.State, link.RevokedReason).ShouldBe((PortalLinkState.Revoked, PortalLink.NumberChangedReason));
    }

    [Fact]
    public async Task A_record_in_a_protected_category_gets_no_link_and_one_sent_before_stops_working()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterAsync(frontOffice, "Selvi");
        await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        var token = await LatestTokenAsync(phone);

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var categories = await doctor.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patient.Id}/sensitive-categories");
        await doctor.PutAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patient.Id}/sensitive-categories",
            new SetSensitiveCategoriesRequest([SensitiveCategory.MentalHealth], "Diagnosis recorded in the clinic", categories.Version));

        await ShouldNotWorkAsync(server.AnonymousOnPortal(ownAddress: true), token, "the record was put in a protected category");
        var problem = await ShouldBeRefusedAsync(frontOffice, patient.Id, [NotificationChannel.Sms], "portal.link_sensitive");
        problem.ShouldContain("Give portal access at the front desk after checking that the number is the patient's own.");

        // The panel says why before anyone tries, to those who may send links.
        var links = await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id));
        links.CannotSend.ShouldNotBeNull().Code.ShouldBe("portal.link_sensitive");

        // Staff who may read the record but not send links are not told why: the reason would say that it is in a protected category.
        var asReader = await doctor.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id));
        asReader.CannotSend.ShouldBeNull("the doctor may read the record but not send links");
        asReader.Links.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_patient_without_a_mobile_number_or_whose_whatsapp_is_stopped_is_told_why()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (withoutPhone, _) = await RegisterAsync(frontOffice, "Kannan", withPhone: false);
        await ShouldBeRefusedAsync(frontOffice, withoutPhone.Id, [NotificationChannel.Sms], "portal.link_no_mobile");
        (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(withoutPhone.Id))).CannotSend.ShouldNotBeNull().Code.ShouldBe("portal.link_no_mobile");

        var (patient, phone) = await RegisterAsync(frontOffice, "Valli");
        await StopWhatsAppAsync(phone);
        (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id))).WhatsAppStopped.ShouldBeTrue();
        await ShouldBeRefusedAsync(frontOffice, patient.Id, [NotificationChannel.WhatsApp], "portal.link_whatsapp_stopped");

        var sent = await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id),
            new SendPortalLinkRequest([NotificationChannel.WhatsApp, NotificationChannel.Sms]));
        sent.Channels.ShouldBe([NotificationChannel.Sms]);
        sent.Notice.ShouldNotBeNull().ShouldContain("SMS only");
        (await LinkMessagesAsync(phone, NotificationService.SmsType)).ShouldHaveSingleItem();
        (await LinkMessagesAsync(phone, NotificationService.WhatsAppType)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_expired_or_unknown_link_gets_the_same_answer_as_any_other_that_does_not_work()
    {
        await using var address = await PortalAddressAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterAsync(frontOffice, "Ponni");
        var sent = await frontOffice.PostAsync<PortalLinkSent>(SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        var token = await LatestTokenAsync(phone);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<PortalLink>().IgnoreQueryFilters().Where(l => l.Id == sent.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        }

        var portal = server.AnonymousOnPortal(ownAddress: true);
        var expired = await ShouldNotWorkAsync(portal, token, "it expired");
        (await frontOffice.GetAsync<PatientPortalLinksDto>(ListPath(patient.Id))).Links.Single().State.ShouldBe(PortalLinkState.Expired);

        var unknown = await ShouldNotWorkAsync(portal, SecretTokens.NewOpaqueToken(16), "no such link");
        unknown.ShouldBe(expired, "an expired link and an unknown one read the same");
        using var malformed = await portal.SendRawAsync(HttpMethod.Get, PublicPath("not-a-token"));
        malformed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_staff_who_register_patients_send_links_and_only_once_the_portal_has_an_address()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, _) = await RegisterAsync(frontOffice, "Arumugam");

        // Without the portal's public address there is nothing to link to.
        await ShouldBeRefusedAsync(frontOffice, patient.Id, [NotificationChannel.Sms], "portal.link_no_address");

        await using var address = await PortalAddressAsync();
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        using var refused = await lab.SendRawAsync(HttpMethod.Post, SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Sms]));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var invalid = await frontOffice.SendRawAsync(HttpMethod.Post, SendPath(patient.Id), new SendPortalLinkRequest([NotificationChannel.Email]));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static string SendPath(Guid patientId) => $"/api/v1/patients/{patientId}/portal-link";

    private static string ListPath(Guid patientId) => $"/api/v1/patients/{patientId}/portal-links";

    private static string PublicPath(string token) => $"/api/v1/public/portal-links/{token}";

    /// <summary>
    /// The page and the code request both answer 404, with the same words, for a link that does not work. Returns the
    /// answer as the patient reads it (its title and detail; the trace id differs for every request).
    /// </summary>
    private static async Task<string> ShouldNotWorkAsync(ApiClient portal, string token, string why)
    {
        using var page = await portal.SendRawAsync(HttpMethod.Get, PublicPath(token));
        page.StatusCode.ShouldBe(HttpStatusCode.NotFound, why);
        using var code = await portal.SendRawAsync(HttpMethod.Post, PublicPath(token) + "/otp");
        code.StatusCode.ShouldBe(HttpStatusCode.NotFound, why);
        using var problem = JsonDocument.Parse(await page.Content.ReadAsStringAsync(Ct));
        var detail = problem.RootElement.GetProperty("detail").GetString();
        detail.ShouldBe("This link no longer works.");
        return $"{problem.RootElement.GetProperty("title").GetString()}: {detail}";
    }

    private static async Task<string> ShouldBeRefusedAsync(ApiClient staff, Guid patientId, NotificationChannel[] channels, string code)
    {
        using var response = await staff.SendRawAsync(HttpMethod.Post, SendPath(patientId), new SendPortalLinkRequest(channels));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        body.ShouldContain(code);
        return body;
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

    private async Task StopWhatsAppAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var optOut = new WhatsAppOptOut { Phone = Phone.Normalize(phone)! };
        optOut.Apply(optedOut: true, DateTimeOffset.UtcNow, "wamid.portal-link-test", null);
        db.Add(optOut);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<string> LatestTokenAsync(string phone)
    {
        var link = (await LinkMessagesAsync(phone, NotificationService.SmsType))[0].Values["link"];
        return link[(link.LastIndexOf('/') + 1)..];
    }

    // The test server sends no SMS or WhatsApp: the messages wait in the outbox, newest first.
    private async Task<List<NotificationDelivery>> LinkMessagesAsync(string phone, string type)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var to = Phone.Normalize(phone);
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == type)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == "portal.link" && d.To == to).ToList();
    }

    private async Task<List<AuditEvent>> AuditOfLinkAsync(Guid linkId, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var id = linkId.ToString();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => (e.EntityType == nameof(PortalLink) && e.EntityId == id) || (e.PatientId == patientId && e.Action.StartsWith("portal.link")))
            .ToListAsync(Ct);
    }

    private static async Task<(PatientCreated Patient, string Phone)> RegisterAsync(ApiClient frontOffice, string firstName, bool withPhone = true)
    {
        var phone = NewMobile();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Link", Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: withPhone ? phone : null, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return (patient, Phone.Normalize(phone)!);
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
