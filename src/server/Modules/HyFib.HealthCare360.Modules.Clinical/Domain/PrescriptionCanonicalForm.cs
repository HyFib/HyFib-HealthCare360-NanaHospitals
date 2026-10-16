using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// Who the signed prescription names, as printed when the doctor signed it with their DSC. Kept inside the signed
/// bytes, so a later change of the patient's or the doctor's master record does not break the signature.
/// </summary>
public sealed record PrescriptionSigningParties(
    string FacilityName,
    string PatientUhid,
    string PatientName,
    string PrescriberName,
    string? PrescriberQualification,
    string? RegistrationNumber,
    string? RegistrationCouncil);

/// <summary>
/// The bytes a doctor signs with their Digital Signature Certificate (ERX-04): a canonical JSON document, format
/// <see cref="Format"/>, of everything clinically meaningful on the printed prescription.
/// <para>
/// <b>Encoding.</b> RFC 8785 (JSON Canonicalization Scheme) restricted to strings, integers, booleans and null: UTF-8
/// without a byte order mark, no whitespace, object members sorted by name (ordinal), strings escaped only where JSON
/// requires it (<c>\"</c>, <c>\\</c>, <c>\b \f \n \r \t</c>, other control characters as <c>\u00xx</c>) and otherwise
/// written as they are, without Unicode normalisation. Quantities are decimal strings without trailing zeros
/// (<c>"1.5"</c>, <c>"60"</c>), times are UTC to the second (<c>"2026-09-25T10:15:00Z"</c>), ids are lower-case GUIDs.
/// Any JCS implementation re-derives the same bytes from the same values.
/// </para>
/// <para>
/// <b>Content.</b> <c>format</c>; <c>facility</c> {id, name}; <c>patient</c> {id, uhid, name}; <c>prescriber</c> {id,
/// name, qualification, registrationNumber, registrationCouncil}; <c>prescription</c> {id, number, encounterId,
/// visitId, admissionId, isInpatient, signedAt (the clinical signature), refillsAllowed, refillOfPrescriptionId, notes,
/// advice, verificationToken (the QR code), items[{sequence, drugName, genericName, strength, dosageForm, route, dose,
/// frequency, customFrequency, durationDays, quantity, instructions, isControlledDrug, controlledSchedule}]}.
/// </para>
/// <para>
/// <b>Left out on purpose.</b> The PDF and its hash: the PDF is a rendering of these values that changes with the print
/// template, the logo and the renderer, and it shows the signature and the QR code itself, so it cannot be signed before
/// it exists. The encounter's diagnoses and follow-up: they belong to the encounter and can change after the
/// prescription. Anything the doctor did not write (stock, dispensing).
/// </para>
/// </summary>
public static class PrescriptionCanonicalForm
{
    public const string Format = "hc360.prescription.v1";

    public const string ContentType = "application/json";

    public static byte[] Build(Prescription prescription, PrescriptionSigningParties parties)
    {
        ArgumentNullException.ThrowIfNull(prescription);
        ArgumentNullException.ThrowIfNull(parties);
        var document = new Dictionary<string, object?>
        {
            ["format"] = Format,
            ["facility"] = new Dictionary<string, object?> { ["id"] = Id(prescription.FacilityId), ["name"] = parties.FacilityName },
            ["patient"] = new Dictionary<string, object?>
            {
                ["id"] = Id(prescription.PatientId),
                ["uhid"] = parties.PatientUhid,
                ["name"] = parties.PatientName,
            },
            ["prescriber"] = new Dictionary<string, object?>
            {
                ["id"] = Id(prescription.PractitionerId),
                ["name"] = parties.PrescriberName,
                ["qualification"] = parties.PrescriberQualification,
                ["registrationNumber"] = parties.RegistrationNumber,
                ["registrationCouncil"] = parties.RegistrationCouncil,
            },
            ["prescription"] = new Dictionary<string, object?>
            {
                ["id"] = Id(prescription.Id),
                ["number"] = prescription.PrescriptionNumber,
                ["encounterId"] = Id(prescription.EncounterId),
                ["visitId"] = Id(prescription.VisitId),
                ["admissionId"] = Id(prescription.AdmissionId),
                ["isInpatient"] = prescription.IsInpatient,
                ["signedAt"] = Time(prescription.SignedAt),
                ["refillsAllowed"] = (long)prescription.RefillsAllowed,
                ["refillOfPrescriptionId"] = Id(prescription.RefillOfPrescriptionId),
                ["notes"] = prescription.Notes,
                ["advice"] = prescription.Advice,
                ["verificationToken"] = prescription.VerificationToken,
                ["items"] = prescription.Items.OrderBy(i => i.Sequence).Select(Item).ToList<object?>(),
            },
        };

        return CanonicalJson.Encode(document);
    }

