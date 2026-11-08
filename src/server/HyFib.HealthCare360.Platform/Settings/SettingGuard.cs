using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Settings;

/// <summary>
/// How a guarded setting is judged (<see cref="SettingGuard"/>): what it is called and what it controls, in the words the
/// approver reads, and whether a change from the value in force to the one asked loosens it.
/// </summary>
/// <param name="Name">What the setting is, as the approver reads it: "Second pathologist for lab corrections".</param>
/// <param name="Controls">What it controls, and what loosening it lets through.</param>
public abstract record SettingRule(string Name, string Controls)
{
    /// <summary>
    /// Whether going from <paramref name="inForce"/> to <paramref name="asked"/> loosens the setting (or, for an address,
    /// a contact or a provider, changes it at all). Clearing a value in force loosens it: the module's default, or the
    /// server's settings file, decides then, and nobody looked at which. So does a value the rule cannot read: the module
    /// falls back to its default, or fails, and nobody checked which.
    /// </summary>
    public abstract bool Loosens(JsonNode? inForce, JsonNode? asked);

    /// <summary>The value in words for the approver: "on", "10%", "not set (off)".</summary>
    public abstract string Words(JsonNode? value);
}

/// <summary>
/// A switch: true or false, or "true" and "false" typed as text. Without <paramref name="OnIsLooser"/>, on is the control
/// (a second pathologist, payment before a sample is taken), so switching it off waits and switching it on is made at
/// once. With it, on lets something through (a colleague's Windows Hello unlocking a screen, clinical text sent to an AI
/// provider), so switching it on waits.
/// </summary>
/// <param name="DefaultOn">What the module does while no value is set.</param>
public sealed record SettingSwitch(string Name, string Controls, bool DefaultOn, bool OnIsLooser = false) : SettingRule(Name, Controls)
{
    public override bool Loosens(JsonNode? inForce, JsonNode? asked)
    {
        if (asked is null)
        {
            return inForce is not null;
        }

        if (Read(asked) is not { } on)
        {
            return true;
        }

        var now = Read(inForce) ?? DefaultOn;
        return OnIsLooser ? on && !now : now && !on;
    }

    public override string Words(JsonNode? value) =>
        value is null ? $"not set ({OnOff(DefaultOn)})" : Read(value) is { } on ? OnOff(on) : SettingGuard.Raw(value);

