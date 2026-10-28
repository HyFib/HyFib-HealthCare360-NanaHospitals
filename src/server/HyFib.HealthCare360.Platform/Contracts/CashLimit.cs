using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// Cash a module takes from patients, for the cash limit (<see cref="CashLimit"/>). Billing and the pharmacy each
/// implement it, so the limit counts the cash of both counters without either module referencing the other.
/// </summary>
public interface ICashReceiptSource
{
    /// <summary>
    /// The cash (wallets included) the patient paid at any of <paramref name="facilityIds"/> from <paramref name="from"/>
    /// until <paramref name="to"/> (not included). Money paid back is not taken off: the Act limits what is received.
    /// </summary>
    Task<decimal> CashReceivedAsync(
        Guid patientId, IReadOnlyCollection<Guid> facilityIds, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>
/// The cash limit of the Income-tax Act (s.269ST): no one may receive ₹2,00,000 or more from a person in a day, for a
/// single transaction, or for one event or occasion, otherwise than by an account-payee cheque or draft or a bank mode of
/// rule 6ABBA (cards, net banking, IMPS, UPI, RTGS, NEFT, BHIM Aadhaar Pay). The penalty is the sum received (s.271DA),
/// and a hospital has no exemption. Cash and wallets are counted for each patient, whoever pays for them: over the
/// hospital's (IST) day at every hospital under the same PAN (one person receives it all), over the bill, invoice or
/// admission the money is for, and in the payment itself. What would reach the limit is refused.
/// </summary>
public sealed class CashLimit(HospitalDbContext db, IEnumerable<ICashReceiptSource> sources, TimeProvider time)
{
    /// <summary>Cash of this much or more may not be taken.</summary>
    public const decimal Limit = 2_00_000m;

    public const string Code = "payments.cash_limit";

    private const string OtherModes = "card, UPI, net banking or an account-payee cheque";

    /// <summary>The check below, for money received now.</summary>
    public Task<Result> CheckAsync(
        Guid facilityId, Guid? patientId, decimal cash, string occasion, Func<CancellationToken, Task<decimal>> takenFor, CancellationToken ct) =>
        CheckAsync(facilityId, patientId, cash, occasion, takenFor, time.GetUtcNow(), ct);

    /// <summary>
    /// Refuses <paramref name="cash"/> when, with what was taken before, it would make <see cref="Limit"/> or more in one
    /// payment, for the <paramref name="occasion"/> (<paramref name="takenFor"/> counts the cash it took), or from the
    /// patient in the hospital's day; the counter is told how much less to take in cash. It runs inside the transaction
    /// that records the money, holding the patient's cash lock until that ends, so two counters taking cash from the same
    /// patient at once are counted one after the other.
    /// </summary>
    /// <param name="patientId">Null for a customer who is not a registered patient: only the payment and the occasion count.</param>
    /// <param name="occasion">What the money is for, as the counter is told: "admission", "bill" or "invoice".</param>
    /// <param name="receivedAt">
    /// When the money was received: the day counted is its day. A payment a gateway took before midnight may be recorded
    /// after it.
    /// </param>
    public async Task<Result> CheckAsync(
        Guid facilityId, Guid? patientId, decimal cash, string occasion, Func<CancellationToken, Task<decimal>> takenFor, DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        if (cash <= 0)
        {
            return Result.Success();
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Check the cash limit inside the transaction that records the money: the patient's cash lock depends on it.");
        }

        var takenToday = 0m;
        if (patientId is { } patient)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey(patient)})", ct);
            var facilities = await SamePersonAsync(facilityId, ct);
            var (from, to) = DayOf(receivedAt);
            foreach (var source in sources)
            {
                takenToday += await source.CashReceivedAsync(patient, facilities, from, to, ct);
            }
        }

        var day = LocalDate(receivedAt) == LocalDate(time.GetUtcNow()) ? "today" : $"on {LocalDate(receivedAt):dd-MM-yyyy}";

        // The tightest of the three: on a tie, the payment, then what the money is for, then the day.
        (string Over, string? Taken, decimal SoFar)[] limits =
        [
            ("in one payment", null, 0m),
            ($"for one {occasion}", $"for this {occasion}", await takenFor(ct)),
            ("from one patient in a day", $"from this patient {day}", takenToday),
        ];
        var (over, taken, soFar) = limits.MaxBy(l => l.SoFar);
        if (soFar + cash < Limit)
        {
            return Result.Success();
        }

        var refused = $"Income-tax Act s.269ST: ₹{Money.FormatInr(Limit)} or more cannot be taken in cash (wallets included) {over}. ";
        var room = Limit - soFar;
        return Error.Rule(Code, taken is null
            ? $"{refused}Take less than ₹{Money.FormatInr(Limit)} in cash and the rest by {OtherModes}."
            : room > 0
                ? $"{refused}₹{Money.FormatInr(soFar)} has been taken in cash {taken}, so take less than ₹{Money.FormatInr(room)} in cash and the rest by {OtherModes}."
                : $"{refused}₹{Money.FormatInr(soFar)} has been taken in cash {taken}, so take it by {OtherModes}.");
    }

    /// <summary>
    /// The hospitals under the PAN of <paramref name="facilityId"/>: one person receives what any of them takes. Without a
    /// PAN they cannot be told apart, so every hospital of this server counts.
    /// </summary>
    private async Task<List<Guid>> SamePersonAsync(Guid facilityId, CancellationToken ct)
    {
        var pan = (await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Pan).FirstOrDefaultAsync(ct))?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(pan)
            ? await db.Set<Facility>().Select(f => f.Id).ToListAsync(ct)
            : await db.Set<Facility>().Where(f => f.Id == facilityId || (f.Pan != null && f.Pan.Trim().ToUpper() == pan)).Select(f => f.Id).ToListAsync(ct);
    }

    /// <summary>The hospital's (IST) day <paramref name="at"/> falls in, from its first instant until the next day's.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) DayOf(DateTimeOffset at)
    {
        var day = LocalDate(at).ToDateTime(TimeOnly.MinValue);
        var next = day.AddDays(1);
        return (new DateTimeOffset(day, HospitalTimeZone.Zone.GetUtcOffset(day)).ToUniversalTime(),
            new DateTimeOffset(next, HospitalTimeZone.Zone.GetUtcOffset(next)).ToUniversalTime());
    }

    private static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, HospitalTimeZone.Zone).DateTime);

    /// <summary>Stable 64-bit key of the patient's cash lock (identical on every server node).</summary>
    private static long LockKey(Guid patientId) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"payments:cash:{patientId:N}")));
}
