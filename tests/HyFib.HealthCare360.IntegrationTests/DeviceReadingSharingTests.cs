using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;
using MergeHandler = HyFib.HealthCare360.Modules.VirtualCare.Services.PatientsMergedHandler;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Gap #27 through the whole server: the readings patients' monitoring devices push, and those a nurse enters as a
/// patient reads them out, reach another provider through the FHIR API as Observations, under a consent that covers
/// their kind and their day in hospital time. A search that would reach more of them than one answer holds is refused
/// unless narrowed, and readings the consent leaves out never count toward that. A merge of duplicate records takes the
/// monitoring records along.
/// </summary>
public sealed class DeviceReadingSharingTests(HospitalServer server)
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_provider_reads_the_home_readings_each_consent_covers_by_their_kind()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var enrollment = await EnrolAsync(doctor, patient);
        var serial = "OM-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var key = await doctor.PostAsync<DeviceKeyIssued>("/api/v1/rpm/devices",
            new RegisterDeviceRequest(serial, RpmDeviceType.BpMonitor, "Omron", "HEM-7156", enrollment));

        // The monitor sends a blood pressure and a pulse taken together; a nurse enters the glucose and the ECG patch's
        // rhythm the patient read out on a call.
        var measured = Minute(DateTimeOffset.UtcNow.AddHours(-2));
        await PushAsync(key, measured, ("SystolicBp", 150m), ("DiastolicBp", 95m), ("HeartRate", 88m));
        await nurse.PostAsync<IngestResult>($"/api/v1/rpm/enrollments/{enrollment}/readings", new RecordReadingsRequest(
        [
            new ManualReading(RpmReadingType.Glucose, 6.5m, "mmol/L", measured.AddMinutes(-30), null),
            new ManualReading(RpmReadingType.EcgRhythm, 2m, null, measured.AddMinutes(-20), "Atrial fibrillation suspected"),
        ]));
        var readings = await ReadingsAsync(enrollment);
        var (panel, diastolic, pulse, glucose, rhythm) = (Rpm(readings[RpmReadingType.SystolicBp].Id), Rpm(readings[RpmReadingType.DiastolicBp].Id),
            Rpm(readings[RpmReadingType.HeartRate].Id), Rpm(readings[RpmReadingType.Glucose].Id), Rpm(readings[RpmReadingType.EcgRhythm].Id));

        // The patient shares their lab results with the partner for treatment: the glucose goes with them, by glucometer as
        // a bedside glucose does; the rest is withheld, the blood pressure panel counted once.
        await ShareAsync(frontOffice, patient, [SharedRecordKind.LabResults], null);
        var labs = await FhirAsync(partner, $"Observation?patient={patient}", "TREAT");
        Matches(labs.Body).ShouldBe([glucose]);
        Withheld(labs.Body).ShouldBeTrue();
        (await LastSearchAsync(patient)).Withheld.ShouldBe(3, "the panel, the pulse and the rhythm");
        var sugar = Resource(labs.Body, glucose);
        Code(sugar.GetProperty("code")).ShouldBe(("http://loinc.org", "41653-7"));
        (sugar.GetProperty("valueQuantity").GetProperty("value").GetDecimal(), sugar.GetProperty("valueQuantity").GetProperty("code").GetString())
            .ShouldBe((117.1m, "mg/dL"), "kept in the canonical unit it was converted to");
        sugar.GetProperty("note")[0].GetProperty("text").GetString().ShouldStartWith("Read out by the patient and entered by ");
        sugar.TryGetProperty("device", out _).ShouldBeFalse();

        // Then their vital signs: the panel under the systolic reading's id, and the pulse.
        await ShareAsync(frontOffice, patient, [SharedRecordKind.VitalSigns], null);
        var vitals = await FhirAsync(partner, $"Observation?patient={patient}", "TREAT");
        Matches(vitals.Body).ShouldBe([glucose, panel, pulse], ignoreOrder: true);
        (await LastSearchAsync(patient)).Withheld.ShouldBe(1, "the rhythm");
        var bp = Resource(vitals.Body, panel);
        Code(bp.GetProperty("code")).ShouldBe(("http://loinc.org", "85354-9"));
        bp.GetProperty("meta").GetProperty("profile")[0].GetString().ShouldBe("http://hl7.org/fhir/StructureDefinition/bp");
        bp.GetProperty("component").EnumerateArray().Select(c => (Code(c.GetProperty("code")).Code, c.GetProperty("valueQuantity").GetProperty("value").GetDecimal(),
            Interpretation(c))).ShouldBe([("8480-6", 150m, Expected(readings[RpmReadingType.SystolicBp].Severity)), ("8462-4", 95m, Expected(readings[RpmReadingType.DiastolicBp].Severity))]);
        Interpretation(bp).ShouldBe(Expected((ReadingSeverity)Math.Max((int)readings[RpmReadingType.SystolicBp].Severity, (int)readings[RpmReadingType.DiastolicBp].Severity)));
        bp.GetProperty("effectiveDateTime").GetString().ShouldBe(measured.ToOffset(Ist).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        bp.GetProperty("performer")[0].GetProperty("reference").GetString().ShouldBe($"Patient/{patient}");
        bp.GetProperty("device").GetProperty("display").GetString().ShouldBe($"Omron HEM-7156 blood pressure monitor (serial {serial})");

        // Read by id: the panel, both readings in it; the diastolic reading has no id of its own; the rhythm is outside
        // every consent.
        var read = await FhirAsync(partner, $"Observation/{panel}", "TREAT");
        read.Status.ShouldBe(200);
        read.Body.GetProperty("component").EnumerateArray().Select(c => c.GetProperty("valueQuantity").GetProperty("value").GetDecimal()).ShouldBe([150m, 95m]);
        (await FhirAsync(partner, $"Observation/{diastolic}", "TREAT")).Status.ShouldBe(404);
        (await FhirAsync(partner, $"Observation/{rhythm}", "TREAT")).Refusal.ShouldBe((403, "consent-required"));

        // Searches narrow them as any observation: by code, by category and by date.
        Matches((await FhirAsync(partner, $"Observation?patient={patient}&code=http://loinc.org|8867-4", "TREAT")).Body).ShouldBe([pulse]);
        Matches((await FhirAsync(partner, $"Observation?patient={patient}&category=vital-signs", "TREAT")).Body).ShouldBe([panel, pulse], ignoreOrder: true);
        Matches((await FhirAsync(partner, $"Observation?patient={patient}&date=lt{Day(measured.AddDays(-1))}", "TREAT")).Body).ShouldBeEmpty();

        // Then their heart tests: the rhythm, in the device's words, as a procedure's finding.
        await ShareAsync(frontOffice, patient, [SharedRecordKind.ScansAndHeartTests], null);
        var everything = await FhirAsync(partner, $"Observation?patient={patient}", "TREAT");
        Matches(everything.Body).ShouldBe([glucose, panel, pulse, rhythm], ignoreOrder: true);
        Withheld(everything.Body).ShouldBeFalse();
        var ecg = Resource(everything.Body, rhythm);
        Code(ecg.GetProperty("category")[0]).ShouldBe(("http://terminology.hl7.org/CodeSystem/observation-category", "procedure"));
        ecg.GetProperty("valueCodeableConcept").GetProperty("text").GetString().ShouldBe("Atrial fibrillation suspected");
    }

    [Fact]
    public async Task A_consent_for_some_days_shares_the_readings_measured_on_those_days_in_hospital_time()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var enrollment = await EnrolAsync(doctor, patient);

        // Two weights half a minute either side of midnight in India, a day apart in UTC by no more.
        var yesterday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).Date).AddDays(-1);
        var lateEvening = new DateTimeOffset(yesterday.AddDays(-1).ToDateTime(new TimeOnly(23, 59, 30)), Ist);
        var pastMidnight = new DateTimeOffset(yesterday.ToDateTime(new TimeOnly(0, 0, 30)), Ist);
        await nurse.PostAsync<IngestResult>($"/api/v1/rpm/enrollments/{enrollment}/readings", new RecordReadingsRequest(
            [new ManualReading(RpmReadingType.Weight, 70m, "kg", lateEvening, null), new ManualReading(RpmReadingType.Weight, 70.4m, "kg", pastMidnight, null)]));
        var weights = await AllReadingsAsync(enrollment);
        var (before, after) = (Rpm(weights.Single(r => r.MeasuredAt == lateEvening).Id), Rpm(weights.Single(r => r.MeasuredAt == pastMidnight).Id));

        // The consent shares vital signs recorded from yesterday on, with anyone it names, for any purpose.
        await ShareAsync(frontOffice, patient, [SharedRecordKind.VitalSigns], yesterday, partnerId);

        var search = await FhirAsync(partner, $"Observation?patient={patient}", null);
        Matches(search.Body).ShouldBe([after]);
        Withheld(search.Body).ShouldBeTrue();
        (await FhirAsync(partner, $"Observation/{after}", null)).Status.ShouldBe(200);
        (await FhirAsync(partner, $"Observation/{before}", null)).Refusal.ShouldBe((403, "consent-required"));
    }

    [Fact]
    public async Task A_search_reaching_more_readings_than_one_answer_holds_is_refused_unless_narrowed_and_readings_outside_the_consent_never_count()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (vitalsPartner, vitalsPartnerId, _) = await PartnerAsync(server);
        var (labPartner, labPartnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var enrollment = await EnrolAsync(doctor, patient);

        // A pulse every minute for three and a half days: one reading more than a search loads.
        var last = Minute(DateTimeOffset.UtcNow.AddMinutes(-5));
        var measured = await PulsesAsync(patient, enrollment, last, 5001);
        await ShareAsync(frontOffice, patient, [SharedRecordKind.VitalSigns], null, vitalsPartnerId);
        await ShareAsync(frontOffice, patient, [SharedRecordKind.LabResults], null, labPartnerId);

        (await FhirAsync(vitalsPartner, $"Observation?patient={patient}", null)).Refusal.ShouldBe((400, "too-many-records"));
        var today = Day(last);
        var narrowed = await FhirAsync(vitalsPartner, $"Observation?patient={patient}&date=ge{today}", null);
        narrowed.Status.ShouldBe(200);
        narrowed.Body.GetProperty("total").GetInt32().ShouldBe(measured.Count(m => Day(m) == today));

        // To a partner the consents share no vital signs with, the readings are withheld and never counted: no refusal,
        // which would tell them the patient has many.
        var labs = await FhirAsync(labPartner, $"Observation?patient={patient}", null);
        labs.Status.ShouldBe(200);
        Matches(labs.Body).ShouldBeEmpty();
        Withheld(labs.Body).ShouldBeTrue();
        (await LastSearchAsync(patient)).Withheld.ShouldBe(5001);
    }

    [Fact]
    public async Task A_merge_of_duplicate_records_takes_the_enrolments_readings_and_devices_along_and_audits_what_moved()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var duplicate = await RegisterAsync(frontOffice);
        var kept = await RegisterAsync(frontOffice);
        var enrollment = await EnrolAsync(doctor, duplicate);
        var key = await doctor.PostAsync<DeviceKeyIssued>("/api/v1/rpm/devices",
            new RegisterDeviceRequest("PO-" + Guid.NewGuid().ToString("N")[..10], RpmDeviceType.PulseOximeter, null, null, enrollment));
        var measured = Minute(DateTimeOffset.UtcNow.AddHours(-1));
        await PushAsync(key, measured, ("SpO2", 95m), ("HeartRate", 76m));

        await MakerChecker.MergeAsync(
            await server.SignInAsync(HospitalServer.Users.MrdOfficer), await server.SignInAsync(HospitalServer.Users.MrdVerifier), duplicate, kept, "Registered twice");

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<RpmEnrollment>().IgnoreQueryFilters().SingleAsync(e => e.Id == enrollment, Ct)).PatientId.ShouldBe(kept);
            (await db.Set<RpmReading>().IgnoreQueryFilters().Where(r => r.EnrollmentId == enrollment).Select(r => r.PatientId).Distinct().ToListAsync(Ct)).ShouldBe([kept]);
            (await db.Set<RpmDevice>().IgnoreQueryFilters().SingleAsync(d => d.Id == key.DeviceId, Ct)).PatientId.ShouldBe(kept);
            var moved = await db.Set<AuditEvent>().AsNoTracking()
                .Where(a => a.Action == MergeHandler.RecordsMovedAction && a.EntityId == duplicate.ToString()).ToListAsync(Ct);
            var entry = moved.ShouldHaveSingleItem();
            (entry.PatientId, entry.Category).ShouldBe(((Guid?)kept, AuditCategories.Data));
            using var details = JsonDocument.Parse(entry.Details!);
            Ids(details.RootElement.GetProperty("enrollments")).ShouldBe([enrollment]);
            Ids(details.RootElement.GetProperty("devices")).ShouldBe([key.DeviceId]);
            details.RootElement.GetProperty("readings").GetInt32().ShouldBe(2);
        }

        // The device goes on reporting, now for the surviving record, and everything is shared under its consent.
        await PushAsync(key, measured.AddMinutes(10), ("SpO2", 96m));
        await ShareAsync(frontOffice, kept, [SharedRecordKind.VitalSigns], null, partnerId);
        Matches((await FhirAsync(partner, $"Observation?patient={kept}", null)).Body).Count.ShouldBe(3);
    }

    private static string Rpm(Guid readingId) => $"rpm-{readingId:D}";

    private static DateTimeOffset Minute(DateTimeOffset instant) => new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerMinute), instant.Offset);

    /// <summary>The day in hospital time, as a FHIR date.</summary>
    private static string Day(DateTimeOffset instant) => instant.ToOffset(Ist).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Expected(ReadingSeverity severity) => severity switch
    {
        ReadingSeverity.Normal => "N",
        ReadingSeverity.Warning => "A",
        _ => "AA",
    };

    private static string? Interpretation(JsonElement observation) =>
        observation.GetProperty("interpretation")[0].GetProperty("coding")[0].GetProperty("code").GetString();

    private static (string? System, string? Code) Code(JsonElement concept)
    {
        var coding = concept.GetProperty("coding")[0];
        return (coding.GetProperty("system").GetString(), coding.GetProperty("code").GetString());
    }

    /// <summary>A match of a search bundle by id (the bundle's outcome entry, saying records were withheld, has none).</summary>
    private static JsonElement Resource(JsonElement bundle, string id) =>
        bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource"))
            .Single(r => r.TryGetProperty("id", out var value) && value.GetString() == id);

    private static List<Guid> Ids(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetGuid())];

    /// <summary>The doctor enrols the patient in hypertension monitoring, with themself as the care team.</summary>
    private static async Task<Guid> EnrolAsync(ApiClient doctor, Guid patient)
    {
        var practitioner = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var program = (await doctor.GetAsync<List<RpmProgramDto>>("/api/v1/rpm/programs")).Single(p => p.Code == "HTN");
        return (await doctor.PostAsync<Created>("/api/v1/rpm/enrollments",
            new EnrollPatientRequest(patient, program.Id, [practitioner], practitioner, StartDate: null, Goals: null, Notes: null))).Id;
    }

    /// <summary>The device uploads readings taken together, with its key, as a device does (no signed-in user).</summary>
    private async Task PushAsync(DeviceKeyIssued key, DateTimeOffset measuredAt, params (string Type, decimal Value)[] readings)
    {
        var body = JsonSerializer.Serialize(new
        {
            readings = readings.Select(r => new { type = r.Type, value = r.Value, measuredAt = measuredAt.ToString("O", CultureInfo.InvariantCulture) }),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, key.IngestPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(key.HeaderName, key.ApiKey);
        using var response = await server.Anonymous().Http.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A pulse every minute up to <paramref name="last"/>, as a patch filing them would; the times measured.</summary>
    private async Task<List<DateTimeOffset>> PulsesAsync(Guid patient, Guid enrollment, DateTimeOffset last, int count)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var facility = await db.Set<RpmEnrollment>().IgnoreQueryFilters().Where(e => e.Id == enrollment).Select(e => e.FacilityId).SingleAsync(Ct);
        var measured = Enumerable.Range(0, count).Select(i => last.AddMinutes(-i).ToUniversalTime()).ToList();
        db.AddRange(measured.Select(at => new RpmReading
        {
            FacilityId = facility,
            EnrollmentId = enrollment,
            PatientId = patient,
            Type = RpmReadingType.HeartRate,
            Value = 72,
            Unit = "bpm",
            MeasuredAt = at,
            ReceivedAt = at.AddSeconds(20),
            Severity = ReadingSeverity.Normal,
            Source = ReadingSource.Device,
        }));
        await db.SaveChangesAsync(Ct);
        return measured;
    }

    private async Task<Dictionary<RpmReadingType, RpmReading>> ReadingsAsync(Guid enrollment) =>
        (await AllReadingsAsync(enrollment)).ToDictionary(r => r.Type);

    private async Task<List<RpmReading>> AllReadingsAsync(Guid enrollment)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<RpmReading>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EnrollmentId == enrollment).ToListAsync(Ct);
    }

    /// <summary>The patient shares these kinds of records, from a day on when given, with one account or the partner set.</summary>
    private static Task<Created> ShareAsync(ApiClient frontOffice, Guid patient, IReadOnlyList<SharedRecordKind> kinds, DateOnly? from, Guid? with = null) =>
        frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope(with is { } account ? [account] : null, with is null ? [SharingPurpose.Treatment] : null, kinds, from, null, null)));

    /// <summary>The last FHIR search audited for the patient: how many records it left out.</summary>
    private async Task<(int Returned, int Withheld)> LastSearchAsync(Guid patient)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var details = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patient && a.Summary != null && a.Summary.StartsWith("FHIR R4 search"))
            .OrderByDescending(a => a.OccurredAt).Select(a => a.Details).FirstAsync(Ct);
        using var json = JsonDocument.Parse(details!);
        return (json.RootElement.GetProperty("returned").GetInt32(), json.RootElement.GetProperty("withheld").GetInt32());
    }
}
