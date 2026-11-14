namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// What a save of a user's facilities changes (<see cref="AccessAdminEndpoints"/>). All facilities is a choice of its own
/// (<see cref="User.AllFacilities"/>) and an empty list is no facility, so taking a user's facilities away can only narrow
/// what they reach. What narrows applies at once; what widens (a facility added, all facilities) waits for a second person
/// who may approve grants (<see cref="GrantApprovals"/>), and the user keeps what they have meanwhile.
/// </summary>
internal static class FacilityGrant
{
    public const string AllOrSomeMessage = "Give all facilities, or choose some: not both.";

    /// <summary>
    /// Splits a save into what applies at once and what is asked for.
    /// </summary>
    /// <param name="allBefore">The user has all facilities now.</param>
    /// <param name="before">The facilities the user has now (none with <paramref name="allBefore"/>).</param>
    /// <param name="all">All facilities are saved.</param>
    /// <param name="chosen">The facilities saved, without <paramref name="all"/>.</param>
    /// <param name="pending">The grant to the user already waiting, if any: what it asks is not asked for again.</param>
    /// <returns>
    /// Whether the user has all facilities at once (<c>AllNow</c>), the facilities they have at once (<c>Now</c>), the
    /// facilities to ask for (<c>Add</c>), and whether to ask for all of them (<c>AddAll</c>).
    /// </returns>
    public static (bool AllNow, IReadOnlyList<Guid> Now, IReadOnlyList<Guid> Add, bool AddAll) Split(
        bool allBefore, IReadOnlyCollection<Guid> before, bool all, IReadOnlyCollection<Guid> chosen, PendingUserGrant? pending)
    {
        if (all)
        {
            // Asked for, unless the user has it or it waits already; until it is given they keep their own.
            return (allBefore, allBefore ? [] : [.. before], [], !allBefore && pending?.AllFacilities != true);
        }

        if (allBefore)
        {
            // Taken back to some: all facilities covered those chosen, so they apply at once and nothing waits.
            return (false, [.. chosen.Distinct()], [], false);
        }

        return (false, [.. before.Where(chosen.Contains)], [.. chosen.Distinct().Where(f => !before.Contains(f) && pending?.FacilityIds.Contains(f) != true)], false);
    }
}
