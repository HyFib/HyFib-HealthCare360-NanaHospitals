using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Documents;

/// <summary>The A5 prescription the hospital prints, the portal downloads and WhatsApp sends: one document for all three.</summary>
public sealed class PrescriptionDocumentTests
{
    private const string Token = "Zk3_9aB-qR7tLm2xWv5cAbCd";
    private const string Hostile = "<script>alert(1)</script>";

    // 10:15 UTC is 3:45 pm in India.
    private static readonly DateTimeOffset Signed = new(2026, 9, 25, 10, 15, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_prescription_is_an_a5_page_named_after_its_number_with_the_patient_in_the_footer()
    {
        var document = PrescriptionDocument.Build(Model(), null, null);
        var html = document.ToHtml();

        document.PageSize.ShouldBe(DocumentPageSize.A5);
        document.FileName.ShouldBe("prescription-NHRX260000123");
        document.Title.ShouldBe("Prescription NHRX260000123");
        document.Footer.ShouldBe("Asha Rao · UHID NH-000123 · Prescription NHRX260000123");
        document.Language.ShouldBe("en");
        html.ShouldContain("size: A5");
        html.ShouldContain("@bottom-left { content: \"Asha Rao · UHID NH-000123 · Prescription NHRX260000123\"");
    }

    [Fact]
    public void Each_medicine_keeps_its_hindi_directions_as_text_under_the_english_ones()
    {
        var body = PrescriptionDocument.Body(Model(), null, null).ToString();

        body.ShouldContain("<div class=\"drug\">Metformin 500 mg</div>");
        body.ShouldContain("<div class=\"small\">Metformin hydrochloride · Tablet</div>");
        body.ShouldContain("<div>1 tab · Twice a day</div>");
        body.ShouldContain("<div class=\"hindi\" lang=\"hi\">1 tab · दिन में दो बार</div>");
        body.ShouldContain("<div class=\"small\">After food</div>");
        body.IndexOf("Twice a day", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("दिन में दो बार", StringComparison.Ordinal));
    }

    [Fact]
    public void Controlled_drugs_are_tagged_and_a_route_other_than_oral_is_named()
    {
        var body = PrescriptionDocument.Body(Model(), null, null).ToString();

        body.ShouldContain("<div class=\"drug\">Tramadol 50 mg<span class=\"controlled\">Schedule H1/X</span></div>");
        body.ShouldContain("<div class=\"drug\">Ceftriaxone 1 g</div>");
        body.ShouldContain("<div>1 g · Immediately, once · IV</div>");
        body.ShouldNotContain("· Oral");
    }

    [Fact]
    public void A_stat_dose_reads_once_and_quantities_print_without_trailing_zeros()
    {
        var model = Model();
        var body = PrescriptionDocument.Body(model with { Lines = [.. model.Lines, model.Lines[0] with { Sequence = 4, Quantity = 1.50m }] }, null, null)
            .ToString();

        body.ShouldContain("<td>30 days</td>");
        body.ShouldContain("<td>Once</td>");
        body.ShouldContain("<td class=\"right\">60</td>");
        body.ShouldContain("<td class=\"right\">1.5</td>");
        body.ShouldContain("<td>4.</td>");
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 day")]
    [InlineData(5, "5 days")]
    [InlineData(7, "7 days")]
    [InlineData(14, "2 weeks")]
    [InlineData(21, "3 weeks")]
    [InlineData(30, "30 days")]
    public void Durations_read_in_days_or_whole_weeks(int days, string text) => PrescriptionDocument.DurationText(days).ShouldBe(text);

    [Fact]
    public void The_letterhead_carries_the_facility_and_the_prescribers_registration_on_the_right()
    {
        var body = PrescriptionDocument.Body(Model(), null, null).ToString();

        body.ShouldContain("<div class=\"facility\">Nana Hospitals</div>");
        body.ShouldContain("<div class=\"small\">12 MG Road, Pune</div>");
        body.ShouldContain("<div class=\"small\">020 4000 1000 · care@nana.in · nana.in</div>");
        body.ShouldContain("<div class=\"small\">Reg. MH-PUN-1234 · NABH H-2021-0456</div>");
        body.ShouldContain("<div class=\"small\">Accredited by NABH</div>");
        body.ShouldContain("<div class=\"name\">Dr Meera Shah</div>");
        body.ShouldContain("<div class=\"small\">MBBS, MD (Medicine)</div>");
        body.ShouldContain("<div class=\"small\">Internal medicine, General Medicine</div>");
        body.ShouldContain("<div class=\"small\">Reg. No. MMC-55821 (Maharashtra Medical Council)</div>");
        body.IndexOf("class=\"facility\"", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("class=\"doctor\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_patient_allergies_diagnosis_advice_and_follow_up_are_printed()
    {
        var body = PrescriptionDocument.Body(Model(), null, null).ToString();

        body.ShouldContain("<div class=\"label\">Patient</div><div>Asha Rao</div>");
        body.ShouldContain("<div class=\"label\">Age / sex</div><div>34 y / F</div>");
        body.ShouldContain("<div class=\"label\">Date</div><div>25 Sep 2026, 3:45 pm</div>");
        body.ShouldContain("<div class=\"allergy\">Known allergies: Penicillin, Sulfa drugs</div>");
        body.ShouldContain("<span class=\"label\">Diagnosis</span> Type 2 diabetes mellitus (E11.9); Hypertension (I10)</div>");
        body.ShouldContain("<div class=\"rx keep-next\">℞</div>");
        body.ShouldContain("<h3>Advice</h3><div class=\"pre\">Walk 30 minutes a day.<br>Avoid sweets.</div>");
        body.ShouldContain("<h3>Follow-up</h3><div>Review on 03 Oct 2026. Bring your sugar chart.</div>");
        body.ShouldContain("Refills allowed: 2");
        body.ShouldContain("Digitally signed on 25 Sep 2026, 3:45 pm");
    }

    [Fact]
    public void Without_allergies_advice_or_follow_up_the_page_says_none_recorded_and_leaves_the_rest_out()
    {
        var body = PrescriptionDocument.Body(
            Model() with { KnownAllergies = [], Diagnoses = [], Advice = null, FollowUpDate = null, FollowUpAdvice = null, RefillsAllowed = 0 }, null, null)
            .ToString();

        body.ShouldContain("<div class=\"small\">Known allergies: none recorded</div>");
        body.ShouldNotContain("Diagnosis");
        body.ShouldNotContain("<h3>Advice</h3>");
        body.ShouldNotContain("Follow-up");
        body.ShouldNotContain("Refills allowed");
    }

    [Fact]
    public void The_qr_code_opens_the_portal_verification_page()
    {
        var html = PrescriptionDocument.Build(Model(), null, null).ToHtml();

        html.ShouldContain("<img class=\"qr-img\" src=\"data:image/svg+xml;base64,");
        html.ShouldContain("<div class=\"small\">NHRX260000123</div>");
        html.ShouldContain("<div class=\"small\">Scan to verify this prescription</div>");
        html.ShouldNotContain("Verification code");
    }

    [Fact]
    public void Without_the_public_address_the_code_is_printed_without_a_qr_code()
    {
        var html = PrescriptionDocument.Build(Model() with { VerificationUrl = null }, null, null).ToHtml();

        html.ShouldContain($"Verification code <span class=\"mono\">{Token}</span>");
        html.ShouldNotContain("data:image/svg+xml");
        html.ShouldNotContain("Scan to verify");
    }

    [Fact]
    public async Task The_facility_logo_and_the_prescribers_signature_are_embedded()
    {
        var logo = Guid.NewGuid();
        var signature = Guid.NewGuid();
        var model = Model();
        model = model with { Letterhead = model.Letterhead! with { LogoFileId = logo }, Prescriber = model.Prescriber! with { SignatureFileId = signature } };

        var html = (await PrescriptionDocument.BuildAsync(model, Assets((logo, "image/png", [1, 2, 3]), (signature, "image/jpeg", [4, 5, 6])), Ct)).ToHtml();

        html.ShouldContain($"<img class=\"logo\" src=\"data:image/png;base64,{Convert.ToBase64String(new byte[] { 1, 2, 3 })}\" alt=\"\">");
        html.ShouldContain(
            $"<img class=\"sign-img\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(new byte[] { 4, 5, 6 })}\" alt=\"Signature of Dr Meera Shah\">");
        html.ShouldContain("<div class=\"line\">Dr Meera Shah</div>");
    }

    [Fact]
    public async Task An_unsigned_copy_carries_no_signature_image()
    {
        var signature = Guid.NewGuid();
        var model = Model();
        model = model with { Status = PrescriptionStatus.Draft, SignedAt = null, Prescriber = model.Prescriber! with { SignatureFileId = signature } };

        var html = (await PrescriptionDocument.BuildAsync(model, Assets((signature, "image/png", [1, 2, 3])), Ct)).ToHtml();

        html.ShouldNotContain("class=\"sign-img\"");
        html.ShouldNotContain("Digitally signed");
        html.ShouldContain("<div class=\"label\">Date</div><div>—</div>");
    }

    [Fact]
    public void A_copy_that_must_not_be_dispensed_says_so_on_the_page_and_on_every_footer()
    {
        var draft = PrescriptionDocument.Build(Model() with { Status = PrescriptionStatus.Draft, SignedAt = null }, null, null);
        var cancelled = PrescriptionDocument.Build(Model() with { Status = PrescriptionStatus.Cancelled }, null, null);

        draft.ToHtml().ShouldContain("<div class=\"draft\">DRAFT — not valid for dispensing</div>");
        draft.Footer.ShouldBe("Asha Rao · UHID NH-000123 · Prescription NHRX260000123 · DRAFT — not valid for dispensing");
        cancelled.ToHtml().ShouldContain("<div class=\"draft\">CANCELLED — do not dispense</div>");
        cancelled.Footer!.ShouldEndWith(" · CANCELLED — do not dispense");
        PrescriptionDocument.Build(Model(), null, null).ToHtml().ShouldNotContain("class=\"draft\"");
    }

    [Fact]
    public void Text_from_the_record_is_encoded_and_never_becomes_markup()
    {
        var model = Model();
        model = model with
        {
            Patient = model.Patient with { Name = Hostile, Uhid = "NH-1\"><b>" },
            Letterhead = model.Letterhead! with { Name = Hostile, Address = Hostile, LetterheadNote = Hostile },
            Prescriber = model.Prescriber! with { DisplayName = Hostile, Qualification = Hostile },
            Diagnoses = [Hostile],
            KnownAllergies = [Hostile],
            Lines = [model.Lines[0] with { DrugName = Hostile, Dose = Hostile, Instructions = Hostile, FrequencyHindi = Hostile }],
            Advice = Hostile,
            FollowUpAdvice = Hostile,
        };

        var document = PrescriptionDocument.Build(model, null, null);
        var html = document.ToHtml();

        html.ShouldNotContain("<script");
        html.ShouldNotContain("<b>");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain("<div class=\"hindi\" lang=\"hi\">&lt;script&gt;");
        document.Footer.ShouldBe($"{Hostile} · UHID NH-1\"><b> · Prescription NHRX260000123");
    }

    [Fact]
    public void Without_a_letterhead_or_prescriber_the_prescription_still_prints()
    {
        var html = PrescriptionDocument.Build(Model() with { Letterhead = null, Prescriber = null }, null, null).ToHtml();

        html.ShouldContain("<div class=\"facility\">Hospital</div>");
        html.ShouldContain("<div class=\"line\">Doctor</div>");
        html.ShouldNotContain("class=\"doctor\"");
    }

    [Fact]
    public void Messages_can_carry_the_prescription_and_the_discharge_summary()
    {
        var services = new ServiceCollection();
        new ClinicalModule().AddServices(services, new ConfigurationBuilder().Build());

        PrescriptionDocumentSource.DocumentKind.ShouldBe("clinical.prescription");
        DischargeSummaryDocumentSource.DocumentKind.ShouldBe("clinical.discharge-summary");
        services.Where(s => s.ServiceType == typeof(INotificationDocumentSource)).Select(s => s.ImplementationType)
            .ShouldBe([typeof(PrescriptionDocumentSource), typeof(DischargeSummaryDocumentSource)], ignoreOrder: true);
    }

    internal static DocumentAssets Assets(params (Guid Id, string ContentType, byte[] Content)[] images)
    {
        var files = Substitute.For<IFileStore>();
        foreach (var (id, contentType, content) in images)
        {
            files.ReadAsync(id, Arg.Any<CancellationToken>())
                .Returns(Result<(StoredFile File, byte[] Content)>.Ok((new StoredFile { ContentType = contentType }, content)));
        }

        return new DocumentAssets(null!, files, new MemoryCache(new MemoryCacheOptions()));
    }

    private static PrescriptionPrintModel Model() => new(
        Guid.NewGuid(),
        "NHRX260000123",
        PrescriptionStatus.Signed,
        Signed,
        new FacilityLetterhead(Guid.NewGuid(), "NH", "Nana Hospitals", "Nana Hospitals Pvt Ltd", "12 MG Road, Pune", "020 4000 1000", "care@nana.in",
            "nana.in", "MH-PUN-1234", "H-2021-0456", null, null, "Accredited by NABH"),
        new PrescriberInfo(Guid.NewGuid(), "Dr Meera Shah", "MBBS, MD (Medicine)", "Maharashtra Medical Council", "MMC-55821", "General Medicine",
            "Internal medicine", null),
        new PrescriptionPrintPatient(Guid.NewGuid(), "NH-000123", "Asha Rao", "34 y", Gender.Female, "hi"),
        ["Type 2 diabetes mellitus (E11.9)", "Hypertension (I10)"],
        ["Penicillin", "Sulfa drugs"],
        [
            new PrescriptionPrintLine(1, "Metformin", "Metformin hydrochloride", "500 mg", "Tablet", "Oral", "1 tab", "BD", "Twice a day", "दिन में दो बार", 30,
                60.00m, "After food", false),
            new PrescriptionPrintLine(2, "Tramadol", "Tramadol", "50 mg", "Capsule", "Oral", "1 cap", "SOS", "Only when required", "आवश्यकता होने पर", 5,
                10.00m, null, true),
            new PrescriptionPrintLine(3, "Ceftriaxone", null, "1 g", "Injection", "IV", "1 g", "STAT", "Immediately, once", "तुरंत, एक बार", 1, 1.00m, null,
                false),
        ],
        "Walk 30 minutes a day.\nAvoid sweets.",
        new DateOnly(2026, 10, 3),
        "Bring your sugar chart.",
        2,
        Token,
        PrescriptionVerificationLinks.Path(Token),
        PrescriptionVerificationLinks.Url("https://care.nanahospitals.in", Token));
}
