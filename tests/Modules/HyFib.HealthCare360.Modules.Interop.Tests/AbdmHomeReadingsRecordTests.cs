using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Gap #27: the readings a patient takes at home in a remote monitoring programme, as ABDM wellness records (the guide's
/// WellnessRecord), a document for each month. The September sample is in Fixtures/Abdm with the other kinds
/// (<see cref="AbdmDocumentTests"/>, which validates it against the guide); these tests say what a month holds, how the
/// dates a consent asks for cut it, how a month of too many readings is split, and how the programme is named and sent.
/// </summary>
public sealed class AbdmHomeReadingsRecordTests
{
    private static readonly DateOnly September = new(2026, 9, 1);

    private static R4.Bundle Build(HomeReadingsData data) => AbdmDocumentBuilder.Build(AbdmRecords.HomeReadings(data));

    private static R4.Composition Composition(R4.Bundle bundle) => bundle.Entry[0].Resource.ShouldBeOfType<R4.Composition>();

    private static List<R4.Observation> Entries(R4.Bundle bundle, R4.Composition.SectionComponent section) =>
        [.. section.Entry.Select(e => bundle.Entry.Single(x => x.FullUrl == e.Reference).Resource).Cast<R4.Observation>()];

    private static string? FirstLoinc(R4.Observation observation) => observation.Code.Coding.First(c => c.System == FhirSystems.Loinc).Code;

    private static void ShouldMeetTheGuide(R4.Bundle bundle)
    {
        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
        validation.Warnings.ShouldBeEmpty(validation.Describe());
    }

