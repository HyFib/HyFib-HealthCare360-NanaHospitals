using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The copies outside the building (DED-16) over the real server and PostgreSQL: the off-site settings refused when the
/// uploads could not use them, and saved once a second administrator approves; a real backup uploaded in parts by the
/// job's own uploader to an S3-compatible store (in memory here; the signing and the store's checks are in the platform's
/// tests), its progress and outcome kept in the backup history, which the Backups list and System health read.
/// </summary>
public sealed class BackupOffsiteTests(HospitalServer server)
{
    private const string Health = "/api/v1/admin/system/health";
    private const string Prefix = "hc360-it/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_verified_backup_goes_off_site_in_parts_and_the_backup_list_and_system_health_say_so()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var backups = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Backup;
        var nightly = backups.Enabled;
        using var store = new MemoryStore();
        try
        {
            // An address over plain http is refused when it is saved, not read leniently later.
            using (var refused = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{BackupOffsite.SettingKey}",
                       new SaveSettingRequest(JsonNode.Parse("""{ "enabled": true, "endpoint": "http://objects.hc360-offsite.invalid", "region": "ap-south-1", "bucket": "nana-it" }"""),
                           null, false, null, null, "Off-site copy")))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            }

            // Saved as the Backups screen saves it: each change waits for, and is approved by, the other administrator.
            await server.SaveSettingAsync(admin, BackupOffsite.SettingKey, new SaveSettingRequest(JsonNode.Parse(
                $$"""{ "enabled": true, "endpoint": "https://objects.hc360-offsite.invalid", "region": "ap-south-1", "bucket": "nana-it", "prefix": "{{Prefix}}", "pathStyle": true, "retainDays": 35, "backfillDays": 0 }"""),
                null, false, null));
            await server.SaveSettingAsync(admin, BackupOffsite.SecretKey, new SaveSettingRequest(JsonNode.Parse(
                """{ "accessKeyId": "AKIAHC360INTEGRATION", "secretAccessKey": "integration-test-secret" }"""), null, true, null));

            backups.Enabled = true;
            var made = await BackupAsync();
            var file = Path.Combine(server.Services.GetRequiredService<DataDirectories>().Backups, made.FileName!);

            var result = await UploadAsync(store);

            result.Stopped.ShouldBeFalse();
            var listed = (await admin.GetAsync<List<BackupRunDto>>("/api/v1/admin/backups")).Single(r => r.Id == made.Id);
            listed.OffsiteUploadedAt.ShouldNotBeNull();
            (listed.OffsiteKey, listed.OffsiteError).ShouldBe((Prefix + made.FileName, (string?)null));
            listed.OffsiteAttempts.ShouldBeGreaterThanOrEqualTo(1);
            store.Objects[Prefix + made.FileName].ShouldBe(await File.ReadAllBytesAsync(file, Ct));
            store.Locks[Prefix + made.FileName].ShouldBe("COMPLIANCE");
            store.Parts.ShouldBeGreaterThan(1, "a backup larger than a part goes up in parts");
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var row = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<BackupRun>().AsNoTracking().SingleAsync(r => r.Id == made.Id, Ct);
                (row.OffsiteSizeBytes, row.OffsiteUpload).ShouldBe((made.SizeBytes, (string?)null));
            }

            (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == BackupOffsite.Code);

            // The internet goes: the next backup's upload fails, and System health says why, as a warning for now.
            store.Down = true;
            var next = await BackupAsync();
            (await UploadAsync(store)).Stopped.ShouldBeTrue();
            var warning = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == BackupOffsite.Code);
            warning.Severity.ShouldBe("warning");
            warning.Message.ShouldContain("has not reached the off-site copy in bucket nana-it at objects.hc360-offsite.invalid yet");
            (await admin.GetAsync<List<BackupRunDto>>("/api/v1/admin/backups")).Single(r => r.Id == next.Id).OffsiteError.ShouldNotBeNull();
        }
        finally
        {
            // The server is shared by every test of the run: no off-site copy, and nightly backups as they were.
            backups.Enabled = nightly;
            await using var scope = server.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await settings.RemoveAsync(BackupOffsite.SettingKey, null, Ct);
            await settings.RemoveAsync(BackupOffsite.SecretKey, null, Ct);
        }
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it.</summary>
    private async Task<BackupRunDto> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        return run;
    }

    /// <summary>One run of the off-site job's uploader, as the server composes it, with its requests going to <paramref name="store"/> and parts of 256 KiB.</summary>
    private async Task<BackupOffsiteRun> UploadAsync(MemoryStore store)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var uploader = new BackupOffsiteUploader(
            services.GetRequiredService<IDbContextFactory<HospitalDbContext>>(),
            services.GetRequiredService<ISettingsService>(),
            new StoreClients(store),
            services.GetRequiredService<DataDirectories>(),
            services.GetRequiredService<IOptions<PlatformOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<BackupOffsiteUploader>>(),
            services.GetRequiredService<HyFib.HealthCare360.Platform.Security.IFieldKeyEscrow>())
        {
            Sizes = new OffsiteUploadSizes(SinglePutLimit: 256 << 10, MinPartSize: 256 << 10),
        };
        return await uploader.RunAsync(Ct);
    }

    /// <summary>The off-site client, with its requests to the store in memory.</summary>
    private sealed class StoreClients(MemoryStore store) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(store, disposeHandler: false);
    }

    /// <summary>
    /// An S3-compatible store in memory, path-style, enough for the uploads: one PUT, a multipart upload and HEAD. While
    /// <see cref="Down"/>, every request fails as when the internet is down.
    /// </summary>
    private sealed class MemoryStore : HttpMessageHandler
    {
        private readonly Dictionary<string, SortedDictionary<int, byte[]>> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _uploadKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _uploadLocks = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> Locks { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ETags { get; } = new(StringComparer.Ordinal);

        public int Parts { get; private set; }

        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("No such host is known. (objects.hc360-offsite.invalid:443)");
            }

            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var key = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/').Split('/', 2)[1];
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty, StringComparer.Ordinal);
            var lockMode = request.Headers.TryGetValues("x-amz-object-lock-mode", out var modes) ? modes.First() : null;
            lock (_uploads)
            {
                switch (request.Method.Method)
                {
                    case "POST" when query.ContainsKey("uploads"):
                        var id = Guid.NewGuid().ToString("N");
                        (_uploads[id], _uploadKeys[id], _uploadLocks[id]) = (new SortedDictionary<int, byte[]>(), key, lockMode);
                        return Xml(new XElement("InitiateMultipartUploadResult", new XElement("UploadId", id)));
                    case "PUT" when query.TryGetValue("partNumber", out var number):
                        _uploads[query["uploadId"]][int.Parse(number, CultureInfo.InvariantCulture)] = body;
                        Parts++;
                        return Tagged(new HttpResponseMessage(HttpStatusCode.OK), Convert.ToHexStringLower(Md5(body)));
                    case "POST" when query.TryGetValue("uploadId", out var upload):
                        var parts = _uploads[upload];
                        _uploads.Remove(upload);
                        Objects[_uploadKeys[upload]] = [.. parts.Values.SelectMany(p => p)];
                        Locks[_uploadKeys[upload]] = _uploadLocks[upload];
                        var etag = $"{Convert.ToHexStringLower(Md5([.. parts.Values.SelectMany(Md5)]))}-{parts.Count}";
                        ETags[_uploadKeys[upload]] = etag;
                        return Xml(new XElement("CompleteMultipartUploadResult", new XElement("ETag", $"\"{etag}\"")));
                    case "PUT":
                        Objects[key] = body;
                        Locks[key] = lockMode;
                        ETags[key] = Convert.ToHexStringLower(Md5(body));
                        return Tagged(new HttpResponseMessage(HttpStatusCode.OK), ETags[key]);
                    case "HEAD" when Objects.TryGetValue(key, out var data):
                        var head = Tagged(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }, ETags[key]);
                        head.Content.Headers.ContentLength = data.Length;
                        return head;
                    default:
                        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
                }
            }
        }

        private static HttpResponseMessage Tagged(HttpResponseMessage response, string etag)
        {
            response.Headers.TryAddWithoutValidation("ETag", $"\"{etag}\"");
            return response;
        }

        private static HttpResponseMessage Xml(XElement element) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(element.ToString(), Encoding.UTF8, "application/xml"),
        };

        [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Primitives", Justification = "S3's ETags are MD5 by the protocol.")]
        private static byte[] Md5(byte[] data) => MD5.HashData(data);
    }
}
