using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>What is at the top of each page: the hospital's letterhead, or blank space because the paper has it printed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrintLetterhead>))]
public enum PrintLetterhead
{
    /// <summary>HC360 prints the letterhead: logo, name, address and the rest.</summary>
    Printed = 0,

    /// <summary>The paper is the hospital's pre-printed stationery, so the space its letterhead takes is left blank.</summary>
    Preprinted,
}

/// <summary>
/// How the hospital wants one kind of document printed (SRS HMS-04, UX-09), as an administrator set it in Hospital setup
/// › Print templates: its letterhead or pre-printed stationery, the logo, a closing note, and the paper where there is a
/// choice. It is kept as the setting <c>print.{key}</c>, hospital-wide or for one facility. The layout itself stays in the
/// code (ADR-012): nothing here can add markup or styles to a document.
/// </summary>
public sealed record PrintTemplate
{
    public const int MaxSpaceMm = 80;
    public const int MaxClosingNoteLength = 500;

    public static PrintTemplate Default { get; } = new();

    public PrintLetterhead Letterhead { get; init; }

    /// <summary>Blank space at the top of every page for the stationery's letterhead, in millimetres. Pre-printed paper only.</summary>
    public int TopSpaceMm { get; init; }

    /// <summary>Blank space at the foot of every page for the stationery's own footer, in millimetres. Pre-printed paper only.</summary>
    public int BottomSpaceMm { get; init; }

    public bool ShowLogo { get; init; } = true;

    /// <summary>Plain text printed at the end of the document, such as terms or a thank-you line. Line breaks are kept.</summary>
    public string? ClosingNote { get; init; }

    /// <summary>The paper, where the document offers a choice (<see cref="PrintTemplateDefinition.Papers"/>); null for its usual one.</summary>
    public DocumentPageSize? Paper { get; init; }

    [JsonIgnore]
    public bool PrintsLetterhead => Letterhead == PrintLetterhead.Printed;
}

/// <summary>A kind of document whose printing an administrator can set up.</summary>
/// <param name="Key">Stable key: the setting is <c>print.{key}</c>, and the staff app asks for the same key.</param>
/// <param name="Name">What staff call these documents: "Bills", "Reports".</param>
/// <param name="Area">The part of the system that prints them, as the staff app's menu names it.</param>
/// <param name="Covers">Which documents follow this template, in a sentence.</param>
public sealed record PrintTemplateDefinition(string Key, string Name, string Area, string Covers)
{
    /// <summary>The papers to choose from, the usual one first. Empty when the paper is fixed, or set per counter as receipts are.</summary>
    public IReadOnlyList<DocumentPageSize> Papers { get; init; } = [];

    /// <summary>False for documents printed only on 80 mm rolls, which are never pre-printed.</summary>
    public bool AllowsPreprinted { get; init; } = true;
}

/// <summary>The kinds of document an administrator can set up, with the rules their templates must keep to.</summary>
public static class PrintTemplateCatalog
{
    public const string Bill = "bill";
    public const string Receipt = "receipt";
    public const string SalesInvoice = "sales-invoice";
    public const string Report = "report";
    public const string Prescription = "prescription";
    public const string ClinicalNote = "clinical-note";
    public const string Purchase = "purchase";
    public const string Other = "other";

    public const string SettingPrefix = "print.";

    public static IReadOnlyList<PrintTemplateDefinition> All { get; } =
    [
        new(Bill, "Bills", "Billing", "Tax invoices and bills of supply, the 80 mm counter bill and the IP running bill."),
        new(Receipt, "Receipts and refund vouchers", "Billing", "Payment and advance receipts, on 80 mm or A4 as each counter is set up, and refund vouchers."),
        new(SalesInvoice, "Pharmacy and canteen bills", "Pharmacy", "The 80 mm bills of the pharmacy and canteen counters.") { AllowsPreprinted = false },
        new(Report, "Reports", "Diagnostics", "Laboratory, radiology, cardiology and health check-up reports."),
        new(Prescription, "Prescriptions", "Clinical", "Prescriptions, on A5 or A4.") { Papers = [DocumentPageSize.A5, DocumentPageSize.A4] },
        new(ClinicalNote, "Clinical notes", "Clinical", "Discharge summaries, consultation summaries and operation notes."),
        new(Purchase, "Purchase orders and goods receipts", "Purchase", "Purchase orders sent to suppliers, and goods receipt notes."),
        new(Other, "Other documents", "Hospital",
            "Everything else the staff app prints: day-end and other reports, ward, store and CSSD slips, admission and appointment slips, visitor passes, lists and statements."),
    ];

