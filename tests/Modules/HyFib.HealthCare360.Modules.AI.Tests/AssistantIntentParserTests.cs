using HyFib.HealthCare360.Modules.AI.Services.Assistant;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>The offline command rules. "Today" is Friday 25 September 2026.</summary>
public sealed class AssistantIntentParserTests
{
    private static readonly DateOnly Today = TestClock.Today;
    private static readonly Guid PatientId = Guid.Parse("0199a1b2-0000-7000-8000-000000000042");

    private static ParsedIntent Parse(string text, Guid? patientId = null) => AssistantIntentParser.Parse(text, patientId, Today);

    [Theory]
    [InlineData("open patient UHID NH26000123")]
    [InlineData("Hey HC360, open patient NH26000123 please.")]
    [InlineData("show uhid nh 2600 0123")]
    [InlineData("pull up the chart of patient number NH26000123")]
    public void Opens_a_patient_by_uhid(string text)
    {
        var intent = Parse(text);

        intent.Intent.ShouldBe(AssistantIntents.OpenPatient);
        intent.Parameters["uhid"].ShouldBe("NH26000123");
        intent.ConfirmationText.ShouldBe("Opening patient NH26000123.");
        intent.RequiresConfirmation.ShouldBeFalse();
    }

    [Fact]
    public void Open_patient_without_details_opens_the_patient_on_screen()
    {
        var intent = Parse("open patient", PatientId);

        intent.Intent.ShouldBe(AssistantIntents.OpenPatient);
        intent.Parameters["patientId"].ShouldBe(PatientId.ToString());
    }

    [Fact]
    public void Open_patient_without_details_or_context_asks_which_patient()
    {
        var intent = Parse("open patient");

        intent.Intent.ShouldBe(AssistantIntents.SearchPatient);
        intent.Parameters.ShouldBeEmpty();
        intent.ConfirmationText.ShouldBe("Which patient? Say the UHID, mobile number or name.");
    }

    [Fact]
    public void Opening_a_patient_by_name_searches_and_opens_a_single_match()
    {
        var intent = Parse("open patient Ravi Kumar");

        intent.Intent.ShouldBe(AssistantIntents.SearchPatient);
        intent.Parameters["query"].ShouldBe("Ravi Kumar");
        intent.Parameters["openIfSingleMatch"].ShouldBe("true");
        intent.ConfirmationText.ShouldBe("Searching patients for “Ravi Kumar”.");
    }

    [Theory]
    [InlineData("search patient 98765 43210")]
    [InlineData("find patient with mobile number +91 98765-43210")]
    [InlineData("9876543210")]
    public void Searches_patients_by_mobile_number(string text)
    {
        var intent = Parse(text);

        intent.Intent.ShouldBe(AssistantIntents.SearchPatient);
        intent.Parameters["phone"].ShouldBe("+919876543210");
        intent.Parameters.ContainsKey("openIfSingleMatch").ShouldBeFalse();
        intent.ConfirmationText.ShouldBe("Searching patients with mobile number +919876543210.");
    }

    [Fact]
    public void Searches_patients_by_name() =>
        Parse("find patients named Priya Sharma").Parameters["query"].ShouldBe("Priya Sharma");

    [Theory]
    [InlineData("today's appointments", "2026-09-25", null, "Showing today's appointments.")]
    [InlineData("show my appointments for tomorrow", "2026-09-26", "mine", "Showing tomorrow's appointments.")]
    [InlineData("what were yesterday's appointments", "2026-09-24", null, "Showing yesterday's appointments.")]
    [InlineData("appointments on Monday", "2026-09-28", null, "Showing appointments for Mon, 28 Sep 2026.")]
    [InlineData("appointments next Friday", "2026-10-02", null, "Showing appointments for Fri, 2 Oct 2026.")]
    [InlineData("appointments on 3/10", "2026-10-03", null, "Showing appointments for Sat, 3 Oct 2026.")]
    [InlineData("show all appointments on 5th October", "2026-10-05", "all", "Showing appointments for Mon, 5 Oct 2026.")]
    [InlineData("show appointments", "2026-09-25", null, "Showing today's appointments.")]
    [InlineData("review tomorrow's appointments", "2026-09-26", null, "Showing tomorrow's appointments.")]
    public void Lists_appointments_for_a_spoken_day(string text, string date, string? scope, string confirmation)
    {
        var intent = Parse(text);

        intent.Intent.ShouldBe(AssistantIntents.ListAppointments);
        intent.Parameters["date"].ShouldBe(date);
        intent.Parameters.GetValueOrDefault("scope").ShouldBe(scope);
        intent.ConfirmationText.ShouldBe(confirmation);
    }

    [Fact]
    public void Books_a_follow_up_after_confirmation()
    {
        var intent = Parse("book follow-up in 2 weeks", PatientId);

        intent.Intent.ShouldBe(AssistantIntents.BookFollowUp);
        intent.RequiresConfirmation.ShouldBeTrue();
        intent.Parameters.ShouldBe(new Dictionary<string, string>
        {
            ["days"] = "14",
            ["date"] = "2026-10-09",
            ["interval"] = "2 weeks",
            ["patientId"] = PatientId.ToString(),
        });
        intent.ConfirmationText.ShouldBe("Book a follow-up in 2 weeks, on Fri, 9 Oct 2026?");
    }

