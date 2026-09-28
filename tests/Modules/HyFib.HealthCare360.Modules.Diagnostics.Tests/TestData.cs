using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>Builders for domain objects used across the unit tests (no database).</summary>
internal static class TestData
{
    /// <summary>25 Sep 2026, 12:00 IST.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 30, 0, TimeSpan.Zero);

    public static readonly Guid UserId = Guid.CreateVersion7();

    public static ReferenceRange Range(
        decimal? low, decimal? high, decimal? criticalLow = null, decimal? criticalHigh = null, Gender? gender = null, int from = 0, int? to = null,
        string? text = null) => new()
        {
            Gender = gender,
            AgeFromDays = from,
            AgeToDays = to,
            Low = low,
            High = high,
            CriticalLow = criticalLow,
            CriticalHigh = criticalHigh,
            Text = text,
        };

    public static ReferenceRange Answers(string[] normal, string[]? critical = null) => new()
    {
        NormalValues = [.. normal],
        CriticalValues = [.. critical ?? []],
    };

    public static LabTest Numeric(int decimals = 1, string name = "Potassium", string unit = "mmol/L") => new()
    {
        Code = "K",
        Name = name,
        ResultType = LabResultType.Numeric,
        Unit = unit,
        Decimals = decimals,
    };

    public static LabTest Options(params string[] options) => new()
    {
        Code = "OPT",
        Name = "Rapid test",
        ResultType = LabResultType.Options,
        Options = [.. options],
    };

    public static LabSample Sample(SampleStatus status = SampleStatus.Pending, LabSection section = LabSection.Biochemistry) => new()
    {
        FacilityId = Guid.CreateVersion7(),
        LabOrderId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        Barcode = "000000123",
        SpecimenType = SpecimenType.Serum,
        Container = ContainerType.Plain,
        Section = section,
        Status = status,
    };

    public static LabResult Result(LabResultStatus status = LabResultStatus.Pending, ResultFlag? flag = null) => new()
    {
        LabOrderId = Guid.CreateVersion7(),
        LabOrderItemId = Guid.CreateVersion7(),
        LabTestId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        TestCode = "K",
        TestName = "Potassium",
        Unit = "mmol/L",
        ResultType = LabResultType.Numeric,
        Status = status,
        Flag = flag,
    };

    public static ResultCapture Capture(string value, decimal? numeric = null, ResultFlag? flag = ResultFlag.Normal) =>
        new(new InterpretedResult(value, numeric, flag, "3.5 - 5.1"), DeltaOutcome.None, null, null, null, "ISE", null);

    public static LabOrderItem Item(LabOrderItemStatus status, string name = "CBC") => new()
    {
        LabTestId = Guid.CreateVersion7(),
        TestCode = name,
        TestName = name,
        SourceOrderItemId = Guid.CreateVersion7(),
        Status = status,
    };

    /// <summary>Reference intervals of a seeded analyte or single-analyte test, as entities.</summary>
    public static IReadOnlyList<ReferenceRange> SeededRanges(string code)
    {
        var result = LabCatalog.Analytes.FirstOrDefault(a => a.Code == code)?.Result
                     ?? LabCatalog.Orderables.First(o => o.Code == code).Result!;
        return result.Ranges.Select(r => r.ToEntity(Guid.Empty)).ToList();
    }

    public static int Years(int years) => (int)Math.Round(years * 365.25);
}
