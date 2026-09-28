namespace HyFib.HealthCare360.Modules.Engagement.Features;

/// <summary>A labelled count for breakdown charts (by month, department, type…).</summary>
public sealed record CountByLabel(string Label, int Count);
