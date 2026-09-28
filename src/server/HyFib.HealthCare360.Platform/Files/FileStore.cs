using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Files;

/// <summary>Metadata for a document stored encrypted (AES-256-GCM) under the data directory.</summary>
public sealed class StoredFile : AuditableEntity
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string Category { get; set; } = "document";
    public Guid? PatientId { get; set; }
    public string? OwnerModule { get; set; }
    public Guid? FacilityId { get; set; }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> b)
    {
        b.ToTable("stored_files", "platform");
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.StoragePath).HasMaxLength(300);
        b.Property(x => x.Category).HasMaxLength(32);
        b.Property(x => x.OwnerModule).HasMaxLength(32);
        b.HasIndex(x => x.PatientId);
    }
}

public sealed record StoredFileDto(Guid Id, string FileName, string ContentType, long SizeBytes, string Category, Guid? PatientId, DateTimeOffset CreatedAt);

public interface IFileStore
{
    /// <summary>Validates type by content signature, encrypts and stores the file.</summary>
    Task<Result<StoredFile>> SaveAsync(Stream content, string fileName, string category, Guid? patientId, string? ownerModule, CancellationToken cancellationToken);

    Task<Result<(StoredFile File, byte[] Content)>> ReadAsync(Guid id, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class EncryptedFileStore(HospitalDbContext db, DataDirectories directories, IFieldProtector protector, ICurrentUser currentUser, TimeProvider time) : IFileStore
{
    public const long MaxBytes = 25 * 1024 * 1024;
    private const string Purpose = "files";

    public async Task<Result<StoredFile>> SaveAsync(Stream content, string fileName, string category, Guid? patientId, string? ownerModule, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            return Error.Field("file", "The file is empty.");
        }

        if (buffer.Length > MaxBytes)
        {
            return Error.Field("file", "Files must be 25 MB or smaller.");
        }

        var bytes = buffer.ToArray();
        var contentType = SniffContentType(bytes);
        if (contentType is null)
        {
            return Error.Field("file", "Only PDF, PNG, JPEG and WebP files are accepted.");
        }

        var now = time.GetUtcNow();
        var file = new StoredFile
        {
            FileName = SanitizeFileName(fileName),
            ContentType = contentType,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Category = string.IsNullOrWhiteSpace(category) ? "document" : category.Trim().ToLowerInvariant(),
            PatientId = patientId,
            OwnerModule = ownerModule,
            FacilityId = currentUser.FacilityId,
        };
        file.StoragePath = Path.Combine(now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), now.ToString("MM", System.Globalization.CultureInfo.InvariantCulture), $"{file.Id:N}.bin");

        var fullPath = Path.Combine(directories.Files, file.StoragePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, protector.ProtectBytes(bytes, Purpose), cancellationToken);

        db.Add(file);
        await db.SaveChangesAsync(cancellationToken);
        return file;
    }

    public async Task<Result<(StoredFile File, byte[] Content)>> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.Set<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null)
        {
            return Error.NotFound("File", id);
        }

        var fullPath = Path.Combine(directories.Files, file.StoragePath);
        if (!File.Exists(fullPath))
        {
            return Error.NotFound("File content", id);
        }

        var content = protector.UnprotectBytes(await File.ReadAllBytesAsync(fullPath, cancellationToken), Purpose);
        return (file, content);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.Set<StoredFile>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null)
        {
            return Error.NotFound("File", id);
        }

        // Soft delete keeps the encrypted blob for medico-legal retention; purge is a retention job.
        db.Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // DICOM Part 10: a 128-byte preamble followed by "DICM" (ECG/echo/cardiology device output, key images).
    private static bool IsDicom(ReadOnlySpan<byte> bytes) => bytes.Length > 132 && bytes.Slice(128, 4).SequenceEqual("DICM"u8);

    internal static string? SniffContentType(ReadOnlySpan<byte> bytes) => IsDicom(bytes) ? "application/dicom" : bytes switch
    {
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => "application/pdf",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "document");
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "document" : cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }
}

internal static class FileEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/files").WithTags("Documents");

        group.MapPost("/", async (IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string? category, [Microsoft.AspNetCore.Mvc.FromForm] Guid? patientId,
                IFileStore store, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                var result = await store.SaveAsync(stream, file.FileName, category ?? "document", patientId, null, ct);
                return result.ToHttpResult(f => TypedResults.Created($"/api/v1/files/{f.Id}",
                    new StoredFileDto(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.Category, f.PatientId, f.CreatedAt)));
            })
            .RequirePermission(PlatformPermissions.FilesWrite)
            .DisableAntiforgery() // bearer-token API: not vulnerable to CSRF
            .WithMetadata(new RequestSizeLimitAttributeShim(EncryptedFileStore.MaxBytes + (1024 * 1024)));

        group.MapGet("/{id:guid}", async (Guid id, bool? download, IFileStore store, IAuditLogger audit, CancellationToken ct) =>
            {
                var result = await store.ReadAsync(id, ct);
                if (result.IsFailure)
                {
                    return result.Error!.ToProblem();
                }

                var (file, content) = result.Value;
                if (file.PatientId is { } patientId)
                {
                    await audit.PatientRecordAccessedAsync(patientId, $"Opened document '{file.FileName}'", ct);
                }

                return TypedResults.File(content, file.ContentType, download == true ? file.FileName : null);
            })
            .RequirePermission(PlatformPermissions.FilesRead);

        group.MapGet("/{id:guid}/meta", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await db.Set<StoredFile>().Where(f => f.Id == id)
                        .Select(f => new StoredFileDto(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.Category, f.PatientId, f.CreatedAt))
                        .FirstOrDefaultAsync(ct) is { } dto
                    ? TypedResults.Ok(dto)
                    : Error.NotFound("File", id).ToProblem())
            .RequirePermission(PlatformPermissions.FilesRead);
    }
}

/// <summary>Per-endpoint request size limit (Kestrel honours IRequestSizeLimitMetadata).</summary>
internal sealed class RequestSizeLimitAttributeShim(long maxBytes) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maxBytes;
}
