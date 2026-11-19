using System.Linq.Expressions;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore.Query;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Which tube a tube number typed by staff, or sent back by an analyser, may find. Tube numbers end in a Damm check digit
/// (<see cref="DiagnosticsNumberSeries.LabSample"/>), so a number whose last digit does not match the others was typed or
/// read wrong: it finds no tube, and one digit typed wrong or two neighbouring digits swapped never reach another
/// patient's tube, such as the one drawn just before or after. Every place that finds a tube by a number someone typed or
/// an analyser sent goes through <see cref="MatchingAsync(string, CancellationToken)"/>: analyser results and host
/// queries, receipt at the lab counter, and the lab screens' look-up and search.
/// <para>
/// A database from before the check digit keeps the tubes it numbered without one, and finds them by their exact number:
/// <see cref="CheckDigitSinceSetting"/> says when its numbers gained the check digit, and only a tube made before then
/// may be found by a number that does not check, and only while no tube made since has a number it could be a mistyping
/// of (<see cref="Near"/>). New numbers start just above the old ones, so the first new tube's number typed with its last
/// digit wrong is often an old tube's: that finds neither. A tube made since is never found by a number that does not
/// check.
/// </para>
/// <para>
/// What a number finds is a filter on tubes that holds the number and a time as values, and nothing else: it goes into
/// the caller's own query (a search, a look-up) and is translated there, every source of that query keeping its filters.
/// Whether a tube made since is one slip away is read first, in a query of its own: that reads every facility's tubes,
/// and inside the caller's query EF Core would lift the facility filter from all of it, which QueryFilterGuard refuses
/// (once the setting was written, the lab's searches answered 500 and analyser results for such a number were refused).
/// </para>
/// </summary>
internal sealed class TubeNumbers(HospitalDbContext db, ISettingsService settings)
{
    /// <summary>
    /// The hospital-wide setting holding when this database's tube numbers started ending in a check digit (UTC), which
    /// the Diagnostics seeder writes as it brings the tube numbers to it. Not set on a database that never numbered a tube
    /// without one.
    /// </summary>
    public const string CheckDigitSinceSetting = "lab.tubeCheckDigitSince";

    /// <summary>The error a lab screen gets for a mistyped tube number (<see cref="IsMistyped"/>), with <see cref="MistypedMessage"/>.</summary>
    public const string MistypedCode = "lab.tube_number_mistyped";

    /// <summary>True when <paramref name="number"/> is digits that end in their check digit, as every tube number now does.</summary>
    public static bool Verifies(string number) => number.Length > 1 && number.All(char.IsAsciiDigit) && DammCheckDigit.Verifies(number);

    /// <summary>
    /// True for a tube number with a digit typed wrong or two neighbouring digits swapped: 10 digits, the last of which does
    /// not match the others.
    /// </summary>
    public static bool IsMistyped(string number) => number.Length == 10 && number.All(char.IsAsciiDigit) && !DammCheckDigit.Verifies(number);

