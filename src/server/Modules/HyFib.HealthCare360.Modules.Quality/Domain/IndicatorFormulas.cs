namespace HyFib.HealthCare360.Modules.Quality.Domain;

/// <summary>A stretch of a stay in one kind of bed (critical care or not), from the bed assignments.</summary>
public readonly record struct CareSegment(bool CriticalCare, DateTimeOffset From, DateTimeOffset? To);

/// <summary>A transfer out of critical care to a ward, and whether the patient came back within the window.</summary>
public readonly record struct IcuTransferOut(DateTimeOffset At, bool Returned, DateTimeOffset? ReturnedAt);

/// <summary>An operation, for the unplanned-return arithmetic.</summary>
public readonly record struct Operation(Guid Id, Guid PatientId, Guid? AdmissionId, bool Elective, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);

/// <summary>A spell of ventilation, for the re-intubation arithmetic.</summary>
public readonly record struct VentilationSpell(Guid Id, Guid AdmissionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool EndedByExtubation);

/// <summary>An extubation, and whether the patient was re-intubated within the window.</summary>
public readonly record struct Extubation(Guid SpellId, DateTimeOffset At, bool Reintubated, DateTimeOffset? ReintubatedAt);

/// <summary>
/// The arithmetic of the computed NABH indicators, apart from the queries that feed it, so each definition can be checked
/// against a worked example. Days are local (IST) calendar day numbers (<see cref="DateOnly.DayNumber"/>).
/// </summary>
public static class IndicatorFormulas
{
    /// <summary>NABH's 48-hour window for returns to ICU and OT and for re-intubation.</summary>
    public static readonly TimeSpan ReturnWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// Total and count of the waits in minutes (numerator and denominator of an average time). A wait whose end is before its
    /// start is a recording error and is left out, not counted as zero.
    /// </summary>
    public static (decimal TotalMinutes, int Count) Waits(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans)
    {
        decimal total = 0;
        var count = 0;
        foreach (var (start, end) in spans)
        {
            if (end < start)
            {
                continue;
            }

            total += (decimal)(end - start).TotalMinutes;
            count++;
        }

        return (Math.Round(total, 2, MidpointRounding.AwayFromZero), count);
    }

    /// <summary>
    /// In-patient days a stay gives the period, by the midnight census (as the in-patient census counts them): the patient
    /// counts at the midnight ending each day from the admission day to the day before discharge, so a same-day stay gives none.
    /// </summary>
    public static int CensusDays(int admittedDay, int? dischargedDay, int fromDay, int toDay)
    {
        var first = Math.Max(fromDay, admittedDay);
        var last = dischargedDay is { } end ? Math.Min(toDay, end - 1) : toDay;
        return Math.Max(0, last - first + 1);
    }

    /// <summary>Length of a stay in days for ALOS: discharge day minus admission day, a same-day stay counting one.</summary>
    public static int StayDays(int admittedDay, int dischargedDay) => Math.Max(1, dischargedDay - admittedDay);

    /// <summary>
    /// The transfers out of critical care in a stay, and for each whether the patient was back in critical care within
    /// the window. Back-to-back critical-care beds are one spell (a move within ICU is not a transfer out); a spell ending
    /// with the end of the stay (discharge, death) is not a transfer out.
    /// </summary>
    public static IReadOnlyList<IcuTransferOut> IcuTransfersOut(IEnumerable<CareSegment> segments, TimeSpan window)
    {
        var blocks = new List<(bool Critical, DateTimeOffset From, DateTimeOffset? To)>();
        foreach (var s in segments.OrderBy(s => s.From))
        {
            if (blocks.Count > 0 && blocks[^1].Critical == s.CriticalCare)
            {
                var last = blocks[^1];
                blocks[^1] = (last.Critical, last.From, s.To is null || last.To is null ? null : s.To > last.To ? s.To : last.To);
                continue;
            }

            blocks.Add((s.CriticalCare, s.From, s.To));
        }

        var result = new List<IcuTransferOut>();
        for (var i = 0; i < blocks.Count - 1; i++)
        {
            if (!blocks[i].Critical || blocks[i].To is not { } outAt)
            {
                continue;
            }

            var back = blocks.Skip(i + 1).FirstOrDefault(b => b.Critical);
            var returned = back.Critical && back.From - outAt <= window;
            result.Add(new IcuTransferOut(outAt, returned, returned ? back.From : null));
        }

        return result;
    }

    /// <summary>
    /// The operations that are unplanned returns: not elective, and started within the window after the same patient's
    /// previous operation in the same stay (the same patient's previous operation when neither has a stay) was completed.
    /// </summary>
    public static IReadOnlySet<Guid> UnplannedReturns(IEnumerable<Operation> operations, TimeSpan window)
    {
        var returns = new HashSet<Guid>();
        foreach (var group in operations.GroupBy(o => (o.PatientId, o.AdmissionId)))
        {
            var ordered = group.OrderBy(o => o.StartedAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var previous = ordered[i - 1];
                var gap = current.StartedAt - previous.CompletedAt;
                if (!current.Elective && gap >= TimeSpan.Zero && gap <= window)
                {
                    returns.Add(current.Id);
                }
            }
        }

        return returns;
    }

    /// <summary>
    /// The extubations of a set of ventilation spells, and for each whether a new spell began in the same stay within the
    /// window. A spell ended by death, by leaving or by a tube change is not an extubation.
    /// </summary>
    public static IReadOnlyList<Extubation> Extubations(IEnumerable<VentilationSpell> spells, TimeSpan window)
    {
        var result = new List<Extubation>();
        foreach (var stay in spells.GroupBy(s => s.AdmissionId))
        {
            var ordered = stay.OrderBy(s => s.StartedAt).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var spell = ordered[i];
                if (!spell.EndedByExtubation || spell.EndedAt is not { } endedAt)
                {
                    continue;
                }

                var next = ordered.Skip(i + 1).Where(s => s.StartedAt >= endedAt).Select(s => (VentilationSpell?)s).FirstOrDefault();
                var again = next is { } n && n.StartedAt - endedAt <= window;
                result.Add(new Extubation(spell.Id, endedAt, again, again ? next!.Value.StartedAt : null));
            }
        }

        return result;
    }
}
