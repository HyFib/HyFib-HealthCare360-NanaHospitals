using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FellowOakDicom;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>An object ready to send: the file as it goes to the PACS, and the UIDs that name it.</summary>
internal sealed record DicomObjectToStore(string Reference, string SopClassUid, string SopInstanceUid, byte[] Content);

/// <summary>
/// The DICOM side of STOW-RS (PS3.18 §10.5; pure, unit-tested): each file is filed under HC360's patient and study, sent
/// as <c>multipart/related; type="application/dicom"</c>, and the PACS's answer is read for that object.
/// </summary>
/// <remarks>
/// <para>
/// A machine records whatever was typed at its console, often a name without the UHID, and its own study. Before an
/// object goes to the PACS, HC360 writes the patient's UHID, name, birth date and sex, its study UID, the accession number
/// and the study's name over them, so the PACS files it with the patient's other studies and the viewer link finds it.
/// The values replaced are kept in the object's Original Attributes Sequence (PS3.3 §C.12.1), with reason
/// <c>COERCE</c>, so nothing the machine recorded is lost. The object's SOP class and instance UIDs are kept: sending the
/// same file again replaces the same object instead of adding a copy.
/// </para>
/// <para>
/// When a new value holds characters the object's character set cannot (a name in Devanagari, say), the object is
/// changed to UTF-8 (<c>ISO_IR 192</c>); fo-dicom then writes all of its text in UTF-8.
/// </para>
/// </remarks>
internal static class DicomStow
{
    public const string ModifyingSystem = "HC360";

    /// <summary>UTF-8 (<c>ISO_IR 192</c>).</summary>
    public const string Utf8 = "ISO_IR 192";

    /// <summary>Latin-1 (<c>ISO_IR 100</c>).</summary>
    private const string Latin1 = "ISO_IR 100";

    /// <summary>A DICOMDIR: the index of a CD, not an object a PACS stores.</summary>
    private const string MediaStorageDirectory = "1.2.840.10008.1.3.10";

    private const int MaxLongString = 64;

    private const int MaxShortString = 16;

    /// <summary>The attributes HC360 writes, in the order they are written.</summary>
    private static readonly DicomTag[] Coerced =
    [
        DicomTag.PatientID, DicomTag.PatientName, DicomTag.PatientBirthDate, DicomTag.PatientSex,
        DicomTag.StudyInstanceUID, DicomTag.AccessionNumber, DicomTag.StudyDescription,
    ];

    /// <summary>
    /// The file as it goes to the PACS, filed under <paramref name="study"/>; null with <paramref name="problem"/> saying
    /// why when it is not a DICOM object HC360 can send.
    /// </summary>
    public static DicomObjectToStore? Prepare(PacsStoreFile file, PacsStudyIdentity study, DateTimeOffset now, out string? problem)
    {
        DicomFile dicom;
        try
        {
            dicom = DicomFile.Open(new MemoryStream(file.Content, writable: false), FileReadOption.ReadAll);
        }
        catch (Exception ex) when (ex is DicomException or IOException or ArgumentException or InvalidOperationException or FormatException)
        {
            problem = "HC360 could not read this file as DICOM.";
            return null;
        }

        // A PACS reads the object's own UIDs, not the file header's.
        var dataset = dicom.Dataset;
        var sopClass = Uid(dataset, DicomTag.SOPClassUID);
        var sopInstance = Uid(dataset, DicomTag.SOPInstanceUID);
        if (sopClass == MediaStorageDirectory || dicom.FileMetaInfo?.MediaStorageSOPClassUID?.UID == MediaStorageDirectory)
        {
            problem = "This is a DICOMDIR, the index of a CD, not an image. Attach the image files it lists instead.";
            return null;
        }

        if (!QidoQuery.IsValidUid(sopClass) || !QidoQuery.IsValidUid(sopInstance))
        {
            problem = "The file has no SOP class or instance UID, so a PACS cannot store it.";
            return null;
        }

        try
        {
            Coerce(dataset, study, now);
            using var output = new MemoryStream();
            dicom.Save(output);
            problem = null;
            return new DicomObjectToStore(file.Reference, sopClass!, sopInstance!, output.ToArray());
        }
        catch (Exception ex) when (ex is DicomException or IOException or ArgumentException or InvalidOperationException or FormatException)
        {
            problem = "HC360 could not write the patient's details into this DICOM file.";
            return null;
        }
    }