    [Fact]
    public void A_month_of_home_readings_is_a_wellness_record_with_each_reading_in_the_guides_section_for_it()
    {
        var document = AbdmRecords.HomeReadings(AbdmTestData.HomeReadingsRecord());
        var bundle = AbdmDocumentBuilder.Build(document);
        var composition = Composition(bundle);
        var hospital = bundle.Entry.Select(e => e.Resource).OfType<R4.Organization>().ShouldHaveSingleItem();
        var hospitalUrl = bundle.Entry.Single(e => ReferenceEquals(e.Resource, hospital)).FullUrl;

        // Saved under the UHID and the month; typed as the guide fixes it, by its text alone.
        document.FileName.ShouldBe($"home-readings-{AbdmTestData.Patient().Uhid}-2026-09-abdm.json");
        composition.Meta.Profile.ShouldBe([AbdmProfiles.WellnessRecord]);
        composition.Type.Coding.ShouldBeEmpty();
        composition.Type.Text.ShouldBe("Wellness Record");
        composition.Title.ShouldBe("Home readings: Hypertension home monitoring, September 2026");
        composition.Status.ShouldBe(R4.CompositionStatus.Final);

        // No one signs a reading: the facility running the programme compiled them and keeps them, and the document is
        // dated when the latest of them arrived.
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(hospitalUrl);
        composition.Custodian.Reference.ShouldBe(hospitalUrl);
        composition.Attester.ShouldBeEmpty();
        composition.Encounter.ShouldBeNull();
        composition.Date.ShouldBe(FhirTime.DateTime(AbdmTestData.Ist(9, 20, 23, 52)), "the ECG patch's rhythm, received last");

        // The whole of September, in its days.
        var period = composition.Event.ShouldHaveSingleItem().Period;
        (period.Start, period.End).ShouldBe(("2026-09-01", "2026-09-30"));

        // The guide's sections, in its order, told apart by title alone as the guide slices them.
        composition.Section.Select(s => s.Title).ShouldBe(["Vital Signs", "Body Measurement", "General Assessment", "Other Observations"]);
        composition.Section.ShouldAllBe(s => s.Code == null);

        // The blood pressure taken with the pulse is one panel; each reading has the profile its section asks for.
        var vitals = Entries(bundle, composition.Section[0]);
        vitals.Select(FirstLoinc).ShouldBe(["85354-9", "8867-4", "59408-5", "8310-5"]);
        vitals.ShouldAllBe(o => o.Meta.Profile.Single() == AbdmProfiles.ObservationVitalSigns);
        var weight = Entries(bundle, composition.Section[1]).ShouldHaveSingleItem();
        (FirstLoinc(weight), weight.Meta.Profile.Single()).ShouldBe(("29463-7", AbdmProfiles.ObservationBodyMeasurement));
        var glucose = Entries(bundle, composition.Section[2]).ShouldHaveSingleItem();
        (FirstLoinc(glucose), glucose.Meta.Profile.Single()).ShouldBe(("41653-7", AbdmProfiles.ObservationGeneralAssessment));
        var rhythm = Entries(bundle, composition.Section[3]).ShouldHaveSingleItem();
        (FirstLoinc(rhythm), rhythm.Meta.Profile.Single()).ShouldBe((DeviceReadingObservations.RhythmLoinc, AbdmProfiles.Observation));
        rhythm.Value.ShouldBeOfType<R4.CodeableConcept>().Text.ShouldBe("Atrial fibrillation suspected");

        // As the FHIR API serves them: the patient measured each, with the device named, and how it stood is said.
        var panel = vitals[0];
        panel.Performer.ShouldHaveSingleItem().Reference.ShouldBe(composition.Subject.Reference);
        panel.Device.Display.ShouldBe("Omron HEM-7156 blood pressure monitor (serial OM2609-0041)");
        panel.Interpretation.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("A");
        glucose.Interpretation.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("AA");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void Each_section_reads_as_a_chart_of_its_readings_with_the_time_each_was_measured_in_IST()
    {
        var bundle = Build(AbdmTestData.HomeReadingsRecord());
        var composition = Composition(bundle);

        composition.Section[0].Text.Div.ShouldBe(
            "<div xmlns=\"http://www.w3.org/1999/xhtml\">"
            + "<p>02 Sep 2026, 7:30 am: Blood pressure: 150 / 95 mmHg, outside the limits set for the patient</p>"
            + "<p>02 Sep 2026, 7:30 am: Heart rate: 78 beats/minute</p>"
            + "<p>03 Sep 2026, 8:00 am: Oxygen saturation (SpO2): 97 %</p>"
            + "<p>05 Sep 2026, 9:15 pm: Body temperature: 37.9 °C (read out by the patient, entered by Priya Nair)</p></div>");
        composition.Section[2].Text.Div.ShouldContain("12 Sep 2026, 7:05 am: Blood glucose: 182 mg/dL, beyond the critical limits set for the patient");
        composition.Section[3].Text.Div.ShouldContain("20 Sep 2026, 11:50 pm: Heart rhythm: Atrial fibrillation suspected, beyond the critical limits set for the patient");
        composition.Section.ShouldAllBe(s => s.Text.Status == R4.Narrative.NarrativeStatus.Generated);

        // Each reading's own narrative is its line, so it reads whole wherever it is shown.
        var temperature = Entries(bundle, composition.Section[0])[3];
        temperature.Text.Div.ShouldBe(
            "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>05 Sep 2026, 9:15 pm: Body temperature: 37.9 °C (read out by the patient, entered by Priya Nair)</p></div>");
    }

    [Fact]
    public void Readings_are_in_the_order_they_were_measured_whatever_order_the_rows_came_in()
    {
        var reversed = AbdmTestData.HomeReadings().Reverse().ToList();

        FhirJson.Serialize(Build(AbdmTestData.HomeReadingsRecord(reversed))).ShouldBe(FhirJson.Serialize(Build(AbdmTestData.HomeReadingsRecord())));
    }

    [Fact]
    public void A_month_holds_the_readings_measured_in_it_in_India()
    {
        var month = ReadingsSpan.Of(new DateOnly(2026, 9, 17));

        month.Month.ShouldBe(September);
        (month.From, month.To).ShouldBe((null, null));
        month.Start.ShouldBe(AbdmTestData.Ist(9, 1, 0, 0));
        month.End.ShouldBe(AbdmTestData.Ist(10, 1, 0, 0));

        // Midnight in India, not in UTC: 31 August 18:30 UTC is 1 September.
        month.Holds(AbdmTestData.Ist(9, 1, 0, 0)).ShouldBeTrue();
        month.Holds(AbdmTestData.Ist(9, 1, 0, 0).AddTicks(-1)).ShouldBeFalse();
        month.Holds(AbdmTestData.Ist(9, 30, 23, 59, 59)).ShouldBeTrue();
        month.Holds(AbdmTestData.Ist(10, 1, 0, 0)).ShouldBeFalse("the first instant of October is October's");
        month.Key.ShouldBe("2026-09");
        (month.Period.Start, month.Period.End).ShouldBe(("2026-09-01", "2026-09-30"));
    }

    [Fact]
    public void The_dates_a_consent_asks_for_cut_a_month_where_they_fall_inside_it_and_are_inclusive()
    {
        var from = AbdmTestData.Ist(9, 10, 9, 0);
        var to = AbdmTestData.Ist(9, 20, 18, 0);
        var cut = ReadingsSpan.Of(September, from, to);

        (cut.From, cut.To).ShouldBe((from, to));
        cut.Holds(from).ShouldBeTrue();
        cut.Holds(from.AddTicks(-1)).ShouldBeFalse();
        cut.Holds(to).ShouldBeTrue();
        cut.Holds(to.AddTicks(1)).ShouldBeFalse();
        (cut.Period.Start, cut.Period.End).ShouldBe((FhirTime.DateTime(from), FhirTime.DateTime(to)));
        cut.Key.ShouldNotBe("2026-09");

        // Dates that begin before the month and end after it do not cut it.
        var whole = ReadingsSpan.Of(September, AbdmTestData.Ist(1, 1, 0, 0), AbdmTestData.Ist(12, 31, 0, 0));
        (whole.From, whole.To, whole.Key).ShouldBe((null, null, "2026-09"));

        // A consent of the same days in another way is the same cut, and a different one is a different document.
        ReadingsSpan.Of(new DateOnly(2026, 9, 30), from, to).Key.ShouldBe(cut.Key);
        ReadingsSpan.Of(September, from.AddMinutes(1), to).Key.ShouldNotBe(cut.Key);
    }

    [Fact]
    public void A_cut_month_says_what_it_covers_and_is_a_document_of_its_own()
    {
        var from = AbdmTestData.Ist(9, 4, 0, 0);
        var span = ReadingsSpan.Of(September, from, null);
        var readings = AbdmTestData.HomeReadings().Where(r => span.Holds(r.MeasuredAt)).ToList();
        var whole = Composition(Build(AbdmTestData.HomeReadingsRecord()));
        var cut = Composition(Build(AbdmTestData.HomeReadingsRecord(readings, span)));

        var period = cut.Event.ShouldHaveSingleItem().Period;
        (period.Start, period.End).ShouldBe((FhirTime.DateTime(from), "2026-09-30"));
        cut.Text.Div.ShouldContain($"Covers: {FhirTime.DateTime(from)} to 2026-09-30");
        cut.Id.ShouldNotBe(whole.Id, "the whole month and the part a consent asked for are not versions of each other");
        cut.Section[0].Entry.Count.ShouldBe(1, "only the temperature of the vital signs was measured from the 4th");
    }

    [Fact]
    public void Each_month_is_a_document_of_its_own_and_the_same_month_is_the_same_document()
    {
        var october = ReadingsSpan.Of(new DateOnly(2026, 10, 1));
        var octoberReadings = AbdmTestData.HomeReadings().Select(r => r with { MeasuredAt = r.MeasuredAt.AddMonths(1), ReceivedAt = r.ReceivedAt.AddMonths(1) }).ToList();

        var first = Build(AbdmTestData.HomeReadingsRecord());
        var again = Build(AbdmTestData.HomeReadingsRecord());
        var next = Build(AbdmTestData.HomeReadingsRecord(octoberReadings, october));

        FhirJson.Serialize(again).ShouldBe(FhirJson.Serialize(first));
        Composition(next).Id.ShouldNotBe(Composition(first).Id);
        next.Id.ShouldNotBe(first.Id);
        Composition(next).Title.ShouldBe("Home readings: Hypertension home monitoring, October 2026");
    }

    [Fact]
    public void A_month_of_more_readings_than_a_document_holds_goes_in_parts_never_parting_a_blood_pressure()
    {
        // A weight, then a monitor sending every few minutes: pairs of blood pressure readings, more than one document
        // holds. The weight puts the pairs off by one, so the limit falls inside a pair: the 500th, readings 1,000 and 1,001.
        var pairs = (AbdmRecords.MaxReadingsPerDocument / 2) + 10;
        var readings = Enumerable.Range(0, pairs).SelectMany(i => new[]
        {
            AbdmTestData.Reading(0x200 + (2 * i), RpmReadingType.SystolicBp, 120m, AbdmTestData.Ist(9, 1, 6, 0).AddMinutes(10 * i), ReadingSeverity.Normal, null),
            AbdmTestData.Reading(0x201 + (2 * i), RpmReadingType.DiastolicBp, 80m, AbdmTestData.Ist(9, 1, 6, 0).AddMinutes(10 * i), ReadingSeverity.Normal, null),
        }).Prepend(AbdmTestData.Reading(0x1f0, RpmReadingType.Weight, 71.8m, AbdmTestData.Ist(9, 1, 5, 30), ReadingSeverity.Normal, null)).ToList();

        var parts = AbdmRecords.Parts(readings);

        parts.Count.ShouldBe(2);
        parts.ShouldAllBe(p => p.Count <= AbdmRecords.MaxReadingsPerDocument);
        parts.SelectMany(p => p).Select(r => r.Id).ShouldBe(readings.OrderBy(r => r.MeasuredAt).ThenBy(r => r.Id).Select(r => r.Id));
        parts[0].Count.ShouldBe(AbdmRecords.MaxReadingsPerDocument - 1, "the pair the limit falls in starts the next part");
        parts.SelectMany((p, i) => p.Select(r => (r.MeasuredAt, Part: i))).GroupBy(r => r.MeasuredAt)
            .ShouldAllBe(instant => instant.Select(r => r.Part).Distinct().Count() == 1, "a panel's two readings stay together");
        parts[0][^1].MeasuredAt.ShouldBeLessThan(parts[1][0].MeasuredAt);

        // Each part names itself; the first keeps the month's identity as the month grows into more parts.
        var data = AbdmTestData.HomeReadingsRecord(parts[0]) with { Part = 1, Parts = 2 };
        var first = AbdmRecords.HomeReadings(data);
        var second = AbdmRecords.HomeReadings(AbdmTestData.HomeReadingsRecord(parts[1]) with { Part = 2, Parts = 2 });
        first.Title.ShouldBe("Home readings: Hypertension home monitoring, September 2026 (part 1 of 2)");
        second.FileName.ShouldBe($"home-readings-{AbdmTestData.Patient().Uhid}-2026-09-part-2-abdm.json");
        Composition(AbdmDocumentBuilder.Build(first)).Id.ShouldBe(Composition(Build(AbdmTestData.HomeReadingsRecord(parts[0]))).Id);
        Composition(AbdmDocumentBuilder.Build(second)).Id.ShouldNotBe(Composition(AbdmDocumentBuilder.Build(first)).Id);

        // A month that fits is one part.
        AbdmRecords.Parts(AbdmTestData.HomeReadings()).ShouldHaveSingleItem().Count.ShouldBe(AbdmTestData.HomeReadings().Count);
    }

    [Fact]
    public void A_document_of_home_readings_needs_a_reading() =>
        Should.Throw<InvalidOperationException>(() => AbdmRecords.HomeReadings(AbdmTestData.HomeReadingsRecord([])));

    [Fact]
    public void Home_readings_go_only_under_a_consent_for_wellness_records()
    {
        AbdmGatewayMessages.HiType(AbdmRecordType.HomeReadings).ShouldBe("WellnessRecord");
        AbdmDataFlowService.Covers("OPConsultation,WellnessRecord", AbdmRecordType.HomeReadings).ShouldBeTrue();
        AbdmDataFlowService.Covers("OPConsultation,HealthDocumentRecord", AbdmRecordType.HomeReadings).ShouldBeFalse();
        AbdmLinkingService.ReferenceNumber(AbdmRecordType.HomeReadings, AbdmTestData.EnrollmentId).ShouldBe($"home-readings/{AbdmTestData.EnrollmentId:D}");
    }

    [Fact]
    public void A_programme_is_named_for_the_patient_by_its_kind_from_the_day_they_were_enrolled_never_by_its_name()
    {
        var enrolled = FhirTime.StartOfDay(new DateOnly(2026, 9, 2));

        AbdmCareContexts.Describe(AbdmRecordType.HomeReadings, AbdmTestData.EnrollmentId, enrolled, AbdmRecordLoader.ProgrammeLabel(RpmProgramKind.Hypertension))
            .Display.ShouldBe("Home readings: Hypertension monitoring, from 02 Sep 2026");
        AbdmRecordLoader.ProgrammeLabel(RpmProgramKind.Diabetes).ShouldBe("Diabetes monitoring");
        AbdmRecordLoader.ProgrammeLabel(RpmProgramKind.Cardiac).ShouldBe("Heart monitoring");
        AbdmRecordLoader.ProgrammeLabel(RpmProgramKind.PostDischarge).ShouldBe("Monitoring after discharge");

        // A programme of the hospital's own kind goes by the record's kind alone: its name is the hospital's free text.
        AbdmRecordLoader.ProgrammeLabel(RpmProgramKind.Custom).ShouldBeNull();
        AbdmCareContexts.Describe(AbdmRecordType.HomeReadings, AbdmTestData.EnrollmentId, enrolled, null).Display.ShouldBe("Home readings, from 02 Sep 2026");
    }

    [Fact]
    public void Every_kind_of_home_reading_has_a_section_of_the_guide() =>
        Enum.GetValues<RpmReadingType>().Select(t => AbdmRecords.WellnessSection(t).Title).Distinct()
            .ShouldBe(["Vital Signs", "Body Measurement", "General Assessment", "Other Observations"], ignoreOrder: true);
}
