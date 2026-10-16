using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS PP-06 on the server: the text HC360 writes for patients (portal error messages and the documents they receive)
/// is in the patient's language. The language is the signed-in patient's preference, else Accept-Language, else English;
/// staff requests stay in English; every text exists in every language.
/// </summary>
public sealed class PatientLocalizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------- which language

    [Theory]
    [InlineData("hi", "hi")]
    [InlineData("hi-IN", "hi")]
    [InlineData("Hindi", "hi")]
    [InlineData("  HINDI ", "hi")]
    [InlineData("हिन्दी", "hi")]
    [InlineData("हिंदी", "hi")]
    [InlineData("English", "en")]
    [InlineData("English (India)", "en")]
    [InlineData("en", "en")]
    [InlineData("Marathi", null)]
    [InlineData("hindustani", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_records_free_text_preference_names_a_language_hc360_writes_or_none(string? preference, string? language) =>
        PatientLanguages.FromPreference(preference).ShouldBe(language);

    [Theory]
    [InlineData("hi-IN,hi;q=0.9,en;q=0.8", "hi")]
    [InlineData("en-US,en;q=0.9,hi;q=0.8", "en")]
    [InlineData("mr-IN,hi;q=0.5,en;q=0.3", "hi")]
    [InlineData("en;q=0.2, hi;q=0.7", "hi")]
    [InlineData("fr-FR,de", null)]
    [InlineData("*", null)]
    [InlineData("hi;q=0", null)]
    [InlineData(";;;", null)]
    public void Accept_language_picks_the_most_preferred_language_hc360_writes(string header, string? language) =>
        PatientLanguages.FromAcceptLanguage([header]).ShouldBe(language);

    [Fact]
    public async Task A_signed_in_patients_preference_wins_over_the_browsers_language()
    {
        var patient = Guid.NewGuid();

        var language = await LanguageOfAsync("/api/v1/portal/bills", Patient(patient), acceptLanguage: "en-US,en", preferences: new() { [patient] = "Hindi" });

        language.ShouldBe("hi");
    }

    [Fact]
    public async Task Without_a_preference_hc360_writes_the_patients_accept_language_decides()
    {
        var patient = Guid.NewGuid();

        (await LanguageOfAsync("/api/v1/portal/bills", Patient(patient), acceptLanguage: "hi-IN,hi;q=0.9", preferences: new() { [patient] = null }))
            .ShouldBe("hi");
        (await LanguageOfAsync("/api/v1/portal/bills", Patient(patient), acceptLanguage: "hi-IN", preferences: new() { [patient] = "Marathi" }))
            .ShouldBe("hi", "a language HC360 does not write is no preference");
    }

    [Fact]
    public async Task With_neither_the_answer_is_in_english()
    {
        (await LanguageOfAsync("/api/v1/portal/bills", Patient(Guid.NewGuid()), acceptLanguage: null)).ShouldBe("en");
        (await LanguageOfAsync("/api/v1/portal/bills", Patient(Guid.NewGuid()), acceptLanguage: "ta-IN")).ShouldBe("en");
    }

    [Theory]
    [InlineData("/api/v1/portal/appointments", null)]
    [InlineData("/api/v1/public/doctors", null)]
    [InlineData("/api/v1/auth/otp/request", null)]
    [InlineData("/api/v1/system/info", "portal")]
    public async Task Portal_public_and_otp_requests_follow_the_header_before_sign_in(string path, string? client) =>
        (await LanguageOfAsync(path, Anonymous(), acceptLanguage: "hi", client: client)).ShouldBe("hi");

    [Fact]
    public async Task Staff_requests_stay_in_english_whatever_the_browser_says()
    {
        var feature = await FeatureOfAsync("/api/v1/patients", Staff(), acceptLanguage: "hi-IN,hi", client: "web");

        feature.ShouldBeNull();
    }

    [Fact]
    public async Task The_preference_is_read_once_and_kept_for_a_few_minutes()
    {
        var patient = Guid.NewGuid();
        var preferences = new FakePreferences { [patient] = "Hindi" };
        using var cache = new MemoryCache(new MemoryCacheOptions());

        await LanguageOfAsync("/api/v1/portal/bills", Patient(patient), null, preferences, cache);
        await LanguageOfAsync("/api/v1/portal/bills", Patient(patient), null, preferences, cache);

        preferences.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task The_request_runs_with_the_patients_ui_culture_and_gets_its_own_back_after()
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo? during = null;

        await RunAsync("/api/v1/portal/bills", Anonymous(), "hi", null, new FakePreferences(), null, _ => during = CultureInfo.CurrentUICulture);

        during!.Name.ShouldBe("hi-IN");
        CultureInfo.CurrentUICulture.ShouldBe(before);
    }

    [Fact]
    public async Task The_validators_built_in_messages_follow_the_patients_ui_culture()
    {
        string? message = null;

        await RunAsync("/api/v1/portal/bills", Anonymous(), "hi", null, new FakePreferences(), null,
            _ => message = new InlineValidator<Note> { v => v.RuleFor(n => n.Reason).NotEmpty() }.Validate(new Note("")).Errors[0].ErrorMessage);

        DocumentFonts.HasDevanagari(message).ShouldBeTrue(message);
    }

    // ---------------- Problem Details

    [Fact]
    public async Task A_refusal_on_the_portal_reaches_a_hindi_reader_in_hindi_with_its_code_unchanged()
    {
        var response = await ProblemAsync(Error.Conflict("appointment.slot_full", "This slot has just been booked. Choose another slot."), "hi");
        var problem = await BodyOf(response);

        problem.GetProperty("title").GetString().ShouldBe("यह अनुरोध पूरा नहीं हो सका");
        problem.GetProperty("detail").GetString().ShouldBe("यह समय अभी-अभी बुक हो गया है। कृपया कोई दूसरा समय चुनें।");
        problem.GetProperty("code").GetString().ShouldBe("appointment.slot_full");
        response.Headers.ContentLanguage.ToString().ShouldBe("hi");
    }

    [Fact]
    public async Task A_message_with_a_name_in_it_keeps_the_name()
    {
        var problem = await BodyOf(await ProblemAsync(
            Error.Rule("appointment.online_unavailable", "Dr Meera Shah is not available for online booking. Please call the hospital."), "hi"));

        problem.GetProperty("detail").GetString().ShouldBe("Dr Meera Shah के साथ ऑनलाइन बुकिंग उपलब्ध नहीं है। कृपया अस्पताल को फ़ोन करें।");
    }

    [Fact]
    public async Task Field_messages_are_translated_and_field_names_kept()
    {
        var problem = await BodyOf(await ProblemAsync(Error.Field("phone", "Enter a valid 10-digit mobile number."), "hi"));

        problem.GetProperty("title").GetString().ShouldBe("कृपया चिह्नित जानकारी ठीक करें।");
        problem.GetProperty("errors").GetProperty("phone")[0].GetString().ShouldBe("सही 10 अंकों का मोबाइल नंबर दर्ज करें।");
        problem.GetProperty("code").GetString().ShouldBe("validation");
    }

    [Fact]
    public async Task A_message_without_a_translation_is_sent_as_written()
    {
        var problem = await BodyOf(await ProblemAsync(Error.Rule("something.new", "A brand-new rule nobody translated yet."), "hi"));

        problem.GetProperty("title").GetString().ShouldBe("यह काम पूरा नहीं किया जा सकता");
        problem.GetProperty("detail").GetString().ShouldBe("A brand-new rule nobody translated yet.");
    }

    [Fact]
    public async Task English_and_staff_answers_are_left_alone()
    {
        var english = await BodyOf(await ProblemAsync(Error.Conflict("appointment.slot_full", "This slot has just been booked. Choose another slot."), "en"));
        var staffResponse = await ProblemAsync(Error.Conflict("appointment.slot_full", "This slot has just been booked. Choose another slot."), null);
        var staff = await BodyOf(staffResponse);

        english.GetProperty("detail").GetString().ShouldBe("This slot has just been booked. Choose another slot.");
        staff.GetProperty("title").GetString().ShouldBe("Conflict");
        staffResponse.Headers.ContentLanguage.ToString().ShouldBeEmpty();
    }

    // ---------------- the texts

    [Fact]
    public void Every_text_exists_in_every_language_with_the_same_placeholders()
    {
        var texts = PatientTexts.Catalog.Languages;
        var english = texts[PatientLanguages.English];

        texts.Keys.ShouldBe(PatientLanguages.Codes, ignoreOrder: true);
        english.Count.ShouldBeGreaterThan(200);
        foreach (var (language, set) in texts)
        {
            set.Keys.Order(StringComparer.Ordinal).ShouldBe(english.Keys.Order(StringComparer.Ordinal), $"{language} must have exactly the English keys");
            foreach (var (key, text) in set)
            {
                text.Trim().ShouldNotBeEmpty($"{language}:{key}");
                var placeholders = PatientTexts.Placeholders(text).Order(StringComparer.Ordinal).ToList();
                var englishPlaceholders = PatientTexts.Placeholders(english[key]).Order(StringComparer.Ordinal).ToList();
                if (key.StartsWith("message.", StringComparison.Ordinal))
                {
                    // A message may leave out a value it cannot use (an English record name such as "Prescription").
                    placeholders.Except(englishPlaceholders).ShouldBeEmpty($"{language}:{key}");
                }
                else
                {
                    placeholders.ShouldBe(englishPlaceholders, $"{language}:{key}");
                }
            }
        }
    }

    [Fact]
    public void The_hindi_texts_are_written_in_devanagari()
    {
        var hindi = PatientTexts.Catalog.Languages[PatientLanguages.Hindi];

        hindi.Where(t => !DocumentFonts.HasDevanagari(t.Value)).Select(t => t.Key).ShouldBe(
            ["doc.bill.mode.upi"], ignoreOrder: true, "only a name that is the same in both languages may stay in Latin letters");
    }

    [Fact]
    public void Each_server_message_has_one_entry_so_a_message_never_matches_two()
    {
        var messages = PatientTexts.Catalog.Languages[PatientLanguages.English].Where(t => t.Key.StartsWith("message.", StringComparison.Ordinal)).ToList();

        messages.Select(m => m.Value).Distinct(StringComparer.Ordinal).Count().ShouldBe(messages.Count);
    }

    [Fact]
    public void Text_falls_back_to_english_and_an_unknown_key_is_a_mistake()
    {
        var texts = new PatientTexts(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["doc.a"] = "Patient {name}", ["doc.b"] = "Only English" },
            ["hi"] = new Dictionary<string, string> { ["doc.a"] = "रोगी {name}" },
        });

        texts.Text("hi", "doc.a", ("name", "Asha")).ShouldBe("रोगी Asha");
        texts.Text("hi", "doc.b").ShouldBe("Only English");
        texts.Text("ta", "doc.a", ("name", "Asha")).ShouldBe("Patient Asha");
        Should.Throw<KeyNotFoundException>(() => texts.Text("en", "doc.missing"));
        DocumentText.Hindi["doc.patient"].ShouldBe("रोगी");
        DocumentText.For("ta").ShouldBeSameAs(DocumentText.English);
    }

    [Fact]
    public void Messages_are_matched_on_their_whole_english_text()
    {
        var catalog = PatientTexts.Catalog;

        catalog.TranslateMessage("hi", "Not found").ShouldBe("नहीं मिला");
        catalog.TranslateMessage("hi", "Bill 'NHOB1' was not found.").ShouldBe("माँगी गई जानकारी नहीं मिली।");
        catalog.TranslateMessage("hi", "Not found at all").ShouldBeNull();
        catalog.TranslateMessage("en", "Not found").ShouldBeNull();
        catalog.TranslateMessage("hi", null).ShouldBeNull();
    }

    // ---------------- fonts

    [Fact]
    public void A_document_with_hindi_in_it_carries_the_hindi_font_and_an_english_one_does_not()
    {
        PrintableDocument Document(string body) => new() { Title = "Bill", FileName = "bill", Body = Html.Text(body) };

        var hindi = Document("रोगी: Asha Rao").ToHtml();
        var english = Document("Patient: Asha Rao").ToHtml();
        var hindiName = new PrintableDocument { Title = "Bill", FileName = "bill", Body = Html.Text("Patient"), Footer = "आशा राव · UHID NH-1" }.ToHtml();

        hindi.ShouldContain("@font-face { font-family: \"HC360 Devanagari\"; font-style: normal; font-weight: 400; font-display: swap; unicode-range: U+0900-097F");
        hindi.ShouldContain("format(\"woff2\")");
        hindi.ShouldContain("font-src data:");
        english.ShouldNotContain("@font-face");
        hindiName.ShouldContain("@font-face", Case.Sensitive, "a name typed in Devanagari in the running footer needs the font too");
    }

    [Fact]
    public void The_page_numbers_are_in_the_documents_language()
    {
        var document = new PrintableDocument { Title = "Bill", FileName = "bill", Body = Html.Text("रोगी") };

        document.ToHtml().ShouldContain("@bottom-right { content: \"Page \" counter(page) \" of \" counter(pages);");
        (document with { Language = "hi" }).ToHtml().ShouldContain("@bottom-right { content: \"पृष्ठ \" counter(page) \" / \" counter(pages);");
    }

    // ---------------- helpers

    private static async Task<HttpResponse> ProblemAsync(Error error, string? language)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddProblemDetails(problems => problems.CustomizeProblemDetails = ProblemLocalization.Localize)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Accept = "application/json";
        http.Response.Body = new MemoryStream();
        if (language is not null)
        {
            http.Features.Set(new RequestLanguage(language));
        }

        await error.ToProblem().ExecuteAsync(http);
        return http.Response;
    }

    private static async Task<JsonElement> BodyOf(HttpResponse response)
    {
        response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(response.Body, cancellationToken: Ct);
        return document.RootElement.Clone();
    }

    private static async Task<string> LanguageOfAsync(
        string path, ClaimsPrincipal user, string? acceptLanguage, FakePreferences? preferences = null, IMemoryCache? cache = null, string? client = null) =>
        (await FeatureOfAsync(path, user, acceptLanguage, client, preferences, cache))?.Code ?? "(none)";

    private static async Task<RequestLanguage?> FeatureOfAsync(
        string path, ClaimsPrincipal user, string? acceptLanguage, string? client = null, FakePreferences? preferences = null, IMemoryCache? cache = null)
    {
        RequestLanguage? seen = null;
        await RunAsync(path, user, acceptLanguage, client, preferences ?? new FakePreferences(), cache, http => seen = http.Features.Get<RequestLanguage>());
        return seen;
    }

    private static async Task RunAsync(
        string path, ClaimsPrincipal user, string? acceptLanguage, string? client, FakePreferences preferences, IMemoryCache? cache, Action<HttpContext> handler)
    {
        using var ownCache = cache is null ? new MemoryCache(new MemoryCacheOptions()) : null;
        var http = new DefaultHttpContext { User = user };
        http.Request.Path = path;
        if (acceptLanguage is not null)
        {
            http.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        if (client is not null)
        {
            http.Request.Headers["X-HC360-Client"] = client;
        }

        var middleware = new RequestLanguageMiddleware(context =>
        {
            handler(context);
            return Task.CompletedTask;
        }, cache ?? ownCache!);
        await middleware.InvokeAsync(http, preferences);
    }

    private static ClaimsPrincipal Patient(Guid patientId) => new(new ClaimsIdentity(
        [new(HcClaims.UserId, Guid.NewGuid().ToString()), new(HcClaims.UserType, HcClaims.PatientType), new(HcClaims.Patient, patientId.ToString())], "test"));

    private static ClaimsPrincipal Staff() => new(new ClaimsIdentity(
        [new(HcClaims.UserId, Guid.NewGuid().ToString()), new(HcClaims.UserType, HcClaims.StaffType)], "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    public sealed record Note(string Reason);

    private sealed class FakePreferences : Dictionary<Guid, string?>, IPatientLanguageSource
    {
        public int Reads { get; private set; }

        public Task<string?> PreferredLanguageAsync(Guid patientId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(TryGetValue(patientId, out var preference) ? preference : null);
        }
    }
}
