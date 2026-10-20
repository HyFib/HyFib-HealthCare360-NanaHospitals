using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

/// <summary>
/// What HC360 Desktop is asked to sign with the doctor's DSC (ERX-04): the prescription's canonical bytes, as the server
/// made them. The desktop reads the prescription out of those bytes itself — never from text the page sends alongside —
/// so the certificate prompt tells the doctor what the signature will actually cover.
/// </summary>
public sealed record PrescriptionSigningRequest(
    byte[] Content,
    string PrescriptionNumber,
    string PatientName,
    string PatientUhid,
    string PrescriberName,
    IReadOnlyList<string> Medicines)
{
    /// <summary>The server's canonical format (<c>PrescriptionCanonicalForm.Format</c> in the Clinical module).</summary>
    public const string Format = "hc360.prescription.v1";

    /// <summary>Thirty medicines with long instructions stay well under this.</summary>
    public const int MaxContentBytes = 64 * 1024;

    /// <summary>The text of the certificate prompt: the prescription, the patient and the medicines.</summary>
    public string Summary
    {
        get
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Prescription {PrescriptionNumber} for {PatientName} (UHID {PatientUhid}), by {PrescriberName}.");
            text.AppendLine();
            foreach (var medicine in Medicines.Take(8))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"• {medicine}");
            }

            if (Medicines.Count > 8)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"• and {Medicines.Count - 8} more");
            }

            text.Append("Choose your Digital Signature Certificate to sign it.");
            return text.ToString();
        }
    }

    /// <summary>Reads the base64 canonical bytes the page sends.</summary>
    /// <exception cref="FormatException">Not base64, too large, or not a prescription in <see cref="Format"/>.</exception>
    public static PrescriptionSigningRequest Parse(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > MaxContentBytes * 4 / 3 + 4)
        {
            throw new FormatException("There is no prescription to sign.");
        }

        var content = Convert.FromBase64String(base64);
        if (content.Length > MaxContentBytes)
        {
            throw new FormatException("The prescription to sign is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != Format)
            {
                throw new FormatException("This version of HC360 Desktop cannot sign this kind of document.");
            }

            var prescription = root.GetProperty("prescription");
            var patient = root.GetProperty("patient");
            var medicines = prescription.GetProperty("items").EnumerateArray()
                .Select(item => string.Join(' ', new[] { item.GetProperty("drugName").GetString(), item.GetProperty("strength").GetString() }
                    .Where(part => !string.IsNullOrWhiteSpace(part))))
                .ToList();
            return new PrescriptionSigningRequest(
                content,
                prescription.GetProperty("number").GetString() ?? string.Empty,
                patient.GetProperty("name").GetString() ?? string.Empty,
                patient.GetProperty("uhid").GetString() ?? string.Empty,
                root.GetProperty("prescriber").GetProperty("name").GetString() ?? string.Empty,
                medicines);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("The prescription to sign could not be read.", ex);
        }
    }
}