    public static PrintTemplateDefinition? Find(string key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    public static string SettingKey(string key) => SettingPrefix + key;
}

/// <summary>The rules a template keeps to: what an administrator may save, and what a stored value is read as.</summary>
public static class PrintTemplateRules
{
    /// <summary>What is wrong with <paramref name="template"/> for <paramref name="definition"/>, by field; empty when it may be saved.</summary>
    public static IReadOnlyList<(string Field, string Message)> Problems(PrintTemplateDefinition definition, PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(template);
        var problems = new List<(string, string)>();
        if (!Enum.IsDefined(template.Letterhead))
        {
            problems.Add(("letterhead", "Choose the letterhead or pre-printed paper."));
        }
        else if (!template.PrintsLetterhead && !definition.AllowsPreprinted)
        {
            problems.Add(("letterhead", "These documents print on 80 mm rolls, which are never pre-printed."));
        }

        if (template.TopSpaceMm is < 0 or > PrintTemplate.MaxSpaceMm)
        {
            problems.Add(("topSpaceMm", $"Enter between 0 and {PrintTemplate.MaxSpaceMm} mm."));
        }

        if (template.BottomSpaceMm is < 0 or > PrintTemplate.MaxSpaceMm)
        {
            problems.Add(("bottomSpaceMm", $"Enter between 0 and {PrintTemplate.MaxSpaceMm} mm."));
        }

        if (template.ClosingNote is { } note)
        {
            if (note.Trim().Length > PrintTemplate.MaxClosingNoteLength)
            {
                problems.Add(("closingNote", $"Keep the closing note to {PrintTemplate.MaxClosingNoteLength} characters."));
            }
            else if (note.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
            {
                problems.Add(("closingNote", "The closing note can hold text and line breaks only."));
            }
        }

        if (template.Paper is { } paper && !definition.Papers.Contains(paper))
        {
            problems.Add(("paper", definition.Papers.Count == 0
                ? "The paper of these documents is fixed."
                : $"Choose {string.Join(" or ", definition.Papers)}."));
        }

        return problems;
    }

    /// <summary>
    /// The template as documents use it: within the limits, the blank spaces only on pre-printed paper, the closing note
    /// trimmed (null when empty), and a paper the document cannot use dropped. A value set by hand that breaks a rule
    /// prints as near to it as the rules allow, never as a broken document.
    /// </summary>
    public static PrintTemplate Normalize(PrintTemplateDefinition definition, PrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(template);
        var preprinted = template.Letterhead == PrintLetterhead.Preprinted && definition.AllowsPreprinted;
        var note = template.ClosingNote?.Trim();
        return template with
        {
            Letterhead = preprinted ? PrintLetterhead.Preprinted : PrintLetterhead.Printed,
            TopSpaceMm = preprinted ? Math.Clamp(template.TopSpaceMm, 0, PrintTemplate.MaxSpaceMm) : 0,
            BottomSpaceMm = preprinted ? Math.Clamp(template.BottomSpaceMm, 0, PrintTemplate.MaxSpaceMm) : 0,
            ClosingNote = string.IsNullOrEmpty(note)
                ? null
                : new string(note.Where(c => !char.IsControl(c) || c is '\n').Take(PrintTemplate.MaxClosingNoteLength).ToArray()),
            Paper = template.Paper is { } paper && definition.Papers.Contains(paper) ? paper : null,
        };
    }
}

/// <summary>The template a document prints with.</summary>
public interface IPrintTemplates
{
    /// <summary>
    /// The facility's own template for <paramref name="key"/>, else the hospital-wide one, else the defaults; always within
    /// the rules (<see cref="PrintTemplateRules.Normalize"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not in <see cref="PrintTemplateCatalog"/>.</exception>
    Task<PrintTemplate> GetAsync(string key, Guid? facilityId, CancellationToken cancellationToken);
}

internal sealed class PrintTemplates(ISettingsService settings) : IPrintTemplates
{
    public async Task<PrintTemplate> GetAsync(string key, Guid? facilityId, CancellationToken cancellationToken)
    {
        var definition = PrintTemplateCatalog.Find(key) ?? throw new ArgumentException($"There is no print template '{key}'.", nameof(key));
        PrintTemplate? stored;
        try
        {
            stored = await settings.GetAsync<PrintTemplate>(PrintTemplateCatalog.SettingKey(key), facilityId, cancellationToken);
        }
        catch (JsonException)
        {
            // Set by hand in Administration › Settings in a shape that is not a template: the defaults print.
            stored = null;
        }

        return PrintTemplateRules.Normalize(definition, stored ?? PrintTemplate.Default);
    }
}
