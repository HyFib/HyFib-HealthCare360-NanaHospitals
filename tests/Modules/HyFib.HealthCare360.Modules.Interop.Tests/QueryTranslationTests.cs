using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics;
using HyFib.HealthCare360.Modules.Inpatient;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Every provider query translates to PostgreSQL (checked with the Npgsql provider, without a connection): shareable
/// rows only (released, signed), enums compared as their stored text, and the soft-delete filter applied.
/// </summary>
public sealed class QueryTranslationTests : IDisposable
{
    private readonly HospitalDbContext _db;

    public QueryTranslationTests()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new HospitalDbContext(options, new ModuleCatalog(
            [new PatientsModule(), new ClinicalModule(), new DiagnosticsModule(), new InpatientModule(), new VirtualCareModule(), new InteropModule()]));
    }

    public void Dispose() => _db.Dispose();

    private ReferenceDirectoryLoader References => new(_db);

    [Fact]
    public void Encounter_queries_translate()
    {
        var provider = new EncounterProvider(_db, References);

        var visits = provider.Visits(v => v.PatientId == TestData.PatientId).ToQueryString();
        visits.ShouldContain("patients.visits");
        visits.ShouldContain("is_deleted");
        provider.Encounters(e => e.PatientId == TestData.PatientId).ToQueryString().ShouldContain("clinical");
        provider.Admissions([TestData.PatientId]).ToQueryString().ShouldContain("inpatient");
    }

    [Fact]
    public void Observation_queries_translate_and_share_released_results_only()
    {
        var provider = new ObservationProvider(_db);

        provider.Vitals(v => v.PatientId == TestData.PatientId).ToQueryString().ShouldContain("clinical");
        var results = provider.ReleasedResults(r => r.PatientId == TestData.PatientId).ToQueryString();
        results.ShouldContain("'Verified'");
        results.ShouldContain("'Released'");
        results.ShouldContain("LEFT JOIN");
        KeepsFilters(results, "l", facility: true);
        results.ShouldNotContain("lab_tests", Case.Insensitive, "the test master is read apart, with the soft-delete filter off for it alone");
    }

    /// <summary>
    /// A search's home-monitoring readings: its types and measurement window and the consents' kinds and days are all in
    /// the SQL, with instants as UTC parameters (Npgsql refuses any other offset for timestamptz), so readings outside them
    /// are never loaded; the readings are read with the facility filter on, the device apart.
    /// </summary>
    [Fact]
    public void Device_reading_queries_put_the_search_and_the_consent_in_sql()
    {
        var provider = new ObservationProvider(_db);
        var request = TestData.Search(FhirResourceTypes.Observation, $"patient={TestData.PatientId}&category=vital-signs&date=ge2026-10-01&date=lt2026-11-01");
        var access = new PatientAccess(SharingDecision.Allowed, null,
            [new SharingScope([], [], [SharedRecordKind.VitalSigns], new DateOnly(2026, 10, 10), new DateOnly(2026, 12, 31), null)]);

        var (covered, uncovered) = provider.Readings(TestData.PatientId, request, access).ShouldNotBeNull();

        var sql = ObservationProvider.DeviceReadings(covered).ToQueryString();
        sql.ShouldContain("virtualcare.rpm_readings");
        sql.ShouldContain("measured_at >=");
        sql.ShouldContain("measured_at <");
        sql.ShouldContain("'2026-10-09T18:30:00.0000000+00:00'", Case.Sensitive, "the consent's first day starts at IST midnight, sent in UTC");
        sql.ShouldContain("'2026-09-30T18:29:59.0000000+00:00'", Case.Sensitive, "the search's window, a second wider, sent in UTC");
        sql.ShouldContain("@ef_filter__", Case.Sensitive, "the readings keep the facility filter");
        sql.ShouldNotContain("rpm_devices", Case.Insensitive, "the device is read apart, with the filters off for it alone");
        uncovered.ToQueryString().ShouldContain("NOT (");
        provider.Readings(TestData.PatientId, TestData.Search(FhirResourceTypes.Observation, $"patient={TestData.PatientId}&category=survey"), access).ShouldBeNull();
    }

    [Fact]
    public void Condition_and_allergy_queries_translate()
    {
        new ConditionProvider(_db).Diagnoses(d => d.PatientId == TestData.PatientId).ToQueryString().ShouldContain("icd_code");
        new AllergyIntoleranceProvider(_db).Allergies(a => a.PatientId == TestData.PatientId).ToQueryString().ShouldContain("allergen");
    }

    [Fact]
    public void Prescription_query_shares_signed_prescriptions_only()
    {
        var sql = new MedicationRequestProvider(_db, References, TimeProvider.System)
            .SignedLines(i => true, p => p.PatientId == TestData.PatientId).ToQueryString();

        sql.ShouldContain("'Signed'");
        sql.ShouldContain("'Cancelled'");
        sql.ShouldContain("signed_at IS NOT NULL");
    }

    [Fact]
    public void Diagnostic_report_queries_translate()
    {
        var provider = new DiagnosticReportProvider(_db);
        Guid[] ids = [TestData.PatientId];

        var lab = provider.LabReports(r => r.PatientId == TestData.PatientId).ToQueryString();
        lab.ShouldContain("'Released'");
        lab.ShouldContain("'Amended'");
        provider.ReleasedResults(ids).ToQueryString().ShouldContain("'Verified'");
        provider.Collections(ids).ToQueryString().ShouldContain("min(");
        provider.RadiologyReports(r => r.PatientId == TestData.PatientId).ToQueryString().ShouldContain("'Released'");
        provider.Addenda(ids).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        provider.CardiologyReports(r => r.PatientId == TestData.PatientId).ToQueryString().ShouldContain("'Released'");
        provider.CardiologyStudies(ids).ToQueryString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Document_queries_leave_out_identity_documents_and_draft_summaries()
    {
        var provider = new DocumentReferenceProvider(_db, References);

        var documents = provider.PatientDocuments(d => d.PatientId == TestData.PatientId).ToQueryString();
        documents.ShouldContain("'IdProof'");
        documents.ShouldContain("'InsuranceCard'");
        KeepsFilters(documents, "p", facility: false);
        documents.ShouldNotContain("stored_files", Case.Insensitive, "the file is read apart, with the soft-delete filter off for it alone");
        provider.SignedSummaries(s => s.PatientId == TestData.PatientId).ToQueryString().ShouldContain("'Draft'");
    }

    [Fact]
    public void The_note_query_shares_signed_notes_only_and_leaves_out_removed_notes_and_other_facilities_notes()
    {
        var sql = new DocumentReferenceProvider(_db, References).SignedNotes(n => n.PatientId == TestData.PatientId).ToQueryString();

        sql.ShouldContain("is_signed");
        sql.ShouldContain("signed_at");
        KeepsFilters(sql, "c", facility: true);
        sql.ShouldNotContain("identity.users", Case.Insensitive, "the accounts are read apart, with the soft-delete filter off for them alone");
        sql.ShouldNotContain("clinical.encounters", Case.Insensitive, "the encounter is read apart, with the facility filter off for it alone");
    }

    [Fact]
    public void The_procedure_query_leaves_out_removed_procedures_and_other_facilities_procedures()
    {
        // Staff see a patient's procedures from every facility; a partner sees its own facility's, as for clinical notes.
        var sql = new ProcedureProvider(_db, References).Procedures(p => p.PatientId == TestData.PatientId).ToQueryString();

        sql.ShouldContain("clinical.procedure_records");
        KeepsFilters(sql, "p", facility: true);
    }

    /// <summary>
    /// The query reads its own table with the platform's filters on: removed rows left out, and other facilities' rows
    /// when the table is a facility's. A query that ignores a filter for a table it joins (to name a removed account, say)
    /// ignores it for every table it reads, so such lookups are made apart.
    /// </summary>
    private static void KeepsFilters(string sql, string alias, bool facility)
    {
        sql.ShouldContain($"NOT ({alias}.is_deleted)", Case.Sensitive, "removed rows are left out");
        if (facility)
        {
            sql.ShouldContain("@ef_filter__", Case.Sensitive, "other facilities' rows are left out");
        }
    }

    [Fact]
    public void A_prescriptions_lines_translate_whatever_its_status()
    {
        var sql = MedicationRequestProvider.LineRows(
            _db.Set<PrescriptionItem>().Where(i => i.PrescriptionId == TestData.PatientId),
            _db.Set<Prescription>().Where(p => p.Id == TestData.PatientId)).ToQueryString();

        sql.ShouldContain("prescription_items");
        sql.ShouldNotContain("'Signed'");
    }

    [Fact]
    public void The_consent_history_query_reads_data_sharing_consents_with_their_scopes()
    {
        var sql = new PatientSharingService(_db, null!).Consents(TestData.PatientId).ToQueryString();

        sql.ShouldContain("'DataSharing'");
        sql.ShouldContain("share_with");
        sql.ShouldContain("share_purposes");
        sql.ShouldContain("share_kinds");
        sql.ShouldContain("share_until");
    }

    [Fact]
    public void The_patient_search_filters_consent_and_restriction_in_sql()
    {
        var protector = Substitute.For<IFieldProtector>();
        protector.BlindIndexes(Arg.Any<string>()).Returns(["index"]);
        var records = new PatientRecords(_db, protector, Substitute.For<IPermissionService>(), Substitute.For<ICurrentUser>(), References, NullLogger<PatientRecords>.Instance);

        var sql = Where(records.SearchQuery(
            TestData.Search("Patient", "name=meera kul&identifier=91-1234-5678-9012&birthdate=ge1980"), includeRestricted: false, RecordSharingConsentTests.Reader));

        sql.ShouldContain("'DataSharing'");
        sql.ShouldContain("cardinality(p0.share_with) = 0", Case.Insensitive);
        sql.ShouldContain("= ANY (p0.share_with)", Case.Insensitive);
        sql.ShouldContain("= ANY (p0.share_purposes)", Case.Insensitive);
        sql.ShouldContain("share_until IS NULL", Case.Insensitive);
        sql.ShouldContain("is_vip");
        sql.ShouldContain("merged_into_patient_id IS NULL");
        sql.ShouldContain("ILIKE @", Case.Sensitive);
        sql.ShouldNotContain("meera", Case.Insensitive);
        Where(records.SearchQuery(TestData.Search("Patient", "name=meera"), includeRestricted: true, RecordSharingConsentTests.Reader)).ShouldNotContain("is_vip");
    }

    [Fact]
    public void A_patient_search_is_ordered_in_sql_by_the_values_it_is_served_with()
    {
        string Order(string sort)
        {
            var sql = PatientSearchQuery.Ordered(_db.Set<Patients.Domain.Patient>(), TestData.Search("Patient", $"name=meera&_sort={sort}").Sort).ToQueryString();
            return sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        }

        var order = Order("name,-birthdate,gender,_lastUpdated,_id");
        order.ShouldContain("lower(", Case.Insensitive);
        order.ShouldContain("COLLATE \"C\"");
        order.ShouldContain("make_date(", Case.Insensitive);
        order.ShouldContain("date_of_birth IS NULL", Case.Insensitive);
        Order("birthdate").ShouldContain("make_date(", Case.Insensitive);
        Order("-_lastUpdated").ShouldContain("date_trunc('milliseconds',", Case.Sensitive, "to the millisecond it is served with");
        Order("gender").ShouldEndWith("END, p.registered_at DESC, p.id", Case.Sensitive, "then newest registrations first, then by id");
    }

    /// <summary>The filter part of the SQL (the select list names every column; parameter values are comments above it).</summary>
    private static string Where(IQueryable<Patients.Domain.Patient> query)
    {
        var sql = query.ToQueryString();
        return sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
    }
}
