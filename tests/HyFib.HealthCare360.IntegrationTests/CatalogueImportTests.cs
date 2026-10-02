using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Seed;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Importing the ICD-10 catalogue from a hospital's WHO file (Medical records › ICD catalogue › Import). The file here is
/// made in the shape of WHO's ClaML: HC360's own starter codes with their titles, their categories, and enough further
/// categories to pass the check that refuses partial files. The database is shared by the whole run, so the file keeps
/// every starter code exactly as it is: other journeys use them (E11.9, L03.1) and see no difference.
/// </summary>
public sealed class CatalogueImportTests(HospitalServer server)
{
    private const string Imports = "/api/v1/mrd/catalogue/imports";

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
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var categories = WhoLikeCategories();
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
        (await SearchAsync(admin, "test condition")).ShouldNotBeEmpty();
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
    }

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
