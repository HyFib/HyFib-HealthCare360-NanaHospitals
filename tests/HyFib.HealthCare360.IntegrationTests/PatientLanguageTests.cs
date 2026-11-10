using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Portal;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS PP-06 through the whole server: what the server writes for a patient is in their language. On the portal the
/// language the patient chose there (X-HC360-Language) wins; without one, the preferred language on the patient's
/// record, then Accept-Language, then English. A document sent with a message follows the record. The machine code
/// never changes, and staff answers stay in English.
/// </summary>
public sealed class PatientLanguageTests(HospitalServer server)
{
    private const string LanguageHeader = "X-HC360-Language";
    private const string OwnNumberHindi = "आपके अपने नंबर से पंजीकृत सभी लोग पहले से आपके परिवार में हैं।";
    private const string OwnNumberEnglish = "Everyone registered with your own number is already in your family.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_choice_in_the_portal_the_records_preference_wins_over_the_browsers_language()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, "Hindi");
        var portal = await server.SignInToPortalAsync(phone);
        portal.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");

        using var response = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", OwnNumber(patient, phone));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentLanguage.ShouldBe(["hi"]);
        var problem = await ProblemOf(response);
        problem.GetProperty("code").GetString().ShouldBe("portal.family_own_number");
        problem.GetProperty("title").GetString().ShouldBe("यह काम पूरा नहीं किया जा सकता");
        problem.GetProperty("detail").GetString().ShouldBe(OwnNumberHindi);
    }

    [Fact]
    public async Task The_language_chosen_in_the_portal_wins_over_the_records_preference_for_refusals_and_pdfs()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, "Hindi");
        var bill = await FinalizedBillAsync(patient.Id);
        var portal = await server.SignInToPortalAsync(phone);
        portal.Http.DefaultRequestHeaders.Add(LanguageHeader, "en");

        using (var refused = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", OwnNumber(patient, phone)))
        {
            refused.Content.Headers.ContentLanguage.ShouldBeEmpty();
            (await ProblemOf(refused)).GetProperty("detail").GetString().ShouldBe(OwnNumberEnglish);
        }

        portal.Http.DefaultRequestHeaders.Remove(LanguageHeader);
        (await PortalPdf.LanguageAsync(server, portal, $"/api/v1/portal/bills/{bill.Id}/pdf", chosen: "en")).ShouldBe("en");

        // The same patient without a choice in the request: the record's Hindi.
        (await PortalPdf.LanguageAsync(server, portal, $"/api/v1/portal/bills/{bill.Id}/pdf", chosen: null)).ShouldBe("hi");
    }

    [Fact]
    public async Task Without_a_choice_or_a_preference_the_browsers_language_decides_and_english_is_the_default()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, null);
        var portal = await server.SignInToPortalAsync(phone);

        portal.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("hi-IN");
        using (var hindi = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", OwnNumber(patient, phone)))
        {
            (await ProblemOf(hindi)).GetProperty("detail").GetString().ShouldBe(OwnNumberHindi);
        }

        portal.Http.DefaultRequestHeaders.AcceptLanguage.Clear();
        using (var english = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", OwnNumber(patient, phone)))
        {
            (await ProblemOf(english)).GetProperty("detail").GetString().ShouldBe(OwnNumberEnglish);
        }

        portal.Http.DefaultRequestHeaders.Add(LanguageHeader, "hi");
        using (var chosen = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", OwnNumber(patient, phone)))
        {
            (await ProblemOf(chosen)).GetProperty("detail").GetString().ShouldBe(OwnNumberHindi);
        }
    }

    [Fact]
    public async Task A_bill_sent_with_a_message_follows_the_records_preference()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (hindiPatient, _) = await RegisterPatientAsync(frontOffice, "हिन्दी");
        var (plainPatient, _) = await RegisterPatientAsync(frontOffice, null);
        var hindiBill = await FinalizedBillAsync(hindiPatient.Id);
        var plainBill = await FinalizedBillAsync(plainPatient.Id);

        var hindi = await SentDocumentAsync(hindiBill.Id);
        var plain = await SentDocumentAsync(plainBill.Id);

        hindi.ShouldContain("<html lang=\"hi\">");
        hindi.ShouldContain("आपूर्ति बिल (Bill of supply)");
        plain.ShouldContain("<html lang=\"en\">");
        plain.ShouldContain("<div class=\"h-title\">Bill of supply</div>");
    }

    [Fact]
    public async Task Staff_answers_stay_in_english_whatever_the_request_says()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, "Hindi");
        frontOffice.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("hi-IN");
        frontOffice.Http.DefaultRequestHeaders.Add(LanguageHeader, "hi");

        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{patient.Id}/portal-family",
            new LinkFamilyMemberRequest(phone, FamilyRelation.Parent, "Patient", true));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentLanguage.ShouldBeEmpty();
        (await ProblemOf(response)).GetProperty("detail").GetString()
            .ShouldBe("The patient is registered with this mobile number, so its portal account sees the record already.");
    }

    [Fact]
    public async Task A_refused_portal_request_reaches_the_patient_wholly_in_their_language()
    {
        // FluentValidation's own rules and the waitlist's business rules, through the real portal: every message in the
        // patient's script, with no field named in English and no English sentence left over.
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, null);
        var portal = await server.SignInToPortalAsync(phone);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);

        var rules = await RefusedAsync(portal, "ta", new PortalJoinWaitlistRequest(
            Guid.Empty, Guid.NewGuid(), today.AddDays(1), today.AddDays(2), (PreferredSession)99, false, new string('x', 501)));
        rules.GetProperty("title").GetString().ShouldBe("தயவுசெய்து குறிக்கப்பட்ட விவரங்களைச் சரிசெய்யுங்கள்.");
        FieldMessage(rules, "patientId").ShouldBe("இந்தத் தகவல் தேவை.");
        FieldMessage(rules, "preferredSession").ShouldBe("கொடுக்கப்பட்ட விருப்பங்களில் ஒன்றைத் தேர்ந்தெடுங்கள்.");
        FieldMessage(rules, "notes").ShouldBe("500 எழுத்துகளுக்கு மிகாமல் எழுதுங்கள். நீங்கள் 501 எழுத்துகள் எழுதியுள்ளீர்கள்.");

        var hindi = await RefusedAsync(portal, "hi", new PortalJoinWaitlistRequest(
            Guid.Empty, Guid.NewGuid(), today.AddDays(1), today.AddDays(2), PreferredSession.Any, false, null));
        FieldMessage(hindi, "patientId").ShouldBe("यह जानकारी ज़रूरी है।");

        var past = await RefusedAsync(portal, "hi", new PortalJoinWaitlistRequest(
            patient.Id, Guid.NewGuid(), today.AddDays(-1), today, PreferredSession.Any, false, null));
        FieldMessage(past, "preferredFrom").ShouldBe("आज की या उसके बाद की कोई तारीख चुनें।");

        var tooLong = await RefusedAsync(portal, "ta", new PortalJoinWaitlistRequest(
            patient.Id, Guid.NewGuid(), today.AddDays(1), today.AddDays(1 + WaitlistEntry.MaxPreferredDays), PreferredSession.Any, false, null));
        FieldMessage(tooLong, "preferredTo").ShouldBe($"காத்திருப்புப் பட்டியல் கோரிக்கை அதிகபட்சம் {WaitlistEntry.MaxPreferredDays} நாட்களுக்கு மட்டுமே இருக்க முடியும்.");

        // The same refusal for a reader of English keeps FluentValidation's own wording.
        var english = await RefusedAsync(portal, "en", new PortalJoinWaitlistRequest(
            Guid.Empty, Guid.NewGuid(), today.AddDays(1), today.AddDays(2), PreferredSession.Any, false, null));
        FieldMessage(english, "patientId").ShouldBe("'Patient Id' must not be empty.");
    }

    // A portal waitlist request refused in the language chosen: 400, said to be in that language and, for Hindi and
    // Tamil, no Latin letter in its title, detail or any field message (the machine code aside).
    private static async Task<JsonElement> RefusedAsync(ApiClient portal, string language, PortalJoinWaitlistRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/portal/waitlist") { Content = JsonContent.Create(body, options: ApiClient.Json) };
        request.Headers.Add(LanguageHeader, language);
        using var response = await portal.Http.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ProblemOf(response);
        if (language != "en")
        {
            response.Content.Headers.ContentLanguage.ShouldBe([language]);
            string?[] messages =
            [
                problem.GetProperty("title").GetString(),
                problem.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                .. problem.GetProperty("errors").EnumerateObject().SelectMany(field => field.Value.EnumerateArray().Select(message => message.GetString())),
            ];
            messages.OfType<string>().Where(message => message.Any(char.IsAsciiLetter)).ShouldBeEmpty("English in a refusal sent in " + language);
        }

        return problem;
    }

    private static string? FieldMessage(JsonElement problem, string field) => problem.GetProperty("errors").GetProperty(field)[0].GetString();

    private static RequestFamilyCodeRequest OwnNumber(PatientCreated patient, string phone) => new(patient.Uhid, phone, FamilyRelation.Parent);

    // The PDF the "bill shared" message would carry, as the document source makes it, with the PDF browser replaced by
    // one that keeps the page it was given.
    private async Task<string> SentDocumentAsync(Guid billId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var renderer = new KeepingRenderer();
        var source = ActivatorUtilities.CreateInstance<BillDocumentSource>(scope.ServiceProvider, (IPdfRenderer)renderer);

        (await source.CreateAsync(billId, Ct)).ShouldNotBeNull();
        return renderer.Html.ShouldNotBeNull();
    }

    private async Task<BillDetail> FinalizedBillAsync(Guid patientId)
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patientId, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    internal static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static async Task<(PatientCreated Patient, string Phone)> RegisterPatientAsync(ApiClient frontOffice, string? preferredLanguage)
    {
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Language", MiddleName: null, LastName: "Test" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))),
            Gender: Gender.Female, DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: preferredLanguage,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (patient, phone);
    }

    private sealed class KeepingRenderer : IPdfRenderer
    {
        public string? Html { get; private set; }

        public PdfRendererStatus Status { get; } = new(true, "test", null);

        public Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken)
        {
            Html = source.Html;
            return Task.FromResult("%PDF-1.7"u8.ToArray());
        }
    }
}

