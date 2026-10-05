using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Seed;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Importing the ICD-10 catalogue from a hospital's WHO file (Medical records › ICD catalogue › Import). The file here is
/// made in the shape of WHO's ClaML: HC360's own starter codes with their titles, their categories, and enough further
/// categories to pass the check that refuses partial files. The database is shared by the whole run: the file keeps
/// every starter code exactly as it is, and each test that changes the catalogue puts it back as it found it
/// (<see cref="CatalogueState"/>), so other journeys see the starter list and the built-in chapters, as on a new
/// server.
/// </summary>
public sealed class CatalogueImportTests(HospitalServer server)
{
    private const string Imports = "/api/v1/mrd/catalogue/imports";
    private const string Summaries = "/api/v1/clinical/discharge-summaries";
    private const string Codings = "/api/v1/mrd/codings";

    [Fact]
    public async Task A_doctor_cannot_import_a_catalogue()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);

        using var response = await UploadAsync(doctor, Encoding.UTF8.GetBytes(Claml(TinyCategories())), "icd102019en.xml", dryRun: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_partial_file_is_refused_with_the_reason()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var claml = await UploadAsync(admin, Encoding.UTF8.GetBytes(Claml(TinyCategories())), "icd102019en.xml", dryRun: true);
        using var orderFile = await UploadAsync(admin, Encoding.UTF8.GetBytes("00001 0016070 1 Bypass Cereb Vent to Nasophar with Autol Sub, Open Approach\n"),
            "icd10pcs_order_2025.txt", dryRun: true);

        claml.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await claml.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("only 3 categories");
        orderFile.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await orderFile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("only 1 valid code;");
    }

    [Fact]
    public async Task The_who_file_is_checked_imported_recorded_and_searchable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var restore = await CatalogueState.TakeAsync(server);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var categories = WhoLikeCategories();

        // A code whose title is exactly what a coder types, past the first 300 matches in code order: the database must
        // order the candidates by relevance before it cuts them to 300, or this best match is lost.
        static bool IsTestCondition(string title) => title.StartsWith("Test condition", StringComparison.Ordinal);
        var bestMatch = categories.Last(c => c.Key.Length == 3 && IsTestCondition(c.Value)).Key;
        categories[bestMatch] = "Test condition";
        categories.Keys.TakeWhile(c => c != bestMatch).Count(c => IsTestCondition(categories[c]))
            .ShouldBeGreaterThan(300);
        var file = Encoding.UTF8.GetBytes(Claml(categories));
        var sha256 = Convert.ToHexString(SHA256.HashData(file));
        var starter = IcdCodeCatalogue.All.Count;
        var filler = categories.Keys.First(c => c.Length == 5 && !IcdCodeCatalogue.All.Any(e => e.Code == c));

        // Checking the file changes nothing.
        var check = await ReadAsync<CatalogueImportResult>(await UploadAsync(admin, file, "icd102019en.xml", dryRun: true));
        check.ShouldSatisfyAllConditions(
            r => r.DryRun.ShouldBeTrue(),
            r => r.Id.ShouldBeNull(),
            r => r.CodesInFile.ShouldBe(categories.Count),
            r => r.Added.ShouldBe(categories.Count - starter),
            r => r.Withdrawn.ShouldBe(0));
        (await SearchAsync(admin, filler)).ShouldNotContain(c => c.Code == filler);

        // The import itself.
        var imported = await ReadAsync<CatalogueImportResult>(await UploadAsync(admin, file, "icd102019en.xml", dryRun: false));
        imported.ShouldSatisfyAllConditions(
            r => r.DryRun.ShouldBeFalse(),
            r => r.Id.ShouldNotBeNull(),
            r => r.Release.ShouldBe(CodeReleases.Icd10),
            r => r.Sha256.ShouldBe(sha256),
            r => r.SourceTitle.ShouldBe("ICD-10 2019"),
            r => r.Added.ShouldBe(categories.Count - starter),
            r => r.Unchanged.ShouldBe(starter, "the file keeps every starter code as it is"),
            r => r.Updated.ShouldBe(0),
            r => r.Withdrawn.ShouldBe(0),
            r => r.Chapters.ShouldBe(IcdClassification.Chapters.Count));

        var status = await admin.GetAsync<CatalogueStatusDto>("/api/v1/mrd/catalogue");
        var icd10 = status.Catalogues.Single(c => c.Release == CodeReleases.Icd10);
        icd10.ShouldSatisfyAllConditions(
            c => c.ClassificationFromFile.ShouldBe(true),
            c => c.FromFile.ShouldBe(categories.Count),
            c => c.FromStarterList.ShouldBe(0),
            c => c.LastImport.ShouldNotBeNull().Sha256.ShouldBe(sha256));
        status.RecentImports.First().ImportedBy.ShouldNotBeNullOrWhiteSpace();

        // Doctors and coders find the new codes, ordered exact code first, and the starter codes still work.
        (await SearchAsync(admin, filler)).First().Code.ShouldBe(filler);
        (await SearchAsync(admin, "test condition")).First().Code.ShouldBe(bestMatch);
        (await SearchAsync(admin, "test co")).ShouldAllBe(c => c.Title.Contains(" co", StringComparison.OrdinalIgnoreCase), "a two-letter word matches the start of a word");
        var diabetes = (await SearchAsync(admin, "E11.9")).First();
        diabetes.ShouldSatisfyAllConditions(d => d.Code.ShouldBe("E11.9"), d => d.IsActive.ShouldBeTrue(), d => d.IsBillable.ShouldBeTrue());

        var chapters = await admin.GetAsync<List<IcdChapterDto>>("/api/v1/mrd/icd-classification");
        chapters.Count.ShouldBe(IcdClassification.Chapters.Count);
        chapters.Single(c => c.Number == "IV").Blocks.ShouldContain(b => b.Range == "E10-E14");

        // The same file again changes nothing, and the import is in the audit trail.
        var again = await ReadAsync<CatalogueImportResult>(await UploadAsync(admin, file, "icd102019en.xml", dryRun: false));
        again.ShouldSatisfyAllConditions(r => r.Added.ShouldBe(0), r => r.Updated.ShouldBe(0), r => r.Unchanged.ShouldBe(categories.Count));

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Set<AuditEvent>().CountAsync(e => e.Action == "mrd.catalogue.imported", ct)).ShouldBeGreaterThanOrEqualTo(2);

        // Put back, the catalogue is the starter list with the built-in chapters again, for the tests that follow.
        await restore.DisposeAsync();
        (await Icd10StatusAsync(admin)).ShouldSatisfyAllConditions(
            c => c.ClassificationFromFile.ShouldBe(false),
            c => c.FromFile.ShouldBe(0),
            c => c.FromStarterList.ShouldBe(starter),
            c => c.LastImport.ShouldBeNull());
        (await SearchAsync(admin, filler)).ShouldNotContain(c => c.Code == filler);
    }

    [Fact]
    public async Task Codes_switched_off_or_edited_here_keep_the_hospitals_decision_through_later_imports()
    {
        await using var restore = await CatalogueState.TakeAsync(server);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var categories = WhoLikeCategories();
        var fillers = categories.Keys.Where(c => c.Length == 5 && !IcdCodeCatalogue.All.Any(e => e.Code == c))
            .Take(2).ToList();
        var (switchedOff, dropped) = (fillers[0], fillers[1]);
        var edited = IcdCodeCatalogue.All.First(e => e.Billable && e.Code is not ("E11.9" or "L03.1")).Code;
        var full = Encoding.UTF8.GetBytes(Claml(categories));
        var partial = Encoding.UTF8.GetBytes(Claml(new SortedDictionary<string, string>(
            categories.Where(c => c.Key != switchedOff && c.Key != dropped).ToDictionary(), StringComparer.Ordinal)));

        var first = await ImportAsync(admin, full);

        // MRD switches one imported code off, and rewords a starter code.
        await SaveAsync(mrd, (await SearchAsync(admin, switchedOff)).First(), isActive: false);
        var reworded = await SaveAsync(mrd, (await SearchAsync(admin, edited)).First(), title: "Local wording");
        reworded.Source.ShouldBe(CodeSource.Manual, "a hand edit makes it the hospital's own code");
        (await CodeAsync(switchedOff)).Source.ShouldBe(CodeSource.Import, "switching a code off is not an edit");

        // A file without the two fillers withdraws only the one in use; the reworded code is left alone.
        var second = await ImportAsync(admin, partial);
        second.ShouldSatisfyAllConditions(r => r.Withdrawn.ShouldBe(1), r => r.LeftAlone.ShouldBe(first.LeftAlone + 1));
        (await CodeAsync(switchedOff)).ShouldSatisfyAllConditions(
            c => c.IsActive.ShouldBeFalse(), c => c.WithdrawnAt.ShouldBeNull());

        // MRD switches the withdrawn code back on: in use again, counted once, and the hospital's own from now on.
        var withdrawn = await CodeAsync(dropped);
        withdrawn.WithdrawnAt.ShouldNotBeNull();
        (await SaveAsync(mrd, ToDto(withdrawn), isActive: true)).Source.ShouldBe(CodeSource.Manual);
        (await CodeAsync(dropped)).WithdrawnAt.ShouldBeNull();
        (await Icd10StatusAsync(admin)).WithdrawnCodes.ShouldBe(0, "a code back in use is not counted as withdrawn");

        // The full file again brings nothing back that MRD decided on.
        var third = await ImportAsync(admin, full);
        third.ShouldSatisfyAllConditions(
            r => r.Reinstated.ShouldBe(0),
            r => r.Withdrawn.ShouldBe(0),
            r => r.LeftAlone.ShouldBe(first.LeftAlone + 2));
        (await CodeAsync(switchedOff)).IsActive.ShouldBeFalse();
        (await CodeAsync(edited)).Title.ShouldBe("Local wording");
        (await CodeAsync(dropped)).ShouldSatisfyAllConditions(
            c => c.IsActive.ShouldBeTrue(), c => c.Source.ShouldBe(CodeSource.Manual));
    }

    [Fact]
    public async Task Records_that_already_have_a_withdrawn_code_can_be_saved_again_but_it_cannot_be_added()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var restore = await CatalogueState.TakeAsync(server);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var codes = IcdCodeCatalogue.All.Where(e => e.Billable && e.Code is not ("E11.9" or "L03.1"))
            .Take(2).Select(e => e.Code).ToList();
        var (primary, retired) = (codes[0], codes[1]);
        CodingLineRequest[] lines =
            [new(CodingEntryKind.Diagnosis, primary, true), new(CodingEntryKind.Diagnosis, retired, false)];
        var patient = await RegisterPatientAsync(frontOffice);
        var admissionId = Guid.CreateVersion7();

        // A coding and a discharge summary record the code while it is in use.
        var coding = await CreateCodingAsync(mrd, patient.Id, admissionId);
        coding = await SaveCodingAsync(mrd, coding, lines);
        var summaryPath = $"{Summaries}/by-admission/{admissionId}";
        var summary = await doctor.PutAsync<DischargeSummaryDto>(
            summaryPath, Summary(patient.Id, [primary, retired], null));

        // An import withdraws it.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<IcdCode>().Where(c => c.Code == retired && c.Release == CodeReleases.Icd10)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.IsActive, false)
                    .SetProperty(c => c.WithdrawnAt, DateTimeOffset.UtcNow), ct);
        }

        // Both records can be saved again with it.
        coding = await SaveCodingAsync(mrd, coding, lines);
        coding.Entries.Select(e => e.Code).ShouldBe([primary, retired]);
        summary = await doctor.PutAsync<DischargeSummaryDto>(
            summaryPath, Summary(patient.Id, [primary, retired], summary.Version));
        summary.DiagnosisCodes.ShouldBe([primary, retired]);

        // A record that does not have it cannot take it.
        var other = await CreateCodingAsync(mrd, patient.Id, Guid.CreateVersion7());
        using (var refused = await mrd.SendRawAsync(
                   HttpMethod.Put, $"{Codings}/{other.Id}", new SaveCodingRequest(lines, null, other.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(ct)).ShouldContain($"{retired} is no longer in use");
        }

        var newSummary = $"{Summaries}/by-admission/{Guid.CreateVersion7()}";
        using (var refused = await doctor.SendRawAsync(
                   HttpMethod.Put, newSummary, Summary(patient.Id, [retired], null)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(ct))
                .ShouldContain($"No longer in use in the ICD catalogue: {retired}.");
        }
    }

    private static Task<CodingDto> CreateCodingAsync(ApiClient mrd, Guid patientId, Guid admissionId) =>
        mrd.PostAsync<CodingDto>(Codings, new CreateCodingRequest(patientId, null, admissionId, null, null, null));

    private static Task<CodingDto> SaveCodingAsync(ApiClient mrd, CodingDto coding, CodingLineRequest[] lines) =>
        mrd.PutAsync<CodingDto>($"{Codings}/{coding.Id}", new SaveCodingRequest(lines, null, coding.Version));

    private static SaveDischargeSummaryRequest Summary(Guid patientId, List<string> codes, uint? version) => new(
        PatientId: patientId, EncounterId: null, PractitionerId: null, AdmittedAt: null, DischargedAt: null,
        Diagnoses: "Test diagnoses", DiagnosisCodes: codes, PresentingComplaints: null, CourseInHospital: null,
        Procedures: null, InvestigationsSummary: null, ConditionAtDischarge: null, Medications: null, Advice: null,
        FollowUpDate: null, FollowUpInstructions: null, Version: version);

    private static Task<PatientCreated> RegisterPatientAsync(ApiClient frontOffice) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Coder" + Random(6, 'a', 26), MiddleName: null, LastName: "Catalogue" + Random(8, 'a', 26),
            Gender: Gender.Male, DateOfBirth: new DateOnly(1970, 1, 1).AddDays(System.Random.Shared.Next(3650)),
            AgeYears: null, AgeMonths: null, Phone: "9" + Random(9, '0', 10), AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null,
            Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));

    private static string Random(int count, char first, int range) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)(first + System.Random.Shared.Next(range))));

    /// <summary>MRD's save of a code from the catalogue screen, changing only what is given.</summary>
    private static Task<IcdCodeDto> SaveAsync(
        ApiClient mrd, IcdCodeDto code, string? title = null, bool? isActive = null) =>
        mrd.PutAsync<IcdCodeDto>($"/api/v1/mrd/icd-codes/{code.Id}", new SaveIcdCodeRequest(
            code.Code, title ?? code.Title, code.Release, code.IsBillable, code.Keywords, FromOnlineLookup: false,
            isActive ?? code.IsActive));

    private static IcdCodeDto ToDto(IcdCode c) => new(
        c.Id, c.Code, c.Title, c.Release, c.Chapter, c.Block, null, c.IsBillable, c.Usage, c.Keywords, c.IncludedTerms,
        c.Source, c.IsActive);

    private async Task<IcdCode> CodeAsync(string code)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<IcdCode>().AsNoTracking()
            .SingleAsync(c => c.Code == code && c.Release == CodeReleases.Icd10, TestContext.Current.CancellationToken);
    }

    private static async Task<CatalogueSummaryDto> Icd10StatusAsync(ApiClient client) =>
        (await client.GetAsync<CatalogueStatusDto>("/api/v1/mrd/catalogue")).Catalogues
            .Single(c => c.Release == CodeReleases.Icd10);

    private static async Task<CatalogueImportResult> ImportAsync(ApiClient admin, byte[] file) =>
        await ReadAsync<CatalogueImportResult>(await UploadAsync(admin, file, "icd102019en.xml", dryRun: false));

    private static Task<HttpResponseMessage> UploadAsync(ApiClient client, byte[] file, string fileName, bool dryRun)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        form.Add(new StringContent(dryRun ? "true" : "false"), "dryRun");
        var request = new HttpRequestMessage(HttpMethod.Post, Imports) { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            return await ApiClient.ReadAsync<T>(response, HttpMethod.Post, Imports);
        }
    }

    private static Task<List<IcdCodeDto>> SearchAsync(ApiClient client, string search) =>
        client.GetAsync<List<IcdCodeDto>>($"/api/v1/mrd/icd-codes?release=ICD-10&limit=10&search={Uri.EscapeDataString(search)}");

    /// <summary>Three categories: a file far too small to be the WHO file.</summary>
    private static SortedDictionary<string, string> TinyCategories() => new(StringComparer.Ordinal)
    {
        ["A00"] = "Cholera",
        ["A00.0"] = "Cholera due to Vibrio cholerae 01, biovar cholerae",
        ["A00.9"] = "Cholera, unspecified",
    };

    /// <summary>
    /// Every starter code with its own title, the three-character category above each of them, and further test
    /// categories (each with ten subdivisions) until there are more than 5,000 categories.
    /// </summary>
    private static SortedDictionary<string, string> WhoLikeCategories()
    {
        var categories = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in IcdCodeCatalogue.All)
        {
            categories[entry.Code] = entry.Title;
        }

        foreach (var parent in IcdCodeCatalogue.All.Where(e => e.Code.Length > 3).Select(e => e.Code[..3]).Distinct().ToList())
        {
            categories.TryAdd(parent, $"Test category {parent}");
        }

        var used = categories.Keys.Select(c => c[..3]).ToHashSet(StringComparer.Ordinal);
        var free = Enumerable.Range('A', 26).SelectMany(letter => Enumerable.Range(0, 100).Select(n => $"{(char)letter}{n:00}"))
            .Where(c => !used.Contains(c) && IcdClassification.BlockOf(c) is not null);
        foreach (var category in free)
        {
            if (categories.Count > 5_200)
            {
                break;
            }

            categories[category] = $"Test condition {category}";
            for (var digit = 0; digit <= 9; digit++)
            {
                categories[$"{category}.{digit}"] = $"Test condition {category}, type {digit}";
            }
        }

        return categories;
    }

    /// <summary>A ClaML file in WHO's layout (DOCTYPE, chapters, blocks, categories) holding the given categories.</summary>
    private static string Claml(SortedDictionary<string, string> categories)
    {
        static XElement Preferred(string title) =>
            new("Rubric", new XAttribute("kind", "preferred"), new XElement("Label", new XAttribute(XNamespace.Xml + "lang", "en"), title));

        static string BlockCode(IcdBlock block) => $"{block.Start}-{block.End}";

        var root = new XElement("ClaML", new XAttribute("version", "2.0.0"),
            new XElement("Title", new XAttribute("name", "ICD-10"), new XAttribute("version", "2019"), "International Statistical Classification of Diseases"));
        var byBlock = categories.Keys.Where(c => c.Length == 3).GroupBy(c => IcdClassification.BlockOf(c)!).ToDictionary(g => g.Key, g => g.ToList());
        var byCategory = categories.Keys.Where(c => c.Length > 3).GroupBy(c => c[..3], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var chapter in IcdClassification.Chapters)
        {
            var blocks = IcdClassification.Blocks.Where(b => IcdClassification.ChapterOf(b.Start)?.Number == chapter.Number && byBlock.ContainsKey(b)).ToList();
            if (blocks.Count == 0)
            {
                continue;
            }

            root.Add(new XElement("Class", new XAttribute("code", chapter.Number), new XAttribute("kind", "chapter"),
                blocks.Select(b => new XElement("SubClass", new XAttribute("code", BlockCode(b)))), Preferred(chapter.Title)));
            foreach (var block in blocks)
            {
                var inBlock = byBlock[block];
                root.Add(new XElement("Class", new XAttribute("code", BlockCode(block)), new XAttribute("kind", "block"),
                    new XElement("SuperClass", new XAttribute("code", chapter.Number)),
                    inBlock.Select(c => new XElement("SubClass", new XAttribute("code", c))), Preferred(block.Title)));
                foreach (var category in inBlock)
                {
                    var subdivisions = byCategory.GetValueOrDefault(category) ?? [];
                    root.Add(new XElement("Class", new XAttribute("code", category), new XAttribute("kind", "category"),
                        new XElement("SuperClass", new XAttribute("code", BlockCode(block))),
                        subdivisions.Select(c => new XElement("SubClass", new XAttribute("code", c))), Preferred(categories[category])));
                    root.Add(subdivisions.Select(c => new XElement("Class", new XAttribute("code", c), new XAttribute("kind", "category"),
                        new XElement("SuperClass", new XAttribute("code", category)), Preferred(categories[c]))));
                }
            }
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE ClaML SYSTEM \"ClaML.dtd\">\n" + root.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>
/// The ICD-10 catalogue as a test found it: the codes (with their source, state and file facts), the chapters and
/// blocks, and the import records. Disposing puts it back: codes the test's imports added are removed, the others get
/// their values back, and the classification cache is cleared, so the tests after it see the starter list as before.
/// </summary>
internal sealed class CatalogueState(
    HospitalServer server,
    List<IcdCode> codes,
    List<IcdClassificationEntry> entries,
    List<Guid> imports) : IAsyncDisposable
{
    public static async Task<CatalogueState> TakeAsync(HospitalServer server)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return new CatalogueState(
            server,
            await db.Set<IcdCode>().IgnoreQueryFilters().AsNoTracking().Where(c => c.Release == CodeReleases.Icd10)
                .ToListAsync(ct),
            await db.Set<IcdClassificationEntry>().AsNoTracking().Where(e => e.Release == CodeReleases.Icd10)
                .ToListAsync(ct),
            await db.Set<CatalogueImport>().IgnoreQueryFilters().Select(i => i.Id).ToListAsync(ct));
    }

    private bool _restored;

    public async ValueTask DisposeAsync()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var ids = codes.Select(c => c.Id).ToList();
        await db.Set<IcdCode>().IgnoreQueryFilters().Where(c => c.Release == CodeReleases.Icd10 && !ids.Contains(c.Id))
            .ExecuteDeleteAsync();
        var current = await db.Set<IcdCode>().IgnoreQueryFilters().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        foreach (var before in codes)
        {
            var row = current[before.Id];
            row.Code = before.Code;
            row.Title = before.Title;
            row.Chapter = before.Chapter;
            row.Block = before.Block;
            row.IsBillable = before.IsBillable;
            row.Usage = before.Usage;
            row.IncludedTerms = before.IncludedTerms;
            row.Keywords = before.Keywords;
            row.Source = before.Source;
            row.IsActive = before.IsActive;
            row.WithdrawnAt = before.WithdrawnAt;
            row.RefreshSearchText();
        }

        await db.Set<IcdClassificationEntry>().Where(e => e.Release == CodeReleases.Icd10).ExecuteDeleteAsync();
        db.AddRange(entries);
        await db.Set<CatalogueImport>().IgnoreQueryFilters().Where(i => !imports.Contains(i.Id)).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IcdClassificationProvider>().Invalidate();
    }
}
