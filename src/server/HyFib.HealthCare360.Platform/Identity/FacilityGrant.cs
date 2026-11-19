namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// What a save of a user's facilities changes (<see cref="AccessAdminEndpoints"/>). All facilities is a choice of its own
/// (<see cref="User.AllFacilities"/>) and an empty list is no facility, so taking a user's facilities away can only narrow
/// what they reach. What narrows applies at once; what widens (a facility added, all facilities) waits for a second person
/// who may approve grants (<see cref="GrantApprovals"/>), and the user keeps what they have meanwhile.
/// What narrows applies at once to the sessions open in a facility too (<see cref="KeepsSession"/>): a session is signed
/// in to one facility, and its tokens carry it, so a facility taken away ends the user's sessions there, and a session in
/// a facility the user no longer works in, or one taken out of use, is not renewed (<see cref="AuthService.RefreshAsync"/>).
/// </summary>
internal static class FacilityGrant
{
    public const string AllOrSomeMessage = "Give all facilities, or choose some: not both.";

    /// <summary>Why a session ended: its facility was taken away from the user.</summary>
    public const string FacilityRemovedReason = "facility_removed";

    /// <summary>Why a session ended: its facility was taken out of use.</summary>
    public const string FacilityInactiveReason = "facility_inactive";

    /// <summary>
    /// Whether a session signed in to <paramref name="sessionFacility"/> may go on, for a user who works in every facility
    /// (<paramref name="everyFacility"/>: given all of them, or a Super Administrator) or in <paramref name="facilities"/>.
    /// A session with no facility (a patient's, or a Super Administrator's before the hospital has a facility) is not
    /// limited to one, and is not ended for one.
    /// </summary>
    public static bool KeepsSession(Guid? sessionFacility, bool everyFacility, IReadOnlyCollection<Guid> facilities) =>
        sessionFacility is not { } facility || everyFacility || facilities.Contains(facility);

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