    [Theory]
    [InlineData("follow up after three months", "91", "3 months", "Fri, 25 Dec 2026")]
    [InlineData("please schedule a follow-up in a couple of weeks", "14", "2 weeks", "Fri, 9 Oct 2026")]
    [InlineData("review after a fortnight", "14", "2 weeks", "Fri, 9 Oct 2026")]
    [InlineData("follow up next month", "30", "1 month", "Sun, 25 Oct 2026")]
    [InlineData("revisit in twenty one days", "21", "21 days", "Fri, 16 Oct 2026")]
    public void Understands_spoken_follow_up_intervals(string text, string days, string interval, string when)
    {
        var intent = Parse(text);

        intent.Intent.ShouldBe(AssistantIntents.BookFollowUp);
        intent.Parameters["days"].ShouldBe(days);
        intent.Parameters["interval"].ShouldBe(interval);
        intent.ConfirmationText.ShouldBe($"Book a follow-up in {interval}, on {when}? Open the patient first.");
    }

    [Fact]
    public void Asks_when_a_follow_up_has_no_interval()
    {
        var intent = Parse("schedule follow up", PatientId);

        intent.Intent.ShouldBe(AssistantIntents.BookFollowUp);
        intent.Parameters.Keys.ShouldBe(["patientId"]);
        intent.ConfirmationText.ShouldBe("When should the follow-up be? For example “follow-up in 2 weeks”.");
    }

    [Theory]
    [InlineData("follow-up list")]
    [InlineData("follow up in 400 days")]
    [InlineData("what is the weather")]
    [InlineData("")]
    [InlineData("   please   ")]
    public void Does_not_guess_at_commands_it_does_not_understand(string text) =>
        Parse(text).Intent.ShouldBe(AssistantIntents.Unknown);

    [Theory]
    [InlineData("start dictation")]
    [InlineData("begin voice dictation")]
    [InlineData("dictate")]
    [InlineData("OK assistant, start dictating now")]
    public void Starts_dictation(string text)
    {
        var intent = Parse(text, PatientId);

        intent.Intent.ShouldBe(AssistantIntents.StartDictation);
        intent.Parameters["patientId"].ShouldBe(PatientId.ToString());
        intent.ConfirmationText.ShouldBe("Starting dictation.");
    }

    [Fact]
    public void Creates_a_typed_note_with_dictated_text()
    {
        var intent = Parse("create a progress note: patient improving, afebrile", PatientId);

        intent.Intent.ShouldBe(AssistantIntents.CreateNote);
        intent.RequiresConfirmation.ShouldBeTrue();
        intent.Parameters["noteType"].ShouldBe("progress");
        intent.Parameters["text"].ShouldBe("patient improving, afebrile");
        intent.ConfirmationText.ShouldBe("Create a progress note?");
    }

    [Fact]
    public void A_note_prefix_creates_a_clinical_note()
    {
        var intent = Parse("note: BP stable");

        intent.Intent.ShouldBe(AssistantIntents.CreateNote);
        intent.Parameters["text"].ShouldBe("BP stable");
        intent.ConfirmationText.ShouldBe("Create a clinical note? Open the patient first.");
    }

    [Theory]
    [InlineData("go to billing", "/billing", "Billing")]
    [InlineData("take me to the bed board", "/inpatient/beds", "Bed board")]
    [InlineData("open patient list", "/patients", "Patients")]
    [InlineData("pharmacy", "/pharmacy", "Pharmacy")]
    [InlineData("Show me the lab page", "/lab", "Laboratory")]
    public void Navigates_to_known_screens(string text, string route, string label)
    {
        var intent = Parse(text);

        intent.Intent.ShouldBe(AssistantIntents.Navigate);
        intent.Parameters["route"].ShouldBe(route);
        intent.ConfirmationText.ShouldBe($"Opening {label}.");
    }

    [Theory]
    [InlineData("  Hey   HC 360,  please open billing, thanks! ", "open billing")]
    [InlineData("could you show today's appointments for me", "show today's appointments")]
    [InlineData("Assistant: go to pharmacy.", "go to pharmacy")]
    public void Strips_wake_words_and_politeness(string text, string expected) =>
        AssistantIntentParser.Normalize(text).ShouldBe(expected);

    [Theory]
    [InlineData("in 10 days", 10, "10 days")]
    [InlineData("after 3 weeks", 21, "3 weeks")]
    [InlineData("in a couple of weeks", 14, "2 weeks")]
    [InlineData("next week", 7, "1 week")]
    [InlineData("day after tomorrow", 2, "2 days")]
    [InlineData("tomorrow", 1, "1 day")]
    public void Parses_intervals(string text, int days, string label) =>
        AssistantIntentParser.ParseInterval(text, Today).ShouldBe((days, label));

    [Theory]
    [InlineData("on 31/02")]
    [InlineData("sometime soon")]
    public void Ignores_impossible_or_missing_dates(string text) => AssistantIntentParser.ParseDate(text, Today).ShouldBeNull();

    [Theory]
    [InlineData("25-12-26", "2026-12-25")]
    [InlineData("1st jan 2027", "2027-01-01")]
    [InlineData("this sunday", "2026-09-27")]
    [InlineData("friday", "2026-09-25")]
    public void Reads_dates_in_indian_day_month_order(string text, string expected) =>
        AssistantIntentParser.ParseDate(text, Today).ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(1, "1 day")]
    [InlineData(10, "10 days")]
    [InlineData(7, "1 week")]
    [InlineData(28, "4 weeks")]
    public void Describes_day_counts(int days, string expected) => AssistantIntentParser.DescribeDays(days).ShouldBe(expected);
}
