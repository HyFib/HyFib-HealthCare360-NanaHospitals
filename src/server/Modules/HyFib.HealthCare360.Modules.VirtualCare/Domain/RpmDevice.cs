namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

public enum RpmDeviceType
{
    BpMonitor = 0,
    Glucometer,
    PulseOximeter,
    WeightScale,
    Thermometer,
    EcgPatch,
}

/// <summary>
/// A monitoring device (or a patient's phone gateway) that pushes readings (RPM-02). It authenticates with an API
/// key shown once at registration; only a SHA-256 hash is stored and it is compared in constant time. Deactivating the
/// device or issuing a new key stops the old key immediately.
/// </summary>
public sealed class RpmDevice : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public RpmDeviceType Type { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public Guid? EnrollmentId { get; set; }
    public Guid? PatientId { get; set; }

    [SensitiveData]
    public string? ApiKeyHash { get; set; }

    public DateTimeOffset? ApiKeyIssuedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Set when a silence alert was raised; cleared by the next contact so each silence alerts once.</summary>
    public DateTimeOffset? SilenceAlertRaisedAt { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Issues a new API key (replacing any previous one). The key is returned once and never stored.</summary>
    public string IssueApiKey(DateTimeOffset now)
    {
        var key = DeviceApiKeys.Create(Id);
        ApiKeyHash = DeviceApiKeys.Hash(key);
        ApiKeyIssuedAt = now;
        return key;
    }

    public bool Accepts(string? presentedKey) => IsActive && DeviceApiKeys.Verify(presentedKey, ApiKeyHash);

    public void AssignTo(RpmEnrollment? enrollment, DateTimeOffset now)
    {
        EnrollmentId = enrollment?.Id;
        PatientId = enrollment?.PatientId;
        AssignedAt = enrollment is null ? null : now;
        SilenceAlertRaisedAt = null;
    }

    public void Deactivate()
    {
        IsActive = false;
        ApiKeyHash = null;
        EnrollmentId = null;
        PatientId = null;
        SilenceAlertRaisedAt = null;
    }

    public void RecordContact(DateTimeOffset now)
    {
        LastSeenAt = now;
        SilenceAlertRaisedAt = null;
    }

    /// <summary>True when an assigned device has sent nothing for <paramref name="threshold"/> and has not been reported yet (RPM-05).</summary>
    public bool IsSilent(DateTimeOffset now, TimeSpan threshold) =>
        IsActive && EnrollmentId is not null && SilenceAlertRaisedAt is null && (LastSeenAt ?? AssignedAt ?? CreatedAt) <= now - threshold;

    public void MarkSilenceAlerted(DateTimeOffset now) => SilenceAlertRaisedAt = now;
}

/// <summary>What each device type can measure: a thermometer reporting blood pressure is a configuration error.</summary>
public static class DeviceCapabilities
{
    public static IReadOnlyList<RpmReadingType> For(RpmDeviceType type) => type switch
    {
        RpmDeviceType.BpMonitor => [RpmReadingType.SystolicBp, RpmReadingType.DiastolicBp, RpmReadingType.HeartRate],
        RpmDeviceType.Glucometer => [RpmReadingType.Glucose],
        RpmDeviceType.PulseOximeter => [RpmReadingType.SpO2, RpmReadingType.HeartRate],
        RpmDeviceType.WeightScale => [RpmReadingType.Weight],
        RpmDeviceType.Thermometer => [RpmReadingType.Temperature],
        RpmDeviceType.EcgPatch => [RpmReadingType.EcgRhythm, RpmReadingType.HeartRate],
        _ => [],
    };

    public static bool Measures(RpmDeviceType type, RpmReadingType reading) => For(type).Contains(reading);
}

/// <summary>
/// Device API keys: <c>rpm_{deviceId:N}.{secret}</c>. The device id prefix lets the server find the device without
/// searching by secret; the whole key is hashed (SHA-256) and compared in constant time.
/// </summary>
public static class DeviceApiKeys
{
    public const string Prefix = "rpm_";
    public const int MaxLength = 128;

    public static string Create(Guid deviceId) => $"{Prefix}{deviceId:N}.{SecretTokens.NewOpaqueToken(32)}";

    public static string Hash(string key) => SecretTokens.Hash(key);

    public static bool TryGetDeviceId(string? key, out Guid deviceId)
    {
        deviceId = Guid.Empty;
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot == Prefix.Length + 32 && dot < key.Length - 1 && Guid.TryParseExact(key.AsSpan(Prefix.Length, 32), "N", out deviceId);
    }

    public static bool Verify(string? key, string? storedHash) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxLength && !string.IsNullOrEmpty(storedHash) && SecretTokens.FixedTimeEquals(storedHash, Hash(key));
}
