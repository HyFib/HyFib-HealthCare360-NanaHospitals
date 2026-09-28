using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>The model's structured answer is untrusted input; only known intents with plausible values get through.</summary>
public sealed class AssistantAiInterpreterTests
{
    private static readonly DateOnly Today = TestClock.Today;
    private static readonly Guid PatientId = Guid.Parse("0199a1b2-0000-7000-8000-000000000042");

    /// <summary>What the user said, redacted as it was before being sent: [UHID_1] and [PHONE_1] map back to real values.</summary>
    private static readonly RedactionResult Redaction = PhiRedactor.Redact("the chart for uhid NH26000123, mobile 9876543210");

    private static ParsedIntent? Interpret(string json, Guid? patientId = null) => AssistantAiInterpreter.TryParse(json, Redaction, Today, patientId);

    private static string Answer(string intent, string route = "", string query = "", string uhid = "", string phone = "", string date = "", int followUpDays = 0, string noteText = "") =>
        $$"""{"intent":"{{intent}}","route":"{{route}}","query":"{{query}}","uhid":"{{uhid}}","phone":"{{phone}}","date":"{{date}}","followUpDays":{{followUpDays}},"noteText":"{{noteText}}"}""";

    [Fact]
    public void Restores_a_redacted_uhid_the_model_copied_back()
    {
        var intent = Interpret(Answer("open_patient", uhid: "[UHID_1]")).ShouldNotBeNull();

        intent.Intent.ShouldBe(AssistantIntents.OpenPatient);
        intent.Parameters["uhid"].ShouldBe("NH26000123");
        intent.ConfirmationText.ShouldBe("Opening patient NH26000123.");
    }

    [Fact]
    public void Opens_the_patient_on_screen_when_no_uhid_was_given()
    {
        Interpret(Answer("open_patient"), PatientId).ShouldNotBeNull().Parameters["patientId"].ShouldBe(PatientId.ToString());
        Interpret(Answer("open_patient")).ShouldBeNull();
    }

    [Fact]
    public void Restores_and_normalises_a_redacted_phone_number()
    {
        var intent = Interpret(Answer("search_patient", phone: "[PHONE_1]")).ShouldNotBeNull();

        intent.Intent.ShouldBe(AssistantIntents.SearchPatient);
        intent.Parameters["phone"].ShouldBe("+919876543210");
    }

    [Fact]
    public void A_search_needs_something_to_search_for() => Interpret(Answer("search_patient")).ShouldBeNull();

    [Theory]
    [InlineData("/billing", "Billing")]
    [InlineData("/inpatient/beds", "Bed board")]
    public void Navigates_only_to_real_screens(string route, string label) =>
        Interpret(Answer("navigate", route: route)).ShouldNotBeNull().ConfirmationText.ShouldBe($"Opening {label}.");

    [Theory]
    [InlineData("/admin/users/delete")]
    [InlineData("https://evil.example")]
    [InlineData("")]
    public void Refuses_routes_that_are_not_known_screens(string route) => Interpret(Answer("navigate", route: route)).ShouldBeNull();

    [Theory]
    [InlineData("2026-09-28", "2026-09-28")]
    [InlineData("2031-01-01", "2026-09-25")]
    [InlineData("next monday", "2026-09-25")]
    public void Keeps_appointment_dates_plausible(string date, string expected) =>
        Interpret(Answer("list_appointments", date: date)).ShouldNotBeNull().Parameters["date"].ShouldBe(expected);

    [Fact]
    public void Books_follow_ups_with_a_locally_written_confirmation()
    {
        var intent = Interpret(Answer("book_follow_up", followUpDays: 10), PatientId).ShouldNotBeNull();

        intent.Parameters["days"].ShouldBe("10");
        intent.RequiresConfirmation.ShouldBeTrue();
        intent.ConfirmationText.ShouldBe("Book a follow-up in 10 days, on Mon, 5 Oct 2026?");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(400)]
    public void Implausible_follow_up_intervals_become_a_question(int days) =>
        Interpret(Answer("book_follow_up", followUpDays: days), PatientId).ShouldNotBeNull().Parameters.ContainsKey("days").ShouldBeFalse();

    [Fact]
    public void Reads_note_dictation_and_simple_intents()
    {
        Interpret(Answer("create_note", noteText: "Patient comfortable"), PatientId).ShouldNotBeNull().Parameters["text"].ShouldBe("Patient comfortable");
        Interpret(Answer("start_dictation")).ShouldNotBeNull().Intent.ShouldBe(AssistantIntents.StartDictation);
        Interpret(Answer("unknown")).ShouldNotBeNull().Intent.ShouldBe(AssistantIntents.Unknown);
    }

    [Fact]
    public void Never_uses_confirmation_text_supplied_by_the_model()
    {
        const string json = """{"intent":"navigate","route":"/billing","confirmationText":"All records deleted.","requiresConfirmation":false}""";

        Interpret(json).ShouldNotBeNull().ConfirmationText.ShouldBe("Opening Billing.");
    }

    [Theory]
    [InlineData("""{"intent":"delete_patient","uhid":"[UHID_1]"}""")]
    [InlineData("""{"intent":42}""")]
    [InlineData("""["navigate","/billing"]""")]
    [InlineData("Sure! I will open billing.")]
    public void Rejects_anything_that_is_not_a_known_intent(string json) => Interpret(json).ShouldBeNull();
}