    /// <summary>On or off, or null for a value that is neither.</summary>
    internal static bool? Read(JsonNode? value)
    {
        if (value is not JsonValue text)
        {
            return null;
        }

        if (text.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return text.TryGetValue<string>(out var typed) && bool.TryParse(typed.Trim(), out var parsed) ? parsed : null;
    }

    private static string OnOff(bool on) => on ? "on" : "off";
}

/// <summary>
/// A number that sets a limit: a discount's ceiling, a grace or lock time, a check's threshold. Raising it waits; with
/// <paramref name="LowerIsLooser"/>, lowering it does (a delta check's look-back: fewer earlier results to compare with).
/// </summary>
/// <param name="Default">The module's value while none is set.</param>
/// <param name="Effective">
/// The value the module acts on for a value set: the range it keeps to, its default for one outside it, and
/// <see cref="NoLimit"/> where 0 means no limit at all.
/// </param>
/// <param name="Format">An effective value in words: "10%", "30 minutes", "no limit".</param>
/// <param name="Whole">The module reads whole numbers only; any other value is one it cannot read.</param>
public sealed record SettingLimit(
    string Name,
    string Controls,
    decimal Default,
    Func<decimal, decimal> Effective,
    Func<decimal, string> Format,
    bool LowerIsLooser = false,
    bool Whole = false) : SettingRule(Name, Controls)
{
    /// <summary>No limit: 0 where the module takes it to mean none (no session limit, a screen that never locks).</summary>
    public const decimal NoLimit = decimal.MaxValue;

    public override bool Loosens(JsonNode? inForce, JsonNode? asked)
    {
        if (asked is null)
        {
            return inForce is not null;
        }

        if (Read(asked) is not { } then)
        {
            return true;
        }

        var now = Effective(Read(inForce) ?? Default);
        var next = Effective(then);
        return LowerIsLooser ? next < now : next > now;
    }

    public override string Words(JsonNode? value)
    {
        if (value is null)
        {
            return $"not set ({Format(Effective(Default))})";
        }

        if (Read(value) is not { } number)
        {
            return SettingGuard.Raw(value);
        }

        // A value outside the module's range is shown with what the module makes of it; 0 for no limit is just that.
        var effective = Effective(number);
        return effective == number || effective == NoLimit ? Format(effective) : $"{SettingGuard.Number(number)} (acts as {Format(effective)})";
    }

    /// <summary>The number set, as the modules read one (a number, or a number typed as text), or null.</summary>
    private decimal? Read(JsonNode? value)
    {
        if (value is not JsonValue text)
        {
            return null;
        }

        decimal? number = text.TryGetValue<decimal>(out var n) ? n
            : text.TryGetValue<string>(out var typed) && decimal.TryParse(typed.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null;
        return number is { } read && Whole && (read != decimal.Truncate(read) || read is < int.MinValue or > int.MaxValue) ? null : number;
    }
}

/// <summary>
/// Where money, patients' data or alerts go: an address, a contact, a provider and its settings, a rate withheld from
/// payments. No direction of it is safer than another, so every change waits.
/// </summary>
public sealed record SettingRedirect(string Name, string Controls) : SettingRule(Name, Controls)
{
    public override bool Loosens(JsonNode? inForce, JsonNode? asked) => !JsonNode.DeepEquals(inForce, asked);

    public override string Words(JsonNode? value) => value is null ? "not set" : SettingGuard.Raw(value);
}

/// <summary>A change to a setting, judged: what it is, in words, and whether it waits for a second administrator.</summary>
/// <param name="Rule">How the setting is guarded; null for one that is not, which is saved at once as before.</param>
/// <param name="Secret">A credential is involved: neither value is ever shown or stored in clear text with the request.</param>
/// <param name="From">The value in force, in words.</param>
/// <param name="To">The value asked, in words.</param>
public sealed record SettingChange(string Key, SettingRule? Rule, bool Secret, bool Waits, string From, string To)
{
    public bool Guarded => Rule is not null;

    public string Name => Rule?.Name ?? Key;

    /// <summary>"Second pathologist for lab corrections: on → off".</summary>
    public string Summary => $"{Name}: {From} → {To}";
}

/// <summary>
/// The settings one administrator must not change alone (maker-checker). The administration settings screen saves any
/// setting at once, so without this one person could switch off a control other people's second check relies on (the
/// second pathologist, the second doctor on a death certificate, the second blood bank officer, administration only on
/// enrolled desktops), raise a limit that lets money out (a discount given on one's own authority), or send money,
/// patients' data or critical alerts somewhere else (the portal's address in every SMS, a payment gateway's keys, the
/// on-call number). A change that loosens one of these, or changes where something goes, waits for a second administrator
/// (<see cref="ChangeKind"/>); the stricter direction is made at once, and any other setting is saved at once as before.
/// <para>
/// The keys are the modules' own (<c>LabResultService.SeparateVerifierSetting</c> and the others named beside each);
/// SettingApprovalTests checks each against its module's constant. Every credential (a <c>*.secret</c> key, or a value
/// saved as secret) waits, whatever its key.
/// </para>
/// </summary>
public static class SettingGuard
{
    /// <summary>The approval that makes a change to a guarded setting.</summary>
    public const string ChangeKind = "settings.change";

    /// <summary>What the approval is about: one setting, for the whole hospital or one facility.</summary>
    public const string SubjectType = nameof(SystemSetting);

    public const string SecretWords = "a secret value (not shown)";
    public const string NewSecretWords = "a new secret value (not shown)";

    private const int ShownLength = 200;

    private static readonly SettingRule Credential = new SettingRedirect(
        "Credential", "A key or password HC360 uses with an outside service: whoever holds it acts as the hospital there.");

    private static readonly Dictionary<string, SettingRule> Rules = new(StringComparer.Ordinal)
    {
        // ---------------------------------------------------------------- controls a second person relies on

        // LabResultService.SeparateVerifierSetting
        ["lab.separateVerifier"] = new SettingSwitch("Second pathologist for lab corrections",
            "Whether a second pathologist verifies what one entered, and releases the correction of a report already released. Off, one pathologist does both alone.",
            DefaultOn: false),

        // VitalRegistrationService.ConfirmationSetting
        ["mrd.deathCertificateConfirmation"] = new SettingSwitch("Second doctor on death certificates",
            "Whether a second doctor confirms each death certificate before it is reported to the Registrar. Off, the certifying doctor's word alone is reported.",
            DefaultOn: true),

        // BloodBankSettings.SeparateVerifier (the SpecialtyCare module): on unless set to false.
        ["bloodbank.separateVerifier"] = new SettingSwitch("Second blood bank officer",
            "Whether a second blood bank officer verifies a donation's screening results before its units leave quarantine, and issues blood one officer both grouped and cross-matched. Off, one officer does all of it, and what they release so is listed for the medical officer's review.",
            DefaultOn: true),

        // ClinicalSignOff.DischargeSummaryCareTeamSetting (the Clinical module): on unless set to false.
        ["clinical.dischargeSummary.careTeamOnly"] = new SettingSwitch("Discharge summary signed by the care team",
            "Whether a discharge summary is signed only by the stay's consultant or a doctor on its care team, as themselves. Off, any doctor signs any stay's summary, and an account not linked to a doctor signs it in the consultant's name.",
            DefaultOn: true),

        // ClinicalSignOff.EncounterAmendByAuthorSetting (the Clinical module): on unless set to false.
        ["clinical.encounters.amendByAuthorOnly"] = new SettingSwitch("Another doctor's signed encounter amended with approval",
            "Whether a signed encounter is amended at once only by the doctor who wrote it, and by any other doctor only when its author or the head of its department approves. Off, any doctor amends any signed encounter alone (the original is still kept).",
            DefaultOn: true),

        // ClinicalSignOff.CriticalResultCoveringSetting (the Clinical module): on unless set to false.
        ["clinical.criticalResults.coveringOnly"] = new SettingSwitch("Critical results acknowledged by the covering doctor",
            "Whether another doctor's critical result is acknowledged only by a doctor on the same stay's care team or one the ordering doctor handed over to. Off, any doctor acknowledges any critical result, and it leaves the ordering doctor's list unseen by them.",
            DefaultOn: true),

        // AdministrationOnDesktop.SettingKey; not set, the server's settings file decides (on, as it ships).
        ["security.administrationRequiresDesktop"] = new SettingSwitch("Administration only on enrolled desktops",
            "Whether users and roles, settings, devices, backups, integrations and the audit trail can be changed only in HC360 Desktop on a computer IT enrolled. Off, any browser signed in as an administrator can.",
            DefaultOn: true),

        // SampleService.RequirePaymentSetting
        ["lab.requirePaymentBeforeCollection"] = new SettingSwitch("Payment before a lab sample is taken",
            "Whether a paying patient's sample is collected only once the tests are paid for. Off, samples are taken and tested unpaid.",
            DefaultOn: true),

        // ShiftService.RequiredSetting
        ["billing.shift.required"] = new SettingSwitch("Cash counter shift required",
            "Whether money is collected or paid out only in a cashier's open shift, counted and closed at its end. Off, money moves outside any shift's count.",
            DefaultOn: true),

        // AuthService.BiometricUnlockSetting
        ["ui.biometricUnlock"] = new SettingSwitch("Windows Hello unlock",
            "Whether staff may unlock a locked screen with the device's Windows Hello, fingerprint or face. Where staff share a Windows login, whoever can unlock it could unlock a colleague's screen.",
            DefaultOn: false, OnIsLooser: true),

        // AiSettingKeys.Enabled (the AI module): on, clinical text and dictation go to the provider set up (SEC-12).
        ["ai.enabled"] = new SettingSwitch("AI features",
            "Whether clinical text and dictation are sent to the AI provider set up. Off, nothing leaves the hospital for AI.",
            DefaultOn: false, OnIsLooser: true),

        // ---------------------------------------------------------------- limits

        // DiscountService.DefaultLimitSetting: read as 0 to 100.
        ["billing.discount.defaultMaxPercent"] = new SettingLimit("Own-authority discount limit",
            "The discount, as a percentage of the bill, that staff without a limit of their own may give without anyone's approval.",
            0m, v => Math.Clamp(v, 0m, 100m), Percent),

        // ChargeService.CancelApprovalAboveSetting and DefaultCancelApprovalAbove: below 0 acts as 0 (every cancellation waits).
        ["billing.chargeCancel.approvalAbove"] = new SettingLimit("Charge cancelled without approval",
            "The amount up to which the billing desk cancels an unbilled charge on its own; a larger one, and every OT, procedure, implant or bed-day charge, waits for someone who approves discounts. 0 makes every cancellation wait.",
            1000m, v => Math.Max(v, 0m), v => "₹" + Money.FormatInr(v)),

        // ShiftService.ShortageToleranceSetting and DefaultShortageTolerance: below 0 acts as 0 (every shortage waits).
        ["billing.shift.shortageTolerance"] = new SettingLimit("Cash shortage closed without a supervisor",
            "How much cash a cashier's drawer may be short at the shift's close before a supervisor must accept the shortage; 0 makes every shortage wait.",
            0m, v => Math.Max(v, 0m), v => "₹" + Money.FormatInr(v)),

        // DispensingService.MaxDiscountSetting and DefaultMaxDiscountPercent.
        ["pharmacy.discount.max_percent"] = new SettingLimit("Pharmacy discount limit",
            "The largest discount, as a percentage, the pharmacy counter may give on a sale.",
            10m, v => v, Percent),

        // CanteenService.StaffCreditLimitSetting and DefaultStaffCreditLimit: 0 or less, no limit.
        ["supportservices.canteen.staffCreditLimit"] = new SettingLimit("Canteen credit per employee",
            "How much a member of staff may owe the canteen before paying; 0 removes the limit.",
            3000m, v => v <= 0m ? SettingLimit.NoLimit : v, v => v == SettingLimit.NoLimit ? "no limit" : "₹" + Money.FormatInr(v)),

        // MarService.GraceSetting: 5 to 240 minutes, the default (30, MarRules.DefaultGrace) for any other.
        ["inpatient.marGraceMinutes"] = new SettingLimit("Grace before a dose shows overdue",
            "Minutes after a dose's scheduled time before the medication chart shows it overdue to the nurses.",
            30m, v => v is >= 5m and <= 240m ? v : 30m, v => Count(v, "minute"), Whole: true),

        // MarService.CountersignHoursSetting and DefaultCountersignHours: 1 to 72 hours, 24 for any other.
        ["inpatient.verbalOrderCountersignHours"] = new SettingLimit("Time to countersign a verbal order",
            "Hours within which the doctor countersigns a verbal or telephone medication order a nurse wrote in their name, or a nurse's stop of a medicine, before it is flagged and the ward's nurse in charge is told.",
            24m, v => v is >= 1m and <= 72m ? v : 24m, v => Count(v, "hour"), Whole: true),

        // ResultEvaluator.DeltaPercentSetting and DefaultDeltaPercent: 0 or less turns the check off (DeltaCheck).
        ["lab.deltaCheckPercent"] = new SettingLimit("Delta check threshold",
            "How far, as a percentage, a result may move from the patient's previous one before it is held for review; 0 turns the check off.",
            50m, v => v <= 0m ? SettingLimit.NoLimit : v, v => v == SettingLimit.NoLimit ? "off" : Percent(v)),

        // ResultEvaluator.DeltaWindowSetting and DefaultDeltaWindowDays: kept to 1 to 3650 days.
        ["lab.deltaCheckWindowDays"] = new SettingLimit("Delta check look-back",
            "How many days back a result is compared with the patient's previous one; fewer days compare fewer results.",
            30m, v => Math.Clamp(v, 1m, 3650m), v => Count(v, "day"), LowerIsLooser: true, Whole: true),

        // AuthService.MaxSessionsSetting and DefaultMaxSessions: 0 (or less), no limit.
        ["security.maxSessionsPerUser"] = new SettingLimit("Sessions per staff account",
            "How many sessions one staff account may have at once (signing in once more ends the oldest); 0 removes the limit.",
            3m, v => v <= 0m ? SettingLimit.NoLimit : v, v => v == SettingLimit.NoLimit ? "no limit" : Count(v, "session"), Whole: true),

        // AuthService.IdleLockMinutesSetting and DefaultIdleLockMinutes: 0 (or less), never.
        ["ui.idleLockMinutes"] = new SettingLimit("Screen lock after inactivity",
            "Minutes without use before a staff screen locks; 0 never locks it.",
            10m, v => v <= 0m ? SettingLimit.NoLimit : v, v => v == SettingLimit.NoLimit ? "never" : Count(v, "minute"), Whole: true),

        // ---------------------------------------------------------------- where money, patients' data and alerts go

        // DoctorFeeService.TdsSetting: either way is wrong, so any change waits.
        ["doctorfees.tds.percent"] = new SettingRedirect("TDS on doctors' fees",
            "The income tax withheld from doctors' payouts (section 194J): too little leaves the hospital owing it, too much underpays the doctors."),

        // PortalAddress.BaseUrlSetting
        ["portal.baseUrl"] = new SettingRedirect("Patient portal address",
            "The address in every SMS and WhatsApp link and every QR code printed for patients: a wrong one sends patients, and their personal links, somewhere else."),

        // AbdmGatewaySettingKeys (the Interop module): the gateway's address and identities, and its client secret.
        ["abdm.gateway"] = new SettingRedirect("ABDM gateway",
            "Where, and as which facility, HC360 sends patients' health records to the national ABDM network."),
        ["abdm.gateway.secret"] = new SettingRedirect("ABDM gateway credentials",
            "The client secret HC360 signs in to the ABDM gateway with, to send patients' health records."),

        // AiSettingKeys (the AI module): which provider gets clinical text and dictation, its address and keys.
        ["ai.provider"] = new SettingRedirect("AI provider", "Which AI service clinical text is sent to."),
        ["ai.openai"] = new SettingRedirect("AI server", "The address of the OpenAI-compatible server clinical text is sent to."),
        ["ai.speech"] = new SettingRedirect("Speech-to-text service", "The service, and its address, that dictation audio is sent to."),
        ["ai.anthropic.secret"] = new SettingRedirect("Anthropic API key", "The key clinical text is sent to Anthropic with."),
        ["ai.openai.secret"] = new SettingRedirect("AI server key", "The key clinical text is sent to the OpenAI-compatible server with."),
        ["ai.speech.secret"] = new SettingRedirect("Speech-to-text key", "The key dictation audio is sent with."),
    };

    /// <summary>Whole families of keys, each key named after its family.</summary>
    private static readonly (string Prefix, SettingRule Rule)[] Families =
    [
        // AlertHeartbeat, AlertEscalation and WifiMonitor: the numbers paged, how long each has, and the heartbeat's address.
        ("alerts.", new SettingRedirect("Critical alerts",
            "Who IT's critical alerts reach and when they go further (IT on-call, the second contact, HyFib support, and how long each has), and the outside heartbeat that notices the server stop.")),

        // IntegrationNames: each integration's settings (integrations.sms) and credentials (integrations.sms.secret).
        ("integrations.", new SettingRedirect("Integration",
            "An outside service HC360 sends patients' data or money through (SMS, WhatsApp, e-mail, the payment gateway, card machines, PACS, laboratory and other equipment): its provider, address and credentials.")),
    ];

    /// <summary>How <paramref name="key"/> is guarded, or null for a setting saved at once whatever the change.</summary>
    public static SettingRule? RuleFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Rules.TryGetValue(key, out var rule))
        {
            return rule;
        }

        foreach (var (prefix, family) in Families)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return family with { Name = $"{family.Name} ({key})" };
            }
        }

        return null;
    }

    /// <summary>A change from <paramref name="inForce"/> to <paramref name="asked"/>, neither a credential.</summary>
    public static SettingChange Judge(string key, JsonNode? inForce, JsonNode? asked, bool secret) => Judge(key, inForce, secret, asked, secret);

    /// <summary>
    /// A change from the value in force at the setting's scope (<paramref name="inForce"/>: the facility's own value, else
    /// the hospital's, for a facility) to <paramref name="asked"/>. A credential on either side waits on any change and is
    /// never put in words.
    /// </summary>
    public static SettingChange Judge(string key, JsonNode? inForce, bool inForceSecret, JsonNode? asked, bool askedSecret)
    {
        // A *.secret key is a credential however it was saved (SecretSettingKeys).
        inForceSecret |= SecretSettingKeys.IsSecret(key);
        askedSecret |= SecretSettingKeys.IsSecret(key);
        var secret = inForceSecret || askedSecret;
        var rule = RuleFor(key) ?? (secret ? Credential with { Name = $"{Credential.Name} ({key})" } : null);
        if (rule is null)
        {
            return new SettingChange(key, null, false, false, Words(inForce), Words(asked));
        }

        var from = !inForceSecret ? rule.Words(inForce) : inForce is null ? "not set" : SecretWords;
        var to = !askedSecret ? rule.Words(asked) : asked is null ? "cleared" : NewSecretWords;
        return new SettingChange(key, rule, secret, secret || rule.Loosens(inForce, asked), from, to);
    }

    /// <summary>The approval's subject for a setting: the same for the same key and facility (null: the hospital-wide value).</summary>
    public static Guid SubjectId(string key, Guid? facilityId)
    {
        ArgumentNullException.ThrowIfNull(key);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"setting:{key}:{facilityId?.ToString("N") ?? "all"}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>A value as typed, shortened for the approver's list: text without its quotes, anything else as JSON.</summary>
    internal static string Raw(JsonNode value)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        return text.Length <= ShownLength ? text : text[..(ShownLength - 1)] + "…";
    }

    internal static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Words(JsonNode? value) => value is null ? "not set" : Raw(value);

    private static string Percent(decimal value) => Number(value) + "%";

    private static string Count(decimal value, string unit) => $"{Number(value)} {unit}{(value == 1m ? string.Empty : "s")}";
}