/// <summary>The language a portal PDF download is made in, for the tests of the portal's PDF endpoints (PP-06).</summary>
internal static class PortalPdf
{
    /// <summary>
    /// Downloads <paramref name="path"/> with the portal's language choice <paramref name="chosen"/> (none when null) and
    /// tells which language the document was made in: with a PDF browser on this machine, whether the PDF carries the
    /// Hindi font (an English document of a patient with a Latin name has no Devanagari in it); without one, the language
    /// of the "cannot make the PDF" answer, which the same request decides.
    /// </summary>
    public static async Task<string> LanguageAsync(HospitalServer server, ApiClient portal, string path, string? chosen)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (chosen is not null)
        {
            request.Headers.Add("X-HC360-Language", chosen);
        }

        using var response = await portal.Http.SendAsync(request, ct);
        if (!server.Services.GetRequiredService<IPdfRenderer>().Status.Available)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, $"GET {path}: {await response.Content.ReadAsStringAsync(ct)}");
            var title = (await PatientLanguageTests.ProblemOf(response)).GetProperty("title").GetString();
            return title == "PDF नहीं बन सका" ? "hi" : title == "The PDF could not be made" ? "en" : $"unexpected: {title}";
        }

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path}");
        var pdf = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync(ct));
        return pdf.Contains("NotoSansDevanagari", StringComparison.Ordinal) ? "hi" : "en";
    }
}
