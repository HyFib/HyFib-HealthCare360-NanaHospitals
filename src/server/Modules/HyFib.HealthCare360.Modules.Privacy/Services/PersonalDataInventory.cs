using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// The summary of a patient's personal data that the DPDP Act (s11) entitles them to: what is held, why and on what
/// basis, how long it is kept, who it was shared with, and which service providers process it for the hospital.
/// </summary>
public sealed record DataSummary(
    DateTimeOffset GeneratedAt,
    string Hospital,
    SummaryPerson Person,
    IReadOnlyList<SummaryCategory> Categories,
    IReadOnlyList<SummaryConsent> Consents,
    IReadOnlyList<SummaryRecipient> SharedWith,
    IReadOnlyList<SummaryRecipient> Processors,
    SummaryNominee? Nominee,
    PrivacyContact Contact,
    RetentionPeriods Periods);

/// <summary>The retention periods in force, so a client can say "how long we keep it" in the patient's language.</summary>
public sealed record RetentionPeriods(int OutpatientYears, int InpatientYears, int FinanceYears, int MessageLogDays, int AuditYears);

/// <summary>The registration details as held. National identifiers appear only as the hospital stores them: masked.</summary>
public sealed record SummaryPerson(
    string Uhid,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Phone,
    string? AlternatePhone,
    string? Email,
    string? Address,
    string? EmergencyContact,
    string? AbhaNumber,
    string? AbhaAddress,
    string? AadhaarLast4,
    bool HasPhoto,
    DateTimeOffset RegisteredAt,
    string? RegisteredAtFacility);

/// <summary>
/// A group of the summary. <see cref="Retention"/> is the English sentence; <see cref="RetentionKind"/> with the
/// summary's <see cref="DataSummary.Periods"/> lets a client say it in another language.
/// </summary>
public sealed record SummaryCategory(
    string Key,
    string Title,
    string Description,
    string Purpose,
    string Basis,
    string Retention,
    PersonalDataRetention RetentionKind,
    IReadOnlyList<SummaryItem> Items);

/// <summary>
/// How many records of a kind are held, and the dates of the first and the latest. <see cref="Key"/> is stable (the
/// table, "clinical.encounters"), for a client that translates the label.
/// </summary>
public sealed record SummaryItem(string Key, string Label, int Count, DateTimeOffset? First, DateTimeOffset? Last);

public sealed record SummaryConsent(string Type, bool Granted, DateTimeOffset CapturedAt, DateTimeOffset? RevokedAt);

/// <summary>An organisation that received the patient's data: its name, its role, and what it received.</summary>
public sealed record SummaryRecipient(string Name, string Role, string Data);

public sealed record SummaryNominee(string Name, string Relationship);

