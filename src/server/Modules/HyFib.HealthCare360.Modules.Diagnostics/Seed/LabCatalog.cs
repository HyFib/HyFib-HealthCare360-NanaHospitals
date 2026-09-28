using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>A reference interval in the seed catalogue.</summary>
internal sealed record RangeSeed(
    Gender? Gender,
    int AgeFromDays,
    int? AgeToDays,
    decimal? Low,
    decimal? High,
    decimal? CriticalLow = null,
    decimal? CriticalHigh = null,
    string? Text = null,
    IReadOnlyList<string>? Normal = null,
    IReadOnlyList<string>? Critical = null)
{
    public ReferenceRange ToEntity(Guid labTestId) => new()
    {
        LabTestId = labTestId,
        Gender = Gender,
        AgeFromDays = AgeFromDays,
        AgeToDays = AgeToDays,
        Low = Low,
        High = High,
        CriticalLow = CriticalLow,
        CriticalHigh = CriticalHigh,
        Text = Text,
        NormalValues = [.. Normal ?? []],
        CriticalValues = [.. Critical ?? []],
    };
}

/// <summary>How an analyte is resulted: type, unit, precision, intervals and analyser channels.</summary>
internal sealed record ResultSeed(
    LabResultType Type,
    string? Unit,
    int Decimals,
    IReadOnlyList<RangeSeed> Ranges,
    IReadOnlyList<string>? Channels = null,
    IReadOnlyList<string>? Options = null,
    decimal? DeltaPercent = null,
    string? QcGroup = null);

/// <summary>An analyte that is resulted only as part of a panel (not orderable on its own).</summary>
internal sealed record AnalyteSeed(
    string Code, string Name, LabSection Section, SpecimenType Specimen, ContainerType Container, string? Loinc, ResultSeed Result);

/// <summary>A billable test: a panel of analytes, or a single analyte (<see cref="Result"/>).</summary>
internal sealed record OrderableSeed(
    string Code,
    string Name,
    string ShortName,
    decimal Price,
    LabSection Section,
    SpecimenType Specimen,
    ContainerType Container,
    int TatHours,
    string? Method,
    string? Loinc,
    string? Preparation = null,
    string? Interpretation = null)
{
    /// <summary>Panel components (analyte code, optional sub-heading) in report order; empty for a single-analyte test.</summary>
    public IReadOnlyList<(string Code, string? Group)> Components { get; init; } = [];

    /// <summary>Result definition of a single-analyte test.</summary>
    public ResultSeed? Result { get; init; }

    /// <summary>Timed specimen that always gets its own tube (post-prandial glucose).</summary>
    public bool DrawSeparately { get; init; }

    public bool IsPanel => Components.Count > 0;
}

/// <summary>
/// The seeded laboratory catalogue: billable tests with the analytes, reference intervals, critical limits, LOINC codes and
/// analyser channels used by a typical Indian hospital laboratory. Intervals follow commonly used adult and paediatric
/// references (IFCC/CLSI-derived values printed by Indian NABL laboratories), and prices are 2026 INR tariffs of a
/// tier-2 city private hospital. The catalogue is seeded only when a code is missing, so laboratory edits are never
/// overwritten. The <c>LAB-*</c> codes are relied on by other modules (order sets, health-check packages).
/// </summary>
internal static partial class LabCatalog
{
    public static IReadOnlyList<AnalyteSeed> Analytes { get; } =
        [.. HaematologyAnalytes(), .. BiochemistryAnalytes(), .. ImmunologyAnalytes(), .. ClinicalPathologyAnalytes()];

    public static IReadOnlyList<OrderableSeed> Orderables { get; } =
        [.. HaematologyTests(), .. BiochemistryTests(), .. ImmunologyTests(), .. ClinicalPathologyTests()];

    // ------------------------------------------------------------------ interval helpers (ages in days)

    private static int Years(int years) => (int)Math.Round(years * 365.25);

    private static int Months(int months) => (int)Math.Round(months * 30.4375);

    private static RangeSeed All(decimal? low, decimal? high, decimal? criticalLow = null, decimal? criticalHigh = null, string? text = null) =>
        new(null, 0, null, low, high, criticalLow, criticalHigh, text);

    private static RangeSeed Ages(int fromDays, int? toDays, decimal? low, decimal? high, decimal? criticalLow = null, decimal? criticalHigh = null, string? text = null) =>
        new(null, fromDays, toDays, low, high, criticalLow, criticalHigh, text);

    private static RangeSeed Male(int fromDays, int? toDays, decimal? low, decimal? high, decimal? criticalLow = null, decimal? criticalHigh = null, string? text = null) =>
        new(Gender.Male, fromDays, toDays, low, high, criticalLow, criticalHigh, text);

    private static RangeSeed Female(int fromDays, int? toDays, decimal? low, decimal? high, decimal? criticalLow = null, decimal? criticalHigh = null, string? text = null) =>
        new(Gender.Female, fromDays, toDays, low, high, criticalLow, criticalHigh, text);

    private static RangeSeed Answers(IReadOnlyList<string> normal, IReadOnlyList<string>? critical = null, string? text = null) =>
        new(null, 0, null, null, null, Text: text, Normal: normal, Critical: critical);

    private static RangeSeed TextOnly(string text) => new(null, 0, null, null, null, Text: text);

    private static ResultSeed Numeric(string? unit, int decimals, IReadOnlyList<RangeSeed> ranges, string? channel = null, decimal? delta = null, string? qcGroup = null) =>
        new(LabResultType.Numeric, unit, decimals, ranges, channel is null ? null : [channel], DeltaPercent: delta, QcGroup: qcGroup);

    private static ResultSeed Choice(IReadOnlyList<string> options, IReadOnlyList<string>? normal, IReadOnlyList<string>? critical = null, string? text = null) =>
        new(LabResultType.Options, null, 0, normal is null ? [] : [Answers(normal, critical, text)], Options: options);

    private static ResultSeed FreeText(string? unit, string? reference) =>
        new(LabResultType.Text, unit, 0, reference is null ? [] : [TextOnly(reference)]);
}
