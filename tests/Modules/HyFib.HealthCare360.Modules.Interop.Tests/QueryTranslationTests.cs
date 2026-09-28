using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Diagnostics;
using HyFib.HealthCare360.Modules.Inpatient;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients;
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
            [new PatientsModule(), new ClinicalModule(), new DiagnosticsModule(), new InpatientModule(), new InteropModule()]));
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
        provider.SignedSummaries(s => s.PatientId == TestData.PatientId).ToQueryString().ShouldContain("'Draft'");
    }

    [Fact]
    public void The_consent_history_query_reads_data_sharing_consents() =>
        new PatientSharingService(_db, null!).Consents(TestData.PatientId).ToQueryString().ShouldContain("'DataSharing'");

    [Fact]
    public void The_patient_search_filters_consent_and_restriction_in_sql()
    {
        var protector = Substitute.For<IFieldProtector>();
        protector.BlindIndex(Arg.Any<string>()).Returns("index");
        var records = new PatientRecords(_db, protector, Substitute.For<IPermissionService>(), Substitute.For<ICurrentUser>(), References, NullLogger<PatientRecords>.Instance);

        var sql = Where(records.SearchQuery(TestData.Search("Patient", "name=meera kul&identifier=91-1234-5678-9012&birthdate=ge1980"), includeRestricted: false));

        sql.ShouldContain("'DataSharing'");
        sql.ShouldContain("is_vip");
        sql.ShouldContain("merged_into_patient_id IS NULL");
        sql.ShouldContain("ILIKE @", Case.Sensitive);
        sql.ShouldNotContain("meera", Case.Insensitive);
        Where(records.SearchQuery(TestData.Search("Patient", "name=meera"), includeRestricted: true)).ShouldNotContain("is_vip");
    }

    /// <summary>The filter part of the SQL (the select list names every column; parameter values are comments above it).</summary>
    private static string Where(IQueryable<Patients.Domain.Patient> query)
    {
        var sql = query.ToQueryString();
        return sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
    }
}