    /// <summary>
    /// When this database's tube numbers started ending in a check digit; null when every tube it has carries one, or when
    /// the setting holds no time (then no number that does not check finds a tube).
    /// </summary>
    public async Task<DateTimeOffset?> CheckDigitSinceAsync(CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<DateTimeOffset?>(CheckDigitSinceSetting, null, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tubes <paramref name="number"/> may find, as of this database: when its tube numbers gained their check digit,
    /// and whether any facility's tube made since has a number <paramref name="number"/> could be a mistyping of, which is
    /// read in a query of its own. The rule is
    /// <see cref="MatchingAsync(string, DateTimeOffset?, Func{Expression{Func{LabSample, bool}}, Task{bool}})"/>.
    /// </summary>
    public async Task<Expression<Func<LabSample, bool>>> MatchingAsync(string number, CancellationToken ct) =>
        await MatchingAsync(number, await CheckDigitSinceAsync(ct), madeSince =>
            db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(madeSince, ct));

    /// <summary>
    /// The tubes <paramref name="number"/> may find: the one with that number when it ends in its check digit. Otherwise
    /// only one numbered before tube numbers had a check digit (made before <paramref name="checkDigitSince"/>), and only
    /// when no tube made since has a number <paramref name="number"/> could be a mistyping of (<see cref="Near"/>), which
    /// <paramref name="anyTube"/> says: whether any tube meets the condition it is given. None in a database whose tubes
    /// all have a check digit. The filter holds the number and the time as values only, so any query on tubes can take it.
    /// </summary>
    public static async Task<Expression<Func<LabSample, bool>>> MatchingAsync(
        string number, DateTimeOffset? checkDigitSince, Func<Expression<Func<LabSample, bool>>, Task<bool>> anyTube)
    {
        if (Verifies(number))
        {
            return s => s.Barcode == number;
        }

        if (checkDigitSince is not { } since)
        {
            return s => false;
        }

        string[] near = [.. Near(number)];
        if (near.Length > 0 && await anyTube(t => near.Contains(t.Barcode) && t.CreatedAt >= since))
        {
            return s => false;
        }

        return s => s.Barcode == number && s.CreatedAt < since;
    }

    /// <summary>
    /// A search's condition on a tube: one <paramref name="byNumber"/> finds
    /// (<see cref="MatchingAsync(string, CancellationToken)"/>), or one <paramref name="otherwise"/> finds (by its patient,
    /// by its order). One condition with one parameter, made by putting the tube of <paramref name="byNumber"/> in place of
    /// that of <paramref name="otherwise"/>, not by calling either: EF translates it in the search's own query.
    /// </summary>
    public static Expression<Func<LabSample, bool>> Or(Expression<Func<LabSample, bool>> byNumber, Expression<Func<LabSample, bool>> otherwise)
    {
        var tube = byNumber.Parameters[0];
        return Expression.Lambda<Func<LabSample, bool>>(
            Expression.OrElse(byNumber.Body, ReplacingExpressionVisitor.Replace(otherwise.Parameters[0], tube, otherwise.Body)), tube);
    }

    /// <summary>
    /// The numbers that end in their check digit and differ from <paramref name="number"/> in one digit, or in two
    /// neighbouring digits swapped: the tube numbers it can be a mistyping of. One in each place for a wrong digit (the check
    /// digit decides which), and one for each swap that checks.
    /// </summary>
    public static IReadOnlyList<string> Near(string number)
    {
        var near = new List<string>();
        if (number.Length < 2 || !number.All(char.IsAsciiDigit))
        {
            return near;
        }

        var digits = number.ToCharArray();
        for (var place = 0; place < digits.Length; place++)
        {
            var own = digits[place];
            for (var digit = '0'; digit <= '9'; digit++)
            {
                digits[place] = digit;
                if (digit != own && DammCheckDigit.Verifies(digits))
                {
                    near.Add(new string(digits));
                }
            }

            digits[place] = own;
        }

        for (var place = 0; place + 1 < digits.Length; place++)
        {
            if (digits[place] != digits[place + 1])
            {
                (digits[place], digits[place + 1]) = (digits[place + 1], digits[place]);
                if (DammCheckDigit.Verifies(digits))
                {
                    near.Add(new string(digits));
                }

                (digits[place], digits[place + 1]) = (digits[place + 1], digits[place]);
            }
        }

        return [.. near.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>What staff are told when they type a mistyped tube number (<see cref="IsMistyped"/>).</summary>
    public static string MistypedMessage(string number) =>
        $"{number} is not a tube number: its last digit, the check digit, does not match the others. A digit was typed wrong, " +
        "or two were swapped. Check the number written on the tube and type it again.";

    /// <summary>Why an analyser's results for a mistyped tube number (<see cref="IsMistyped"/>) are kept for the lab.</summary>
    public static string MistypedByAnalyser(string instrumentCode, string number) =>
        $"{instrumentCode} sent sample {number}, which is not a tube number: its last digit, the check digit, does not match the " +
        "others, so a digit was typed wrong on the analyser or two were swapped. No tube was changed. Find the tube that was run " +
        "and put these results on its tests.";
}
