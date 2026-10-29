using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests.Documents;

/// <summary>
/// The printed rate list is in English and the local language (Clinical Establishments Rules 2012, r.9(i)): its own words
/// in both, each service's local name under its English name, and the amounts as the rate list worked them out.
/// </summary>
public sealed class RateListDocumentTests
{
    private const string Hostile = "<script>alert(1)</script>";

    private static readonly DocumentLetterhead Letterhead = new(
        "Nana Hospitals", null, "12 Anna Salai, Chennai, Tamil Nadu, 600002", null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Its_words_are_in_english_and_the_local_language()
    {
        var html = Render(PatientLanguages.Tamil);

        html.ShouldContain($"Rate list <span class=\"ta\" lang=\"ta\">/ {DocumentText.Tamil["doc.rates.title"]}</span>");
        html.ShouldContain($"Rates as on 29 Oct 2026 <span class=\"ta\" lang=\"ta\">/ {DocumentText.Tamil.Format("doc.rates.as_on", ("date", "29 Oct 2026"))}</span>");
        html.ShouldContain($"Laboratory tests <span class=\"ta\" lang=\"ta\">/ {DocumentText.Tamil["doc.rates.category.laboratory"]}</span>");
        html.ShouldContain("Complete blood count<span class=\"local\">முழு இரத்த எண்ணிக்கை</span>");

        // A list in Hindi has Hindi after the English.
        Render(PatientLanguages.Hindi).ShouldContain($"Rate list <span class=\"hi\" lang=\"hi\">/ {DocumentText.Hindi["doc.rates.title"]}</span>");
    }

    [Fact]
    public void Where_hc360_does_not_write_the_local_language_its_words_are_in_english_and_local_names_still_print()
    {
        var html = Render(language: null);

        html.ShouldContain("<h1 class=\"doc-title\">Rate list</h1>");
        html.ShouldNotContain("lang=\"ta\"");
        html.ShouldContain("Complete blood count<span class=\"local\">முழு இரத்த எண்ணிக்கை</span>");
    }

    [Fact]
    public void It_shows_what_the_patient_pays_and_marks_prices_the_counter_sets()
    {
        var html = Render(language: null);

        html.ShouldContain("<span>Registration of a new patient</span><b>₹100.00</b>");
        html.ShouldContain("<td class=\"num\">₹900.00</td>");
        html.ShouldContain("Free within 7 days");
        html.ShouldContain("Charged as a consultation");

        // A service with a GST shows its rate, its GST and what is paid with it.
        html.ShouldContain("<td class=\"num\">₹300.00</td><td class=\"num\">18%</td>");
        html.ShouldContain("₹354.00");

        // A service the counter prices by what is used has no amount of its own.
        html.ShouldContain("By what is used");
        html.ShouldContain("Medicines, consumables and implants are charged at no more than their MRP.");
        html.ShouldContain("* The billing counter may set a different price");

        // A room over ₹5,000 a day carries GST, and its multiplier is shown.
        html.ShouldContain("₹6,825.00<span class=\"local\">with 5% GST</span>");
        html.ShouldContain("×1.5");
    }

    [Fact]
    public void Names_are_printed_as_text()
    {
        var rates = Rates() with
        {
            Sections = [new RateSection(ServiceCategory.Laboratory, [new RateLine("LAB-X", Hostile, Hostile, 100m, 0m, 100m)])],
        };

        var html = RateListDocument.Build(rates, Letterhead, PatientLanguages.Tamil).ToHtml();

        html.ShouldNotContain(Hostile);
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void Its_file_and_title_name_the_day()
    {
        var document = RateListDocument.Build(Rates(), Letterhead, PatientLanguages.Tamil);

        document.FileName.ShouldBe("rate-list-2026-10-29");
        document.Title.ShouldBe("Rate list 29 Oct 2026");
    }

    [Fact]
    public void Every_category_listed_has_words_of_its_own() =>
        Enum.GetValues<ServiceCategory>()
            .Where(c => c is not (ServiceCategory.BedCharge or ServiceCategory.Package))
            .ShouldAllBe(c => RateListDocument.CategoryKey(c) != null);

    private static string Render(string? language) => RateListDocument.Build(Rates(), Letterhead, language).ToHtml();

    private static RateList Rates() => new(
        new DateOnly(2026, 10, 29),
        "General",
        0m,
        new RateLine("REG-NEW", "Registration fee (new patient)", null, 100m, 0m, 100m),
        [
            new DoctorFeeLine("Dr. Vikram Rao", "Cardiology", new RateLine("CONS-NEW", "Dr. Vikram Rao", null, 900m, 0m, 900m), null, 7),
            new DoctorFeeLine("Dr. Arjun Mehta", "General Medicine", new RateLine("CONS-NEW", "Dr. Arjun Mehta", null, 500m, 0m, 500m), null, 0),
        ],
        [
            new RoomRateLine(
                new RateLine("BED-DLX", "Bed charges — deluxe suite (per day)", null, 6_500m, 5m, 6_825m), false, ["Deluxe Suites"],
                new RateLine("NURS-DAY", "Nursing charges (per day)", null, 900m, 0m, 900m), new RateLine("VISIT-DR", "Consultant visit (in-patient)", null, 1_050m, 0m, 1_050m),
                1.5m),
        ],
        [
            new RateSection(ServiceCategory.Laboratory, [new RateLine("LAB-CBC", "Complete blood count", "முழு இரத்த எண்ணிக்கை", 350m, 0m, 350m)]),
            new RateSection(ServiceCategory.Consumable, [new RateLine("PHARM-MED", "Pharmacy — medicines (in-patient)", null, 0m, 0m, 0m, MayVary: true)]),
            new RateSection(ServiceCategory.Miscellaneous, [new RateLine("MISC-CERT", "Medical certificate", null, 300m, 18m, 354m)]),
        ],
        []);
}
