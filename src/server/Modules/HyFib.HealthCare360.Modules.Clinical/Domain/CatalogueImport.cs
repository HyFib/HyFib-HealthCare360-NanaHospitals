namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>The code lists a hospital can import from its own licensed or public-domain files.</summary>
public enum CatalogueKind
{
    /// <summary>WHO ICD-10 (2019), from the WHO ClaML file. Stored under <see cref="CodeReleases.Icd10"/>.</summary>
    Icd10 = 0,

    /// <summary>CMS ICD-10-PCS procedures, from the CMS order file. Stored under <see cref="CodeReleases.Icd10Pcs"/>.</summary>
    Icd10Pcs,
}

public enum CatalogueImportChannel
{
    /// <summary>Uploaded on the ICD catalogue screen.</summary>
    Upload = 0,

    /// <summary>The server's <c>catalogue import</c> command, run on the Server Node.</summary>
    Console,
}

/// <summary>
/// One import of a catalogue file: which file (name and SHA-256, so the exact copy can be identified later), what it said
/// it was, what changed, and who imported it when. Kept for the life of the system.
/// </summary>
[NotAudited] // The import writes its own audit entry (mrd.catalogue.imported), which says the same.
public sealed class CatalogueImport : AuditableEntity
{
    public CatalogueKind Catalogue { get; set; }

    /// <summary>The release the codes are stored under (<see cref="CodeReleases"/>).</summary>
    public string Release { get; set; } = string.Empty;

    /// <summary>The uploaded file's name, and the name of the file inside it when it was a zip.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the file as uploaded, in upper-case hexadecimal.</summary>
    public string FileSha256 { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    /// <summary>What the file says it is: the ClaML title ("ICD-10"), or "ICD-10-PCS order file".</summary>
    public string? SourceTitle { get; set; }

    /// <summary>The file's version: the ClaML title's version ("2019") or the order file's year ("2025").</summary>
    public string? SourceVersion { get; set; }

    public int CodesInFile { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Reinstated { get; set; }
    public int Withdrawn { get; set; }

    /// <summary>
    /// Codes MRD added or changed by hand, or saved from the WHO online search: an import never changes them.
    /// </summary>
    public int LeftAlone { get; set; }

    public int Chapters { get; set; }
    public int Blocks { get; set; }

    public DateTimeOffset ImportedAt { get; set; }
    public Guid? ImportedByUserId { get; set; }

    /// <summary>The user's name, or "Server console (DOMAIN\user)" for the command on the Server Node.</summary>
    public string ImportedByName { get; set; } = string.Empty;

    public CatalogueImportChannel Channel { get; set; }
    public int DurationMs { get; set; }
}

public enum IcdClassificationKind
{
    Chapter = 0,
    Block,
}

/// <summary>
/// A chapter or block of an imported classification (MRD-06). The ICD catalogue screen and the morbidity index use these
/// once a WHO file has been imported, and the built-in list (<see cref="IcdClassification"/>) until then. An import
/// replaces them, so they carry no audit columns.
/// </summary>
[NotAudited]
public sealed class IcdClassificationEntry : Entity
{
    public string Release { get; set; } = CodeReleases.Icd10;
    public IcdClassificationKind Kind { get; set; }

    /// <summary>The chapter number ("IV") or the block's code in the file ("E10-E14").</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>First and last three-character category the entry spans ("E10", "E14").</summary>
    public string Start { get; set; } = string.Empty;

    public string End { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>The chapter a block belongs to ("IV"); the chapter's own number for a chapter.</summary>
    public string ChapterNumber { get; set; } = string.Empty;

    /// <summary>The block a nested block sits in ("C00-C97" for "C00-C14"); null for chapters and top-level blocks.</summary>
    public string? ParentCode { get; set; }

    /// <summary>Blocks that hold categories directly (as opposed to blocks of blocks) are the ones the browser lists.</summary>
    public bool HoldsCategories { get; set; }

    /// <summary>Position in the file, which is the classification's own order.</summary>
    public int Sequence { get; set; }
}
