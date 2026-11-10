using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// One kind of work a doctor still has ahead in a module: OPD appointments booked with them, patients waiting for a slot
/// with them, video consultations, patients admitted under them, surgeries booked with them. Taken out of use, nobody can
/// book or assign them again, and this work has to be moved to another doctor or cancelled.
/// </summary>
/// <param name="Kind">A stable name for the kind of work: <c>appointments</c>, <c>waitlist</c>, <c>telehealth</c>, <c>admissions</c>, <c>surgeries</c>.</param>
/// <param name="Count">How many.</param>
/// <param name="First">When the first of them is (an appointment's start, an admission's date); null when it has no time.</param>
/// <param name="Numbers">The first few by number (APT-…, IP-…), for the person to look them up; the rest are counted.</param>
/// <param name="Words">The work in words for the person taking the doctor out of use ("12 OPD appointments from 05 Nov 2026").</param>
public sealed record PractitionerWork(string Kind, int Count, DateTimeOffset? First, IReadOnlyList<string> Numbers, string Words);

/// <summary>
/// What a module holds that a doctor still has ahead. Taking the doctor out of use is refused while any is, naming it,
/// until the person says to go ahead anyway; each module that books or assigns doctors registers one.
/// </summary>
public interface IPractitionerWorkSource
{
    /// <summary>The doctor's work ahead in this module, every facility; empty when there is none.</summary>
    Task<IReadOnlyList<PractitionerWork>> WorkAheadAsync(Guid practitionerId, CancellationToken cancellationToken);
}

/// <summary>Every module's <see cref="IPractitionerWorkSource"/>, asked together by the practitioners master.</summary>
public sealed class PractitionerWorkSources(IEnumerable<IPractitionerWorkSource> sources)
{
    public const string WorkAheadCode = "practitioner.work_ahead";

    /// <summary>How many of each kind a refusal names by number; the rest are counted.</summary>
    public const int Named = 5;

    /// <summary>The doctor's work ahead in every module, in the order the modules registered.</summary>
    public async Task<IReadOnlyList<PractitionerWork>> WorkAheadAsync(Guid practitionerId, CancellationToken cancellationToken)
    {
        List<PractitionerWork> work = [];
        foreach (var source in sources)
        {
            work.AddRange(await source.WorkAheadAsync(practitionerId, cancellationToken));
        }

        return work;
    }

    /// <summary>
    /// The refusal to take <paramref name="doctor"/> out of use while <paramref name="work"/> is ahead: what it is, and
    /// that saving again with <c>takeOutOfUseAnyway</c> goes ahead. The work is in the problem's data for the screen.
    /// </summary>
    public static Error WorkAhead(string doctor, IReadOnlyList<PractitionerWork> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Error.Conflict(WorkAheadCode,
            $"{doctor} still has {string.Join("; ", work.Select(w => w.Words))}. Out of use, nobody can book or assign {doctor} again, " +
            "and these have to be moved to another doctor or cancelled. Save again, saying to take the doctor out of use anyway, to go ahead.",
            new { workAhead = work });
    }
}
