namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// Test master (LIS-01). There is one row per analyte ("Haemoglobin") and one per orderable test or panel ("Complete blood
/// count"). A panel lists its analytes as <see cref="Components"/>, and results are always stored per analyte. So an
/// analyte shared by several panels (sodium in the KFT and in the electrolytes panel) keeps one set of reference ranges,
/// one QC history and one delta-check history.
/// </summary>
public sealed class LabTest : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Short name printed on 50 × 25 mm sample labels.</summary>
    public string? ShortName { get; set; }

    public LabSection Section { get; set; }
    public SpecimenType SpecimenType { get; set; }
    public ContainerType Container { get; set; }
    public string? Method { get; set; }

    /// <summary>Target turnaround time (order → release) used by TAT reports.</summary>
    public int TatHours { get; set; }

    public string? Loinc { get; set; }

    /// <summary>Billable catalogue entry (<c>org.service_items</c>) charged when the test is ordered.</summary>
    public Guid? ServiceItemId { get; set; }

    public bool IsOrderable { get; set; }
    public bool IsPanel { get; set; }
    public LabResultType ResultType { get; set; }
    public string? Unit { get; set; }

    /// <summary>Decimal places for numeric results (rounded half away from zero, as printed on the report).</summary>
    public int Decimals { get; set; }

    /// <summary>Allowed answers for <see cref="LabResultType.Options"/> results.</summary>
    public List<string> Options { get; set; } = [];

    /// <summary>
    /// Analyser channels mapped to this analyte: <c>HGB</c> matches any analyser, <c>XN550:HGB</c> only that instrument.
    /// </summary>
    public List<string> InstrumentChannelCodes { get; set; } = [];

    /// <summary>
    /// Tests measured by the same assay (fasting and post-prandial glucose) share quality control: a QC failure on
    /// the assay locks verification of all of them.
    /// </summary>
    public string? QcGroup { get; set; }

    /// <summary>Delta-check threshold (% change from the previous result); null uses the facility default.</summary>
    public decimal? DeltaThresholdPercent { get; set; }

    public bool AllowAutoValidation { get; set; } = true;

    /// <summary>Patient preparation shown at booking and collection ("10–12 hours fasting").</summary>
    public string? Preparation { get; set; }

    /// <summary>
    /// Timed specimen (post-prandial glucose, post-dose level) that is always drawn into its own tube. Otherwise it would
    /// share the fasting tube with tests ordered at the same time and be drawn at the wrong time.
    /// </summary>
    public bool DrawSeparately { get; set; }

    /// <summary>Interpretation note printed under the test on the report.</summary>
    public string? Interpretation { get; set; }

    /// <summary>Reference laboratory the test is outsourced to by default (LIS-08).</summary>
    public Guid? DefaultReferralLabId { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>
    /// Created automatically for an order whose service has no test set-up, so the order is never lost. Masters staff
    /// complete the set-up; results are free text until then.
    /// </summary>
    public bool IsProvisional { get; set; }

    public bool IsActive { get; set; } = true;

    public List<LabTestComponent> Components { get; set; } = [];
    public List<ReferenceRange> Ranges { get; set; } = [];

    /// <summary>Key identifying the assay for quality control.</summary>
    public string QcKey => string.IsNullOrWhiteSpace(QcGroup) ? Code : QcGroup;

    /// <summary>0 = no match, 1 = generic channel match, 2 = instrument-specific match (preferred).</summary>
    public int ChannelMatchScore(string instrumentCode, string channelCode)
    {
        var best = 0;
        foreach (var mapping in InstrumentChannelCodes)
        {
            var separator = mapping.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                if (string.Equals(mapping, channelCode, StringComparison.OrdinalIgnoreCase))
                {
                    best = 1;
                }
            }
            else if (string.Equals(mapping[..separator], instrumentCode, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(mapping[(separator + 1)..], channelCode, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        return best;
    }

    /// <summary>The channel the given analyser uses for this analyte (host query), or null when it is not mapped.</summary>
    public string? ChannelFor(string instrumentCode)
    {
        string? generic = null;
        foreach (var mapping in InstrumentChannelCodes)
        {
            var separator = mapping.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                generic ??= mapping;
            }
            else if (string.Equals(mapping[..separator], instrumentCode, StringComparison.OrdinalIgnoreCase))
            {
                return mapping[(separator + 1)..];
            }
        }

        return generic;
    }
}

/// <summary>An analyte inside a panel, in report order.</summary>
public sealed class LabTestComponent : AuditableEntity
{
    public Guid PanelTestId { get; set; }
    public Guid TestId { get; set; }
    public int DisplayOrder { get; set; }

    /// <summary>Optional sub-heading printed above the analyte ("Differential leucocyte count").</summary>
    public string? GroupName { get; set; }
}

/// <summary>
/// Age- and sex-specific reference interval with critical limits for one analyte (LIS-01). Ages are in days (from is
/// inclusive, to is exclusive) so neonatal, paediatric and adult intervals can be expressed exactly.
/// </summary>
public sealed class ReferenceRange : AuditableEntity
{
    public Guid LabTestId { get; set; }

    /// <summary>Male or Female for a sex-specific interval; null (or Unknown/Other) applies to everyone.</summary>
    public Gender? Gender { get; set; }

    public int AgeFromDays { get; set; }
    public int? AgeToDays { get; set; }
    public decimal? Low { get; set; }
    public decimal? High { get; set; }
    public decimal? CriticalLow { get; set; }
    public decimal? CriticalHigh { get; set; }

    /// <summary>Printed reference text; overrides the generated "Low - High" text (for example guideline bands).</summary>
    public string? Text { get; set; }

    /// <summary>Normal answers for text/option/culture results ("Negative", "Non-reactive", "No growth").</summary>
    public List<string> NormalValues { get; set; } = [];

    /// <summary>Answers that are critical values (for example "Positive for P. falciparum").</summary>
    public List<string> CriticalValues { get; set; } = [];

    public bool IsSexSpecific => Gender is SharedKernel.Gender.Male or SharedKernel.Gender.Female;

    /// <summary>
    /// Whether the interval applies to a patient. When the age is unknown only all-age intervals apply: guessing an
    /// adult interval for a child of unknown age could hide an abnormal result.
    /// </summary>
    public bool Applies(Gender patientGender, int? ageDays)
    {
        if (IsSexSpecific && Gender != patientGender)
        {
            return false;
        }

        if (ageDays is not { } age)
        {
            return AgeFromDays == 0 && AgeToDays is null;
        }

        return age >= AgeFromDays && (AgeToDays is null || age < AgeToDays);
    }

    /// <summary>Consistency rules applied when masters are saved and to the seeded catalogue.</summary>
    public Result Validate()
    {
        if (AgeFromDays < 0 || (AgeToDays is { } to && to <= AgeFromDays))
        {
            return Error.Rule("range.age_band", "The age band must end after it starts.");
        }

        if (Low is { } low && High is { } high && low > high)
        {
            return Error.Rule("range.limits", "The lower limit cannot be above the upper limit.");
        }

        if (CriticalLow is { } criticalLow && ((Low is { } l && criticalLow > l) || (High is { } h && criticalLow >= h)))
        {
            return Error.Rule("range.critical_low", "The critical low limit must be below the reference interval.");
        }

        if (CriticalHigh is { } criticalHigh && ((High is { } hi && criticalHigh < hi) || (Low is { } lo && criticalHigh <= lo)))
        {
            return Error.Rule("range.critical_high", "The critical high limit must be above the reference interval.");
        }

        if (NormalValues.Intersect(CriticalValues, StringComparer.OrdinalIgnoreCase).Any())
        {
            return Error.Rule("range.values", "An answer cannot be both normal and critical.");
        }

        return Result.Success();
    }
}