    /// <summary>Lower-case hex SHA-256 of the canonical bytes, as printed and shown on the verification page.</summary>
    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>The parties recorded in signed bytes, to rebuild them from the prescription as it is now.</summary>
    /// <exception cref="JsonException">The bytes are not a prescription in this format.</exception>
    public static PrescriptionSigningParties ReadParties(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
        {
            throw new JsonException($"Not a {Format} document.");
        }

        var facility = root.GetProperty("facility");
        var patient = root.GetProperty("patient");
        var prescriber = root.GetProperty("prescriber");
        return new PrescriptionSigningParties(
            facility.GetProperty("name").GetString() ?? string.Empty,
            patient.GetProperty("uhid").GetString() ?? string.Empty,
            patient.GetProperty("name").GetString() ?? string.Empty,
            prescriber.GetProperty("name").GetString() ?? string.Empty,
            prescriber.GetProperty("qualification").GetString(),
            prescriber.GetProperty("registrationNumber").GetString(),
            prescriber.GetProperty("registrationCouncil").GetString());
    }

    private static Dictionary<string, object?> Item(PrescriptionItem i) => new()
    {
        ["sequence"] = (long)i.Sequence,
        ["drugName"] = i.DrugName,
        ["genericName"] = i.GenericName,
        ["strength"] = i.Strength,
        ["dosageForm"] = i.DosageForm,
        ["route"] = i.Route,
        ["dose"] = i.Dose,
        ["frequency"] = i.Frequency.ToString(),
        ["customFrequency"] = i.CustomFrequency,
        ["durationDays"] = (long)i.DurationDays,
        ["quantity"] = Quantity(i.Quantity),
        ["instructions"] = i.Instructions,
        ["isControlledDrug"] = i.IsControlledDrug,
        ["controlledSchedule"] = i.IsControlledDrug ? i.ControlledSchedule : null,
    };

    private static string? Id(Guid? id) => id?.ToString("D", CultureInfo.InvariantCulture);

    private static string? Time(DateTimeOffset? at) =>
        at?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"60", "1.5", "0.25": the value whatever the scale the database returns it with.</summary>
    internal static string Quantity(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}

/// <summary>
/// RFC 8785 (JCS) serialisation of a tree of dictionaries, lists, strings, 64-bit integers, booleans and null — the
/// subset the signed prescription uses. Members are sorted by name in UTF-16 code unit order (ordinal).
/// </summary>
internal static class CanonicalJson
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Encode(object? value)
    {
        var builder = new StringBuilder(1024);
        Write(builder, value);
        return Utf8.GetBytes(builder.ToString());
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                WriteString(builder, text);
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case IReadOnlyDictionary<string, object?> members:
                builder.Append('{');
                var first = true;
                foreach (var name in members.Keys.Order(StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, name);
                    builder.Append(':');
                    Write(builder, members[name]);
                }

                builder.Append('}');
                break;
            case IEnumerable<object?> items:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in items)
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            default:
                throw new ArgumentException($"{value.GetType().Name} is not part of the canonical JSON subset.", nameof(value));
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }
}
