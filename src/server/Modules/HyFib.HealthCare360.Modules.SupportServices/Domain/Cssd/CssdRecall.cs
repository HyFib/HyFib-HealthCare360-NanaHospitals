namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public sealed record PackSnapshot(Guid PackId, PackStatus Status);

public sealed record IssueSnapshot(Guid IssueId, Guid PackId, SetIssueStatus Status, Guid? PatientId, bool AlreadyFlagged);

/// <summary>What a recall must touch.</summary>
public sealed record RecallPlan(IReadOnlyList<Guid> IssuesToFlag, IReadOnlyList<Guid> PacksToRecall, IReadOnlyList<Guid> PatientsExposed)
{
    public static RecallPlan None { get; } = new([], [], []);

    public bool IsEmpty => IssuesToFlag.Count == 0 && PacksToRecall.Count == 0;
}

/// <summary>
/// Recall by load (CSSD-05). When a released load's biological indicator fails, every pack from it is suspect: packs
/// still on the shelf or on a ward are pulled back, and every issue is flagged — including packs already used or
/// returned, because their patients must be reviewed by infection control. Nothing is recalled unless the biological
/// indicator failed after release (an unreleased failed load never left CSSD).
/// </summary>
public static class CssdRecall
{
    public static RecallPlan Plan(
        IndicatorResult biologicalIndicator, bool wasReleased, IReadOnlyCollection<PackSnapshot> packs, IReadOnlyCollection<IssueSnapshot> issues)
    {
        if (biologicalIndicator != IndicatorResult.Fail || !wasReleased)
        {
            return RecallPlan.None;
        }

        var issuesToFlag = issues.Where(i => !i.AlreadyFlagged).Select(i => i.IssueId).ToList();
        var packsToRecall = packs.Where(p => p.Status is PackStatus.Available or PackStatus.Issued).Select(p => p.PackId).ToList();
        var patients = issues.Where(i => i.PatientId is not null).Select(i => i.PatientId!.Value).Distinct().ToList();
        return new RecallPlan(issuesToFlag, packsToRecall, patients);
    }
}
