namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum PayerCategory
{
    Insurer = 0,

    /// <summary>Third-party administrator processing claims for insurers.</summary>
    Tpa,

    Corporate,
    GovernmentScheme,
}

/// <summary>
/// Insurer, TPA, corporate tie-up or government scheme (INS-01). A payer may carry its negotiated tariff plan, which is
/// then used to price every service of its patients.
/// </summary>
public sealed class Payer : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PayerCategory Category { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Gstin { get; set; }
    public Guid? TariffPlanId { get; set; }

    /// <summary>Agreed settlement period, used to flag overdue receivables.</summary>
    public int CreditDays { get; set; } = 30;

    /// <summary>Co-pay applied when the patient's policy does not state one.</summary>
    public decimal DefaultCoPayPercent { get; set; }

    /// <summary>TPA/insurer portal where pre-authorisations and claims are filed.</summary>
    public string? PortalUrl { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    public PayerType ToPayerType() => Category switch
    {
        PayerCategory.Corporate => PayerType.Corporate,
        PayerCategory.GovernmentScheme => PayerType.GovernmentScheme,
        _ => PayerType.Insurance,
    };
}

/// <summary>
/// An item a payer never pays for (IRDAI non-payables such as admission kits and toiletries); the patient pays it.
/// A row without payer applies to every payer; it names a service item or a whole category.
/// </summary>
public sealed class NonPayableItem : AuditableEntity
{
    public Guid? PayerId { get; set; }
    public Guid? ServiceItemId { get; set; }
    public ServiceCategory? Category { get; set; }
    public string? Notes { get; set; }

    public bool Matches(Guid? payerId, Guid serviceItemId, ServiceCategory category) =>
        (PayerId is null || PayerId == payerId)
        && (ServiceItemId is { } item ? item == serviceItemId : Category == category);
}

/// <summary>
/// A patient's insurance policy, corporate membership or scheme card (INS-03). The policy number is encrypted
/// (AES-GCM) with a blind index for exact search and shown masked; only an audited request reveals it.
/// </summary>
public sealed class PatientPolicy : AuditableEntity
{
    public Guid PatientId { get; set; }
    public Guid PayerId { get; set; }
    public Guid? TpaId { get; set; }

    [SensitiveData]
    public string PolicyNumberCipher { get; set; } = string.Empty;

    [SensitiveData]
    public string PolicyNumberIndex { get; set; } = string.Empty;

    public string PolicyNumberMasked { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string? PolicyHolderName { get; set; }
    public string? RelationToHolder { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly ValidTo { get; set; }
    public decimal SumInsured { get; set; }
    public decimal CoPayPercent { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public string? VerificationNotes { get; set; }

    /// <summary>Scan of the insurance/TPA card in the encrypted file store.</summary>
    public Guid? CardFileId { get; set; }

    public string? Notes { get; set; }

    public bool IsValidOn(DateOnly date) => IsActive && ValidFrom <= date && date <= ValidTo;

    public void MarkVerified(Guid? userId, string? notes, DateTimeOffset now)
    {
        VerifiedAt = now;
        VerifiedBy = userId;
        VerificationNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }
}