    /// <summary>One object as a STOW-RS request body.</summary>
    public static MultipartContent Body(DicomObjectToStore item)
    {
        var body = new MultipartContent("related", "hc360-" + Guid.NewGuid().ToString("N"));
        body.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "\"application/dicom\""));
        var part = new ByteArrayContent(item.Content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");
        body.Add(part);
        return body;
    }

    /// <summary>
    /// What the PACS did with the object, from the HTTP status and the response (DICOM JSON: the Referenced SOP Sequence
    /// lists what it stored, the Failed SOP Sequence what it did not and why).
    /// </summary>
    public static PacsStoreOutcome Outcome(DicomObjectToStore item, HttpStatusCode status, string? body)
    {
        var (failure, warning, referenced) = Read(body, item.SopInstanceUid);
        if (failure is { } reason)
        {
            // The PACS already holds this object (same SOP instance UID): it is stored.
            return reason == 0x0111
                ? Stored(item, "The PACS already held this object.")
                : Refused(item, $"The PACS refused the object: {FailureText(reason)}.");
        }

        var code = (int)status;
        return status switch
        {
            HttpStatusCode.OK or HttpStatusCode.NoContent => Stored(item, warning is { } w ? WarningText(w) : null),
            HttpStatusCode.Accepted => Stored(item, warning is { } w ? WarningText(w) : referenced ? null : "The PACS accepted the object with a warning."),
            HttpStatusCode.Conflict => Refused(item, "The PACS refused the object."),
            HttpStatusCode.RequestEntityTooLarge => Refused(item, "The file is too large for the PACS."),
            HttpStatusCode.UnsupportedMediaType => Refused(item, "The PACS does not take this kind of DICOM object."),
            HttpStatusCode.BadRequest => Refused(item, "The PACS could not read the object."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failed(item, Invariant($"The PACS refused HC360's sign-in (HTTP {code}). Check the PACS user name, password or token.")),
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
                Failed(item, Invariant($"The PACS does not take objects at this address (HTTP {code}). Check the STOW-RS address in the PACS settings.")),
            _ => Failed(item, Invariant($"The PACS returned HTTP {code}.")),
        };
    }

    public static PacsStoreOutcome Failed(DicomObjectToStore item, string message) => new(item.Reference, PacsStoreStatus.Failed, item.SopInstanceUid, message);

    /// <summary>The DICOM name of a failure reason (PS3.4 Annex GG, PS3.18 Table 10.5.3-2).</summary>
    internal static string FailureText(int reason) => reason switch
    {
        0x0110 => "it could not process it",
        0x0122 => "it does not support this kind of object (SOP class)",
        0x0124 => "HC360 is not allowed to store objects there",
        0xA700 or 0xA701 or 0xA702 => "it is out of storage space or other resources",
        0xA900 => "the object does not match its SOP class",
        0xC122 => "it does not support the object's transfer syntax",
        >= 0xC000 and <= 0xCFFF => "it could not understand the object",
        _ => Invariant($"failure code {reason:X4}"),
    };

    private static PacsStoreOutcome Stored(DicomObjectToStore item, string? message) => new(item.Reference, PacsStoreStatus.Stored, item.SopInstanceUid, message);

    private static PacsStoreOutcome Refused(DicomObjectToStore item, string message) => new(item.Reference, PacsStoreStatus.Refused, item.SopInstanceUid, message);

    private static string WarningText(int warning) => warning switch
    {
        0xB000 => "The PACS stored the object, and changed some of its values.",
        0xB006 => "The PACS stored the object, and left out some of its values.",
        0xB007 => "The PACS stored the object, which does not fully match its SOP class.",
        _ => Invariant($"The PACS stored the object with warning {warning:X4}."),
    };

    /// <summary>
    /// The failure reason and warning the response gives for the object, and whether it lists the object as stored.
    /// A response for several objects is read for this one; an unreadable body gives nothing.
    /// </summary>
    private static (int? Failure, int? Warning, bool Referenced) Read(string? body, string sopInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(body) || !IntegrationHttp.TryParseJson(body, out var document))
        {
            return (null, null, false);
        }

        using (document)
        {
            // PS3.18 answers with one object; some PACS wrap it in an array.
            var root = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault()
                : document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, false);
            }

            int? failure = null;
            foreach (var failed in DicomJson.Items(root, "00081198"))
            {
                if (Matches(failed, sopInstanceUid))
                {
                    failure = DicomJson.Integer(failed, "00081197") ?? 0x0110;
                }
            }

            int? warning = null;
            var referenced = false;
            foreach (var stored in DicomJson.Items(root, "00081199"))
            {
                if (Matches(stored, sopInstanceUid))
                {
                    referenced = true;
                    warning = DicomJson.Integer(stored, "00081196");
                }
            }

            return (failure, warning, referenced);
        }
    }

    /// <summary>An item about this object; an item without a UID is taken as about it, since one object is sent per request.</summary>
    private static bool Matches(JsonElement item, string sopInstanceUid) =>
        DicomJson.Text(item, "00081155") is not { } uid || uid == sopInstanceUid;

    private static void Coerce(DicomDataset dataset, PacsStudyIdentity study, DateTimeOffset now)
    {
        var values = new Dictionary<DicomTag, string>
        {
            [DicomTag.PatientID] = Clip(study.PatientId, MaxLongString),
            [DicomTag.PatientName] = PersonName(study.PatientName),
            [DicomTag.PatientBirthDate] = study.BirthDate ?? string.Empty,
            [DicomTag.PatientSex] = study.Sex ?? string.Empty,
            [DicomTag.StudyInstanceUID] = study.StudyInstanceUid,
            [DicomTag.AccessionNumber] = study.AccessionNumber is { Length: > 0 and <= MaxShortString } accession ? accession : string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(study.StudyDescription))
        {
            values[DicomTag.StudyDescription] = Clip(study.StudyDescription.Trim(), MaxLongString);
        }

        // What the machine recorded, for the Original Attributes Sequence: only values HC360 changes.
        var original = new DicomDataset();
        foreach (var tag in Coerced.Where(values.ContainsKey))
        {
            if (dataset.TryGetString(tag, out var before) && before != values[tag])
            {
                original.AddOrUpdate(tag, before);
            }
        }

        if (values.Values.Any(v => !CanHold(dataset, v)))
        {
            dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, Utf8);
        }

        foreach (var tag in Coerced.Where(values.ContainsKey))
        {
            dataset.AddOrUpdate(tag, values[tag]);
        }

        if (!original.Any())
        {
            return;
        }

        var modification = new DicomDataset
        {
            new DicomSequence(DicomTag.ModifiedAttributesSequence, original),
        };
        modification.Add(DicomTag.AttributeModificationDateTime, now.UtcDateTime);
        modification.Add(DicomTag.ModifyingSystem, ModifyingSystem);
        modification.Add(DicomTag.SourceOfPreviousValues, string.Empty);
        modification.Add(DicomTag.ReasonForTheAttributeModification, "COERCE");

        // An object coerced before (by another system) keeps those entries.
        var items = dataset.TryGetSequence(DicomTag.OriginalAttributesSequence, out var earlier) ? earlier.Items.ToList() : [];
        items.Add(modification);
        dataset.AddOrUpdate(new DicomSequence(DicomTag.OriginalAttributesSequence, items.ToArray()));
    }

    /// <summary>Whether the object's character set can hold <paramref name="value"/>: ASCII always; Latin-1 and UTF-8 what they cover.</summary>
    private static bool CanHold(DicomDataset dataset, string value)
    {
        if (value.All(char.IsAscii))
        {
            return true;
        }

        var charset = dataset.TryGetValues<string>(DicomTag.SpecificCharacterSet, out var sets) ? sets.LastOrDefault()?.Trim() : null;
        return charset switch
        {
            Utf8 => true,
            Latin1 => value.All(c => c <= 'ÿ'),
            _ => false,
        };
    }

    /// <summary>A person name with each component group within DICOM's 64 characters.</summary>
    private static string PersonName(string name) => string.Join('=', name.Split('=').Select(group => Clip(group, MaxLongString)));

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];

    private static string? Uid(DicomDataset dataset, DicomTag tag) =>
        dataset.TryGetSingleValue<string>(tag, out var uid) && !string.IsNullOrWhiteSpace(uid) ? uid.Trim().TrimEnd('\0') : null;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
