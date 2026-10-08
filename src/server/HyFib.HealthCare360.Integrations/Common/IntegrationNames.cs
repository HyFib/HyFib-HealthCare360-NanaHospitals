namespace HyFib.HealthCare360.Integrations.Common;

/// <summary>
/// Stable integration keys. They name the settings rows (<c>integrations.&lt;key&gt;</c> and
/// <c>integrations.&lt;key&gt;.secret</c>) and the <c>integration</c> column of the integration log, so renaming one
/// orphans existing configuration and history — add new keys instead.
/// </summary>
internal static class IntegrationNames
{
    public const string Sms = "sms";
    public const string WhatsApp = "whatsapp";
    public const string Email = "email";
    public const string Payment = "payment";
    public const string Pos = "pos";
    public const string Tally = "tally";
    public const string Pacs = "pacs";
    public const string Mwl = "mwl";
    public const string Lab = "lab";
    public const string Labels = "labels";
    public const string Icd = "icd";

    /// <summary>Attendance terminals (INT-BIO). HR logs pushes under this name too.</summary>
    public const string Biometric = "biometric";

    public static IReadOnlyList<string> All { get; } = [Sms, WhatsApp, Email, Payment, Pos, Tally, Pacs, Mwl, Lab, Labels, Icd, Biometric];

    public static string SettingsKey(string integration) => $"integrations.{integration}";

    public static string SecretKey(string integration) => $"integrations.{integration}.secret";

    public static string DisplayName(string integration) => integration switch
    {
        Sms => "SMS",
        WhatsApp => "WhatsApp",
        Email => "E-mail",
        Payment => "Payment gateway",
        Pos => "Card/UPI POS",
        Tally => "Tally",
        Pacs => "PACS",
        Mwl => "Modality worklist",
        Lab => "Laboratory equipment",
        Labels => "Barcode labels",
        Icd => "ICD lookup",
        Biometric => "Biometric attendance",
        _ => integration,
    };
}

/// <summary>Values accepted in the <c>provider</c> field of an integration's settings (compared case-insensitively).</summary>
internal static class ProviderKeys
{
    public const string Simulator = "simulator";
    public const string Msg91 = "msg91";
    public const string Twilio = "twilio";
    public const string GenericHttp = "generic";
    public const string MetaCloud = "meta";
    public const string Smtp = "smtp";
    public const string Razorpay = "razorpay";
    public const string PineLabs = "pinelabs";
    public const string TallyPrime = "tally";
    public const string DicomWeb = "dicomweb";
    public const string WhoIcdApi = "who";
    public const string ZkTeco = "zkteco";
    public const string Disabled = "disabled";
}