/// <summary>
/// Builds the <see cref="DataSummary"/> from <see cref="PersonalDataCatalogue"/>: each counted table is found in the
/// EF model by name and counted by its patient column, so no module needs to know about this summary. Records deleted
/// in the application are not counted; every facility of the group is.
/// </summary>
public sealed class PersonalDataInventory(
    HospitalDbContext db,
    PrivacySettings privacy,
    RetentionService retention,
    TimeProvider time)
{
    private static readonly MethodInfo TallyMethod =
        typeof(PersonalDataInventory).GetMethod(nameof(TallyAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    // The providers the hospital may configure, what each does for it, and the data it receives.
    private static readonly (string Setting, string Service, string Data)[] ProviderSettings =
    [
        ("integrations.sms", "Sends SMS messages", "Your mobile number and the text of each SMS"),
        ("integrations.whatsapp", "Sends WhatsApp messages", "Your mobile number and the text of each message"),
        ("integrations.email", "Sends e-mails", "Your e-mail address and the text of each e-mail"),
        ("integrations.payment", "Takes online payments", "Your name, contact details and the amount you pay"),
    ];

    public async Task<DataSummary?> SummariseAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null)
        {
            return null;
        }

        var facilities = await db.Set<Facility>().AsNoTracking().OrderBy(f => f.CreatedAt).Select(f => new { f.Id, f.Name }).ToListAsync(ct);
        var retentionText = await retention.DescribeAsync(ct);
        var categories = new List<SummaryCategory>();
        foreach (var category in PersonalDataCatalogue.Categories)
        {
            var items = new List<SummaryItem>();
            foreach (var group in category.Sources.Where(s => s.Counted).GroupBy(s => s.Label))
            {
                var tallies = new List<Tally>();
                foreach (var source in group)
                {
                    tallies.Add(await CountAsync(source, patientId, ct));
                }

                var count = tallies.Sum(t => t.Count);
                if (count > 0)
                {
                    items.Add(new SummaryItem(group.First().Table, group.Key, count, tallies.Min(t => t.First), tallies.Max(t => t.Last)));
                }
            }

            if (items.Count > 0 || category.Key == "registration")
            {
                categories.Add(new SummaryCategory(category.Key, category.Title, category.Description, category.Purpose, category.Basis,
                    retentionText[category.Retention], category.Retention, items));
            }
        }

        var consents = await db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => new SummaryConsent(c.Type.ToString(), c.Granted, c.CapturedAt, c.RevokedAt))
            .ToListAsync(ct);
        var nominee = await db.Set<Nominee>().AsNoTracking()
            .Where(n => n.PatientId == patientId)
            .Select(n => new SummaryNominee(n.Name, n.Relationship))
            .FirstOrDefaultAsync(ct);

        return new DataSummary(
            time.GetUtcNow(),
            facilities.FirstOrDefault()?.Name ?? "Hospital",
            new SummaryPerson(
                patient.Uhid,
                patient.FullName,
                patient.Gender,
                patient.DateOfBirth,
                patient.Phone,
                patient.AlternatePhone,
                patient.Email,
                patient.Address.ToString() is { Length: > 0 } address ? address : null,
                string.Join(", ", new[] { patient.EmergencyContactName, patient.EmergencyContactRelation, patient.EmergencyContactPhone }
                    .Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } emergency ? emergency : null,
                patient.AbhaNumberMasked,
                patient.AbhaAddress,
                patient.AadhaarLast4 is null ? null : "XXXX-XXXX-" + patient.AadhaarLast4,
                patient.PhotoFileId is not null,
                patient.RegisteredAt,
                facilities.FirstOrDefault(f => f.Id == patient.RegisteredFacilityId)?.Name),
            categories,
            consents,
            await SharedWithAsync(patientId, ct),
            await ProcessorsAsync(ct),
            nominee,
            await privacy.PublishedContactAsync(ct),
            await retention.PeriodsAsync(ct));
    }

    /// <summary>The counted table <paramref name="source"/> resolves to in the EF model, or null when it is missing.</summary>
    public static (IEntityType Entity, IProperty Patient)? Resolve(IModel model, PersonalDataSource source)
    {
        var parts = source.Table.Split('.', 2);
        var entity = model.GetEntityTypes().FirstOrDefault(t => !t.IsOwned() && t.GetSchema() == parts[0] && t.GetTableName() == parts[1]);
        var property = entity?.GetProperties().FirstOrDefault(p => p.GetColumnName() == source.Column);
        return entity is null || property is null ? null : (entity, property);
    }

    private async Task<Tally> CountAsync(PersonalDataSource source, Guid patientId, CancellationToken ct)
    {
        var (entity, patient) = Resolve(db.Model, source)
            ?? throw new InvalidOperationException($"The personal data catalogue names {source.Table}.{source.Column}, which the model does not have.");

        var date = new[] { "CreatedAt", "OccurredAt" }.Select(entity.FindProperty)
            .FirstOrDefault(p => p is not null && p.ClrType == typeof(DateTimeOffset))?.Name;
        var task = (Task<Tally>)TallyMethod.MakeGenericMethod(entity.ClrType)
            .Invoke(this, [patient.Name, patient.ClrType == typeof(Guid?), date, patientId, ct])!;
        return await task;
    }

    private async Task<Tally> TallyAsync<T>(string property, bool nullable, string? date, Guid patientId, CancellationToken ct)
        where T : class
    {
        var rows = db.Set<T>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(OfPatient<T>(property, nullable, patientId));
        var count = await rows.CountAsync(ct);
        if (count == 0 || date is null)
        {
            return new Tally(count, null, null);
        }

        var first = await rows.MinAsync(e => EF.Property<DateTimeOffset>(e, date), ct);
        var last = await rows.MaxAsync(e => EF.Property<DateTimeOffset>(e, date), ct);
        return new Tally(count, first, last);
    }

    // e => EF.Property<Guid>(e, property) == id, with the id read from a closure so the query is parameterised.
    private static Expression<Func<T, bool>> OfPatient<T>(string property, bool nullable, Guid patientId)
    {
        var entity = Expression.Parameter(typeof(T), "e");
        var type = nullable ? typeof(Guid?) : typeof(Guid);
        var column = Expression.Call(typeof(EF), nameof(EF.Property), [type], entity, Expression.Constant(property));
        Expression id = Expression.Property(Expression.Constant(new PatientKey(patientId)), nameof(PatientKey.Id));
        if (nullable)
        {
            id = Expression.Convert(id, typeof(Guid?));
        }

        return Expression.Lambda<Func<T, bool>>(Expression.Equal(column, id), entity);
    }

    /// <summary>
    /// Other organisations that received the patient's data: insurers and TPAs, for claims and pre-authorisations, and
    /// outside systems that read the record through the FHIR interface (as the audit trail records each read).
    /// </summary>
    private async Task<List<SummaryRecipient>> SharedWithAsync(Guid patientId, CancellationToken ct)
    {
        var claims = await db.Set<InsuranceClaim>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(c => c.PatientId == patientId)
            .Select(c => new { c.PayerId, c.TpaId })
            .ToListAsync(ct);
        var preAuthorizations = await db.Set<PreAuthorization>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(p => p.PatientId == patientId)
            .Select(p => new { p.PayerId, p.TpaId })
            .ToListAsync(ct);
        var payerIds = claims.Concat(preAuthorizations)
            .SelectMany(x => new[] { (Guid?)x.PayerId, x.TpaId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var payers = await db.Set<Payer>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => payerIds.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => new { p.Name, p.Category })
            .ToListAsync(ct);
        var recipients = payers
            .Select(p => new SummaryRecipient(p.Name, PayerRole(p.Category.ToString()),
                "Your bills, diagnoses and treatment details, for insurance claims and pre-authorisations"))
            .ToList();

        var fhirReaders = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Category == AuditCategories.PhiAccess && a.Summary != null && a.Summary.StartsWith("FHIR R4"))
            .GroupBy(a => a.UserName)
            .Select(g => new { Name = g.Key, Reads = g.Count() })
            .ToListAsync(ct);
        recipients.AddRange(fhirReaders.Where(r => r.Name is not null).OrderBy(r => r.Name).Select(r => new SummaryRecipient(r.Name!,
            "Another care provider's system, with your data-sharing consent",
            $"Parts of your health record, read {r.Reads} time{(r.Reads == 1 ? string.Empty : "s")}")));
        return recipients;
    }

    private static string PayerRole(string category) => category switch
    {
        "Tpa" => "Third-party administrator (TPA)",
        "Corporate" => "Employer or corporate scheme",
        "GovernmentScheme" => "Government health scheme",
        _ => "Insurance company",
    };

    /// <summary>
    /// The service providers that process patients' data for the hospital, from the integrations it has turned on in
    /// any facility (the simulator used for training is not a provider).
    /// </summary>
    private async Task<List<SummaryRecipient>> ProcessorsAsync(CancellationToken ct)
    {
        var keys = ProviderSettings.Select(p => p.Setting).Append("ai.enabled").Append("ai.provider").ToList();
        var rows = await db.Set<SystemSetting>().AsNoTracking()
            .Where(s => keys.Contains(s.Key) && !s.IsSecret)
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        var processors = new List<SummaryRecipient>();
        foreach (var (setting, service, data) in ProviderSettings)
        {
            foreach (var provider in rows.Where(r => r.Key == setting).Select(r => ActiveProvider(r.Value)).OfType<string>().Distinct())
            {
                processors.Add(new SummaryRecipient(ProviderName(provider), service, data));
            }
        }

        var aiOn = rows.Any(r => r.Key == "ai.enabled" && r.Value.Trim() == "true");
        var aiProvider = rows.Where(r => r.Key == "ai.provider").Select(r => Unquote(r.Value)).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (aiOn && aiProvider is not null)
        {
            processors.Add(new SummaryRecipient(ProviderName(aiProvider), "AI assistant for staff",
                "Clinical text with names, numbers and other identifiers removed"));
        }

        return [.. processors.DistinctBy(p => (p.Name, p.Role))];
    }

    private static string? ActiveProvider(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = provider.GetString()?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(name) || name == "simulator" ? null : name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Unquote(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(json)?.Trim().ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ProviderName(string provider) => provider switch
    {
        "msg91" => "MSG91",
        "twilio" => "Twilio",
        "gupshup" => "Gupshup",
        "meta" or "cloudapi" => "Meta (WhatsApp Business)",
        "smtp" => "The hospital's e-mail server",
        "razorpay" => "Razorpay",
        "anthropic" => "Anthropic",
        _ => char.ToUpperInvariant(provider[0]) + provider[1..],
    };

    private sealed record Tally(int Count, DateTimeOffset? First, DateTimeOffset? Last);

    private sealed record PatientKey(Guid Id);
}
