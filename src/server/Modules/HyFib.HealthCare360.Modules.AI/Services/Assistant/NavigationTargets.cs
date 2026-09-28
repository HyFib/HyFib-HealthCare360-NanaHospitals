namespace HyFib.HealthCare360.Modules.AI.Services.Assistant;

/// <summary>A screen the assistant can open, with the phrases staff use for it.</summary>
internal sealed record NavigationTarget(string Route, string Label, IReadOnlyList<string> Phrases);

/// <summary>
/// Staff UI screens reachable by voice ("go to billing", "open the bed board"). Routes follow the staff app's
/// file-based routes; the UI ignores a route it does not know, so this list can only ever open real screens.
/// </summary>
internal static class NavigationTargets
{
    public static IReadOnlyList<NavigationTarget> All { get; } =
    [
        new("/", "Home", ["home", "dashboard", "home page", "main page"]),
        new("/patients", "Patients", ["patients", "patient list", "patient search", "patient master"]),
        new("/patients/register", "Patient registration", ["registration", "register patient", "register a patient", "new patient", "patient registration", "new registration"]),
        new("/front-office", "Front office", ["front office", "front desk", "reception", "help desk"]),
        new("/front-office/queue", "Queue", ["queue", "token queue", "tokens", "waiting list", "waiting room"]),
        new("/scheduling", "Appointments", ["appointments", "appointment book", "appointment calendar", "appointment diary"]),
        new("/scheduling/book", "Book appointment", ["book appointment", "book an appointment", "new appointment", "appointment booking"]),
        new("/telehealth", "Video consultations", ["telehealth", "video consultations", "video consultation", "teleconsultation", "online consultations"]),
        new("/emr", "Doctor worklist", ["worklist", "my worklist", "doctor worklist", "emr", "physician desktop", "consultation"]),
        new("/nursing", "Nursing station", ["nursing", "nursing station", "medication round", "mar", "ward round"]),
        new("/lab", "Laboratory", ["lab", "laboratory", "lis", "lab orders", "samples", "sample collection"]),
        new("/radiology", "Radiology", ["radiology", "imaging", "x-ray", "xray", "scans", "pacs"]),
        new("/cardiology", "Cardiology", ["cardiology", "ecg", "echo", "tmt", "holter"]),
        new("/pharmacy", "Pharmacy", ["pharmacy", "dispensing", "medicines", "drug store"]),
        new("/billing", "Billing", ["billing", "bills", "op billing", "cash counter", "billing counter", "invoices"]),
        new("/insurance", "Insurance", ["insurance", "tpa", "claims", "pre authorisation", "pre authorization", "preauth"]),
        new("/inpatient/beds", "Bed board", ["bed board", "beds", "bed management", "ward board", "wards", "bed status"]),
        new("/inpatient/admissions", "Admissions", ["admissions", "admission", "adt", "admitted patients", "inpatients", "ip patients"]),
        new("/ot", "Operation theatre", ["ot", "operation theatre", "operation theater", "surgery schedule", "theatre schedule"]),
        new("/dialysis", "Dialysis", ["dialysis", "dialysis unit", "haemodialysis", "hemodialysis"]),
        new("/physio", "Physiotherapy", ["physiotherapy", "physio"]),
        new("/blood-bank", "Blood bank", ["blood bank", "blood stock", "blood units"]),
        new("/health-checkups", "Health check-ups", ["health checkups", "health check-ups", "health checkup", "master health checkup", "packages"]),
        new("/mrd", "Medical records", ["mrd", "medical records", "coding", "icd coding"]),
        new("/inventory", "Inventory", ["inventory", "stores", "stock"]),
        new("/purchase", "Purchase", ["purchase", "purchase orders", "vendors", "grn", "goods receipt"]),
        new("/assets", "Assets", ["assets", "equipment", "biomedical", "maintenance"]),
        new("/cssd", "CSSD", ["cssd", "sterilisation", "sterilization", "sterile packs"]),
        new("/dietary", "Dietary", ["dietary", "diet", "diet orders", "kitchen", "meals"]),
        new("/canteen", "Canteen", ["canteen", "cafeteria"]),
        new("/hr", "Human resources", ["hr", "human resources", "employees", "attendance", "payroll", "leave"]),
        new("/quality", "Quality and incidents", ["quality", "incidents", "incident reporting", "irms", "capa", "risk register"]),
        new("/crm", "CRM", ["crm", "leads", "campaigns", "follow ups", "follow-ups"]),
        new("/feedback", "Feedback", ["feedback", "complaints", "patient feedback"]),
        new("/rpm", "Remote monitoring", ["remote monitoring", "rpm", "home monitoring"]),
        new("/analytics", "Reports and analytics", ["reports", "mis", "mis reports", "analytics", "dashboards"]),
        new("/admin/integrations", "Integrations", ["integrations", "sms settings", "whatsapp settings", "payment gateway"]),
        new("/admin/settings", "Settings", ["settings", "administration", "admin"]),
    ];

    /// <summary>Exact phrase match after light normalisation ("the Billing page" → "billing").</summary>
    public static NavigationTarget? Find(string phrase)
    {
        var words = phrase.Trim().ToLowerInvariant().Replace("'s", string.Empty, StringComparison.Ordinal).Trim('.', '!', '?')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "my" or "page" or "screen" or "module" or "tab" or "section" or "window"))
            .ToList();
        var normalized = string.Join(' ', words);
        return normalized.Length == 0 ? null : All.FirstOrDefault(t => t.Phrases.Contains(normalized, StringComparer.Ordinal));
    }

    public static NavigationTarget? ByRoute(string? route) =>
        All.FirstOrDefault(t => string.Equals(t.Route, route?.Trim(), StringComparison.OrdinalIgnoreCase));
}
