using System.Globalization;
using System.Text.Json;
using Hl7.Fhir.Serialization;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Home-monitoring readings as FHIR Observations. They are coded as the clinic's vital signs are, and conform to the FHIR
/// R4 vital-signs profiles and to ABDM's (Firely's validator, against the published definitions). HC360's own device
/// intake reads every one back as the reading it was. And a search loads only the readings it could match that the
/// consents cover: the query's rule is checked against the rule the rest of the record is shared by.
/// </summary>
public sealed class DeviceReadingObservationsTests
{
    private const string NrcesVitalSigns = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/ObservationVitalSigns";
    private const string NrcesObservation = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/Observation";

    private static readonly Guid EnrollmentId = Guid.Parse("01923a4b-0000-7000-8000-0000000000e1");
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>08:15:30 IST on 14 October 2026.</summary>
    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 14, 2, 45, 30, TimeSpan.Zero);

    /// <summary>Two minutes and a fraction of a second later.</summary>
    private static readonly DateTimeOffset ReceivedAt = MeasuredAt.AddMinutes(2).AddTicks(1_234_567);

    private static readonly JsonSerializerOptions FhirJson = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector);

    private static readonly IReadOnlyList<RpmReadingType> AllTypes = Enum.GetValues<RpmReadingType>();

    public static TheoryData<RpmReadingType> Types => new(Enum.GetValues<RpmReadingType>());

    public static TheoryData<RpmReadingType, decimal, string[], string, string, string, string?> SingleReadings => new()
    {
        { RpmReadingType.HeartRate, 72m, ["8867-4"], VitalSignObservations.VitalSigns, "/min", "beats/minute", "heartrate" },
        { RpmReadingType.SpO2, 96m, ["59408-5", "2708-6"], VitalSignObservations.VitalSigns, "%", "%", "oxygensat" },
        { RpmReadingType.Temperature, 37.2m, ["8310-5"], VitalSignObservations.VitalSigns, "Cel", "°C", "bodytemp" },
        { RpmReadingType.Weight, 68.40m, ["29463-7"], VitalSignObservations.VitalSigns, "kg", "kg", "bodyweight" },
        { RpmReadingType.Glucose, 142m, ["41653-7"], VitalSignObservations.Laboratory, "mg/dL", "mg/dL", null },
    };

    [Theory]
    [MemberData(nameof(SingleReadings))]
    public void A_reading_is_coded_as_the_clinics_vital_signs_are_and_says_when_it_was_measured_and_received(
        RpmReadingType type, decimal value, string[] loinc, string category, string ucum, string unit, string? profile)
    {
        var row = Row(type, value);

        var observation = DeviceReadingObservations.Map([row]).ShouldHaveSingleItem();

        observation.Id.ShouldBe($"rpm-{row.Id}");
        observation.Status.ShouldBe(R4.ObservationStatus.Final);
        observation.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe(category);
        observation.Code.Coding.Select(c => (c.System, c.Code)).ShouldBe(loinc.Select(code => ((string?)FhirSystems.Loinc, (string?)code)));
        observation.Code.Coding.ShouldAllBe(c => !string.IsNullOrEmpty(c.Display));
        var quantity = observation.Value.ShouldBeOfType<R4.Quantity>();
        (quantity.Value, quantity.System, quantity.Code, quantity.Unit).ShouldBe((value, FhirSystems.Ucum, ucum, unit));
        quantity.Value!.Value.ToString(CultureInfo.InvariantCulture).ShouldBe(value.ToString("0.##", CultureInfo.InvariantCulture), "no precision the device did not measure");
        observation.Meta.Profile.ShouldBe(profile is null ? [] : [FhirSystems.VitalSignsProfile + profile]);
        observation.Subject.Reference.ShouldBe($"Patient/{TestData.PatientId}");
        observation.Performer.ShouldHaveSingleItem().Reference.ShouldBe($"Patient/{TestData.PatientId}", "the patient took the measurement");
        observation.Effective.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2026-10-14T08:15:30+05:30");
        observation.Issued.ShouldBe(new DateTimeOffset(2026, 10, 14, 8, 17, 30, 123, Ist));
        observation.Meta.LastUpdated.ShouldBe(observation.Issued);
        observation.Interpretation.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("N");
        SharedRecords.KindOf(observation).ShouldBe(category == VitalSignObservations.Laboratory ? SharedRecordKind.LabResults : SharedRecordKind.VitalSigns);
        SharedRecords.DateOf(observation).ShouldBe(new DateOnly(2026, 10, 14));
    }

    [Fact]
    public void A_systolic_and_a_diastolic_reading_taken_together_are_one_blood_pressure_panel_under_the_systolic_readings_id()
    {
        var systolic = Row(RpmReadingType.SystolicBp, 150m, ReadingSeverity.Warning);
        var diastolic = Row(RpmReadingType.DiastolicBp, 95m) with { ReceivedAt = ReceivedAt.AddSeconds(5) };
        var pulse = Row(RpmReadingType.HeartRate, 88m);

        var observations = DeviceReadingObservations.Map([diastolic, pulse, systolic]);

        observations.Select(o => o.Id).ShouldBe([$"rpm-{systolic.Id}", $"rpm-{pulse.Id}"], ignoreOrder: true);
        var panel = observations.Single(o => o.Id == $"rpm-{systolic.Id}");
        panel.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe("85354-9");
        panel.Meta.Profile.ShouldBe([FhirSystems.VitalSignsProfile + "bp"]);
        panel.Value.ShouldBeNull();
        panel.Component.Select(c => (c.Code.Coding.Single().Code, ((R4.Quantity)c.Value).Value, ((R4.Quantity)c.Value).Code, c.Interpretation.Single().Coding.Single().Code))
            .ShouldBe([("8480-6", (decimal?)150m, "mm[Hg]", "A"), ("8462-4", (decimal?)95m, "mm[Hg]", "N")]);
        panel.Interpretation.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("A", "a panel is as far out as its further reading");
        panel.Issued.ShouldBe(FhirTime.ToMilliseconds(diastolic.ReceivedAt), "the panel was whole when its later reading arrived");
    }

    [Theory]
    [InlineData(RpmReadingType.SystolicBp, "8462-4")]
    [InlineData(RpmReadingType.DiastolicBp, "8480-6")]
    public void A_blood_pressure_reading_taken_alone_is_a_panel_under_its_own_id_that_states_the_other_as_absent(RpmReadingType type, string absent)
    {
        var row = Row(type, type == RpmReadingType.SystolicBp ? 132m : 84m);

        var panel = DeviceReadingObservations.Map([row]).ShouldHaveSingleItem();

        panel.Id.ShouldBe($"rpm-{row.Id}");
        panel.Component.Count.ShouldBe(2);
        var missing = panel.Component.Single(c => c.Code.Coding.Single().Code == absent);
        missing.Value.ShouldBeNull();
        missing.DataAbsentReason.Coding.ShouldHaveSingleItem().Code.ShouldBe("unknown");
        missing.Interpretation.ShouldBeEmpty();
        panel.Component.Single(c => c.Code.Coding.Single().Code != absent).Value.ShouldBeOfType<R4.Quantity>().Value.ShouldBe(row.Value);
    }

    [Fact]
    public void Readings_of_two_enrolments_taken_at_the_same_instant_are_two_panels()
    {
        var other = Guid.Parse("01923a4b-0000-7000-8000-0000000000e2");
        DeviceReadingRow[] rows =
        [
            Row(RpmReadingType.SystolicBp, 140m), Row(RpmReadingType.DiastolicBp, 90m),
            Row(RpmReadingType.SystolicBp, 120m) with { EnrollmentId = other }, Row(RpmReadingType.DiastolicBp, 80m) with { EnrollmentId = other },
        ];

        var panels = DeviceReadingObservations.Map(rows);

        panels.Select(p => (((R4.Quantity)p.Component[0].Value).Value, ((R4.Quantity)p.Component[1].Value).Value))
            .ShouldBe([(140m, 90m), (120m, 80m)], ignoreOrder: true);
        panels.Select(p => p.Id).ShouldBe([$"rpm-{rows[0].Id}", $"rpm-{rows[2].Id}"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(ReadingSeverity.Normal, "N", "Normal")]
    [InlineData(ReadingSeverity.Warning, "A", "Abnormal")]
    [InlineData(ReadingSeverity.Critical, "AA", "Critical abnormal")]
    public void A_readings_interpretation_is_how_it_stood_against_the_limits_the_care_team_set(ReadingSeverity severity, string code, string display)
    {
        var interpretation = DeviceReadingObservations.Map([Row(RpmReadingType.Glucose, 250m, severity)]).Single().Interpretation.ShouldHaveSingleItem();

        var coding = interpretation.Coding.ShouldHaveSingleItem();
        (coding.System, coding.Code, coding.Display).ShouldBe((FhirSystems.ObservationInterpretation, code, display));
        interpretation.Text.ShouldContain("limits set for the patient");
    }

    [Theory]
    [InlineData("Atrial fibrillation suspected", 2, "Atrial fibrillation suspected")]
    [InlineData(null, 0, "Normal sinus rhythm")]
    [InlineData("  ", 1, "Other or inconclusive rhythm")]
    [InlineData(null, 2, "Atrial fibrillation or worse")]
    public void An_ecg_patchs_rhythm_is_a_heart_test_finding_in_the_devices_words_or_else_by_its_class(string? words, int rhythmClass, string text)
    {
        var row = Row(RpmReadingType.EcgRhythm, rhythmClass) with { TextValue = words, DeviceType = RpmDeviceType.EcgPatch };

        var observation = DeviceReadingObservations.Map([row]).ShouldHaveSingleItem();

        observation.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe(DeviceReadingObservations.RhythmLoinc);
        observation.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe(VitalSignObservations.Procedure);
        observation.Meta.Profile.ShouldBeEmpty();
        observation.Value.ShouldBeOfType<R4.CodeableConcept>().Text.ShouldBe(text);
        SharedRecords.KindOf(observation).ShouldBe(SharedRecordKind.ScansAndHeartTests, "shared as the heart tests are, not as a vital sign");
    }

    [Fact]
    public void The_device_is_named_with_its_serial_number_for_tracing_and_a_reading_without_one_names_none()
    {
        var row = Row(RpmReadingType.HeartRate, 70m);

        var device = DeviceReadingObservations.Map([row]).Single().Device.ShouldNotBeNull();
        (device.Type, device.Reference, device.Display).ShouldBe(("Device", null, "Omron HEM-7156 blood pressure monitor (serial OM-0001)"));
        DeviceReadingObservations.Map([row with { DeviceManufacturer = null, DeviceModel = " ", DeviceSerial = null }]).Single().Device!.Display
            .ShouldBe("blood pressure monitor");
        DeviceReadingObservations.Map([row with { DeviceType = null }]).Single().Device.ShouldBeNull();
    }

    [Fact]
    public void A_reading_entered_by_hand_says_so_and_who_entered_it()
    {
        var row = Row(RpmReadingType.Weight, 70m) with { Source = ReadingSource.Manual, DeviceType = null, RecordedByUserName = "Sr. Meena" };

        DeviceReadingObservations.Map([row]).Single().Note.ShouldHaveSingleItem().Text.ShouldBe("Read out by the patient and entered by Sr. Meena.");
        DeviceReadingObservations.Map([row with { RecordedByUserName = null }]).Single().Note.ShouldHaveSingleItem().Text
            .ShouldBe("Read out by the patient and entered by hand.");
        DeviceReadingObservations.Map([row with { Source = ReadingSource.Device }]).Single().Note.ShouldBeEmpty();
        DeviceReadingObservations.Map([row with { Source = ReadingSource.Fhir }]).Single().Note.ShouldBeEmpty();
    }

    /// <summary>
    /// Each reading against its own profile (the FHIR R4 vital-signs profile it names, or the core Observation) and against
    /// ABDM's: the vital-signs profile for a vital sign, the general Observation profile for a glucose or a rhythm (ABDM's
    /// vital signs take a number or a text only).
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void Every_reading_conforms_to_the_fhir_r4_profiles_and_to_abdms(RpmReadingType type)
    {
        var observation = DeviceReadingObservations.Map([Row(type, SampleValue(type))]).Single();

        var own = AbdmProfileValidator.Validate(observation);
        own.Errors.ShouldBeEmpty(own.Describe());
        var abdm = AbdmProfileValidator.Validate(observation, IsVitalSign(observation) ? NrcesVitalSigns : NrcesObservation);
        abdm.Errors.ShouldBeEmpty(abdm.Describe());
    }

    [Fact]
    public void A_whole_blood_pressure_panel_and_an_ecg_rhythm_in_words_conform_too()
    {
        var panel = DeviceReadingObservations.Map([Row(RpmReadingType.SystolicBp, 128m), Row(RpmReadingType.DiastolicBp, 82m)]).Single();
        var rhythm = DeviceReadingObservations.Map([Row(RpmReadingType.EcgRhythm, 2m) with { TextValue = "Atrial fibrillation suspected" }]).Single();

        foreach (var (observation, profile) in new[] { (panel, NrcesVitalSigns), (rhythm, NrcesObservation) })
        {
            var validation = AbdmProfileValidator.Validate(observation, profile);
            validation.Errors.ShouldBeEmpty(validation.Describe());
        }
    }

    /// <summary>
    /// What HC360 serves, HC360's own device intake (<see cref="FhirObservationParser"/>) reads back as the reading it was:
    /// same type, same value in the canonical unit, same time. The codes and units are those a sender's are read by.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void Hc360s_own_device_intake_reads_back_every_reading_it_serves(RpmReadingType type)
    {
        var row = Row(type, SampleValue(type));

        var parsed = ReadBack(DeviceReadingObservations.Map([row]).Single());

        parsed.Errors.ShouldBeEmpty();
        var reading = parsed.Readings.ShouldHaveSingleItem();
        reading.Type.ShouldBe(type);
        ReadingUnits.Normalize(type, reading.Value, reading.Unit).Value.ShouldBe(row.Value);
        reading.MeasuredAt.ShouldBe(row.MeasuredAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_rhythm_served_by_its_class_is_read_back_as_that_class(int rhythmClass)
    {
        var parsed = ReadBack(DeviceReadingObservations.Map([Row(RpmReadingType.EcgRhythm, rhythmClass)]).Single());

        parsed.Readings.ShouldHaveSingleItem().Value.ShouldBe(rhythmClass);
    }

    [Fact]
    public void A_blood_pressure_panel_is_read_back_as_its_two_readings()
    {
        var parsed = ReadBack(DeviceReadingObservations.Map([Row(RpmReadingType.SystolicBp, 128m), Row(RpmReadingType.DiastolicBp, 82m)]).Single());

        parsed.Readings.Select(r => (r.Type, ReadingUnits.Normalize(r.Type, r.Value, r.Unit).Value))
            .ShouldBe([(RpmReadingType.SystolicBp, 128m), (RpmReadingType.DiastolicBp, 82m)]);
    }

    [Theory]
    [InlineData("", "SystolicBp DiastolicBp HeartRate SpO2 Glucose Weight Temperature EcgRhythm")]
    [InlineData("category=vital-signs", "SystolicBp DiastolicBp HeartRate SpO2 Weight Temperature")]
    [InlineData("category=laboratory", "Glucose")]
    [InlineData("category=procedure", "EcgRhythm")]
    [InlineData("category=survey", "")]
    [InlineData("code=http://loinc.org|85354-9", "SystolicBp DiastolicBp")]
    [InlineData("code=8480-6", "")]
    [InlineData("code=2708-6", "SpO2")]
    [InlineData("code=8867-4,8310-5", "HeartRate Temperature")]
    [InlineData("code=http://snomed.info/sct|8867-4", "")]
    [InlineData("category=vital-signs&code=41653-7", "")]
    [InlineData("date=ge2026-10-01&status=final&_lastUpdated=ge2026-10-01", "SystolicBp DiastolicBp HeartRate SpO2 Glucose Weight Temperature EcgRhythm")]
    public void A_search_reaches_only_the_reading_types_it_could_match(string query, string expected)
    {
        var request = Search(query);

        DeviceReadingObservations.TypesMatching(request).ShouldBe(TypesOf(expected), ignoreOrder: true);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("date=2026-10-14", "2026-10-13T18:29:59Z", "2026-10-14T18:30:01Z")]
    [InlineData("date=ge2026-10-14&date=lt2026-10-16", "2026-10-13T18:29:59Z", "2026-10-15T18:30:01Z")]
    [InlineData("date=gt2026-10-14T08:15:30%2B05:30", "2026-10-14T02:45:30Z", null)]
    [InlineData("date=le2026-10-14", null, "2026-10-14T18:30:01Z")]
    [InlineData("date=ne2026-10-14", null, null)]
    [InlineData("date=ge2026-10-10&date=ge2026-10-12&date=le2026-10-20&date=le2026-10-15", "2026-10-11T18:29:59Z", "2026-10-15T18:30:01Z")]
    public void A_searchs_date_limits_the_readings_measured_a_second_wider_either_side_in_utc(string query, string? from, string? before)
    {
        var (lower, upper) = DeviceReadingObservations.MeasuredWithin(Search(query));

        lower.ShouldBe(Instant(from));
        upper.ShouldBe(Instant(before));
        new[] { lower, upper }.OfType<DateTimeOffset>().ShouldAllBe(bound => bound.Offset == TimeSpan.Zero, "the database takes instants in UTC");
    }

    [Fact]
    public void A_consent_covers_the_reading_types_of_the_kinds_it_names_on_the_days_it_names_as_instants_in_utc()
    {
        var vitalsInOctober = Scope([SharedRecordKind.VitalSigns], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var labs = Scope([SharedRecordKind.LabResults], null, null);
        var notes = Scope([SharedRecordKind.ClinicalNotes], null, null);

        var spans = DeviceReadingObservations.CoveredSpans([vitalsInOctober, labs, notes], AllTypes);

        spans.Count.ShouldBe(2, "clinical notes are no reading's kind");
        spans[0].Types.ShouldBe(TypesOf("SystolicBp DiastolicBp HeartRate SpO2 Weight Temperature"), ignoreOrder: true);
        (spans[0].From, spans[0].Before).ShouldBe((Instant("2026-09-30T18:30:00Z"), Instant("2026-10-31T18:30:00Z")));
        spans[1].Types.ShouldBe([RpmReadingType.Glucose]);
        (spans[1].From, spans[1].Before).ShouldBe(((DateTimeOffset?)null, (DateTimeOffset?)null));
    }

    [Fact]
    public void A_consent_naming_no_kind_covers_every_reading_and_the_first_and_last_days_there_are_leave_its_span_open()
    {
        var scope = SharingScope.Everything with { RecordsFrom = DateOnly.MinValue, RecordsTo = DateOnly.MaxValue };

        var span = DeviceReadingObservations.CoveredSpans([scope], AllTypes).ShouldHaveSingleItem();

        span.Types.ShouldBe(AllTypes, ignoreOrder: true);
        (span.From, span.Before).ShouldBe(((DateTimeOffset?)null, (DateTimeOffset?)null));
        DeviceReadingObservations.CoveredSpans([scope], [RpmReadingType.Glucose]).ShouldHaveSingleItem().Types.ShouldBe([RpmReadingType.Glucose]);
    }

    /// <summary>
    /// The condition a search's query puts on a reading (<see cref="ObservationProvider.Within"/>, run here as the code it
    /// compiles to) shares a reading exactly when the consents share the observation it becomes, as every other record is
    /// shared (<see cref="PatientAccess.Covers"/> on its kind and day): for each kind of reading, at the edges of a day in
    /// hospital time, under consents naming kinds, days, both or neither, alone and two together.
    /// </summary>
    [Fact]
    public void The_query_shares_a_reading_exactly_when_the_consents_share_the_observation_it_becomes()
    {
        SharingScope[] scopes =
        [
            SharingScope.Everything,
            Scope([SharedRecordKind.VitalSigns], null, null),
            Scope([SharedRecordKind.LabResults, SharedRecordKind.ScansAndHeartTests], new DateOnly(2026, 10, 14), null),
            Scope([], null, new DateOnly(2026, 10, 13)),
            Scope([SharedRecordKind.VitalSigns], new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 14)),
            Scope([SharedRecordKind.Immunisations], null, null),
            Scope([SharedRecordKind.CounsellingNotes], new DateOnly(2026, 10, 1), null),
        ];
        DateTimeOffset[] instants =
        [
            new(2026, 10, 13, 23, 59, 59, Ist), new(2026, 10, 14, 0, 0, 0, Ist), new(2026, 10, 14, 23, 59, 59, Ist),
            new(2026, 10, 15, 0, 0, 0, Ist), new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2099, 12, 31, 23, 59, 59, TimeSpan.Zero),
        ];
        List<SharingScope[]> consents = [[], .. scopes.Select(s => new[] { s }), .. scopes.SelectMany((a, i) => scopes.Skip(i + 1).Select(b => new[] { a, b }))];

        var checks = 0;
        foreach (var consent in consents)
        {
            var access = new PatientAccess(SharingDecision.Allowed, null, consent);
            var (inQuery, outside) = ObservationProvider.Within(DeviceReadingObservations.CoveredSpans(consent, AllTypes));
            var covered = inQuery.Compile();
            var uncovered = outside.Compile();
            foreach (var type in AllTypes)
            {
                foreach (var instant in instants)
                {
                    var observation = DeviceReadingObservations.Map([Row(type, SampleValue(type)) with { MeasuredAt = instant }]).Single();
                    var shared = access.Covers(SharedRecords.KindOf(observation), SharedRecords.DateOf(observation));
                    var reading = new RpmReading { PatientId = TestData.PatientId, EnrollmentId = EnrollmentId, Type = type, MeasuredAt = instant };
                    covered(reading).ShouldBe(shared, $"{type} at {instant:O} under {consent.Length} consent(s)");
                    uncovered(reading).ShouldBe(!shared);
                    checks++;
                }
            }
        }

        checks.ShouldBe(29 * 8 * 6);
    }

    [Fact]
    public void A_search_too_large_to_answer_is_refused_as_too_costly_asking_for_a_narrower_one_and_saying_no_count()
    {
        var refusal = FhirOutcomes.TooCostly(FhirResourceTypes.Observation);

        refusal.StatusCode.ShouldBe(400);
        refusal.Reason.ShouldBe(FhirOutcomes.TooManyRecords);
        var issue = refusal.Outcome.Issue.ShouldHaveSingleItem();
        (issue.Severity, issue.Code).ShouldBe((R4.OperationOutcome.IssueSeverity.Error, R4.OperationOutcome.IssueType.TooCostly));
        issue.Diagnostics.ShouldContain("date=ge2026-01-01&date=lt2026-04-01");
        issue.Diagnostics.ShouldNotContain(DeviceReadingObservations.MaxPerSearch.ToString(CultureInfo.InvariantCulture));
        SearchDefinitions.Find(FhirResourceTypes.Observation)!.Documentation.ShouldContain("more than 5,000 of them is refused");
    }

    [Fact]
    public void The_withheld_notice_is_true_whether_some_records_or_all_were_left_out()
    {
        var issue = FhirOutcomes.Withheld().Issue.ShouldHaveSingleItem();

        issue.Diagnostics.ShouldStartWith("Records of this patient may have been left out.");
        issue.Code.ShouldBe(R4.OperationOutcome.IssueType.Suppressed);
    }

    private static DeviceReadingRow Row(RpmReadingType type, decimal value, ReadingSeverity severity = ReadingSeverity.Normal) => new()
    {
        Id = Guid.CreateVersion7(),
        PatientId = TestData.PatientId,
        EnrollmentId = EnrollmentId,
        Type = type,
        Value = value,
        MeasuredAt = MeasuredAt,
        ReceivedAt = ReceivedAt,
        Severity = severity,
        Source = ReadingSource.Device,
        DeviceType = RpmDeviceType.BpMonitor,
        DeviceManufacturer = "Omron",
        DeviceModel = "HEM-7156",
        DeviceSerial = "OM-0001",
    };

    private static decimal SampleValue(RpmReadingType type) => type switch
    {
        RpmReadingType.SystolicBp => 128m,
        RpmReadingType.DiastolicBp => 82m,
        RpmReadingType.HeartRate => 72m,
        RpmReadingType.SpO2 => 97m,
        RpmReadingType.Glucose => 112.5m,
        RpmReadingType.Weight => 64.2m,
        RpmReadingType.Temperature => 36.8m,
        _ => 2m,
    };

    private static bool IsVitalSign(R4.Observation observation) =>
        observation.Category.Any(c => c.Coding.Any(coding => coding.Code == VitalSignObservations.VitalSigns));

    private static FhirParseResult ReadBack(R4.Observation observation)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(observation, FhirJson));
        return FhirObservationParser.Parse(document.RootElement);
    }

    private static SearchRequest Search(string query) =>
        TestData.Search(FhirResourceTypes.Observation, $"patient={TestData.PatientId}" + (query.Length > 0 ? "&" + query : string.Empty));

    private static SharingScope Scope(IReadOnlyCollection<SharedRecordKind> kinds, DateOnly? from, DateOnly? to) => new([], [], kinds, from, to, null);

    private static IReadOnlyList<RpmReadingType> TypesOf(string names) =>
        [.. names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<RpmReadingType>)];

    private static DateTimeOffset? Instant(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
}
