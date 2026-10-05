using System.Xml.Linq;
using HyFib.HealthCare360.Integrations.Persistence;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// A voucher in the simulator's Tally company: one row per company and remote id, so importing a voucher again alters
/// it, as Tally does, and the read-back check has a copy to compare. The voucher is kept as the <c>VOUCHER</c> element
/// HC360 sent (ledgers renamed by the ledger map), which the check reads with the same code that reads Tally's export.
/// Training data only.
/// </summary>
[NotAudited]
public sealed class SimulatorTallyVoucher : Entity
{
    public string Company { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string VoucherType { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Xml { get; set; } = string.Empty;

    /// <summary>How many later imports altered it.</summary>
    public int Alterations { get; set; }

    public Guid? FacilityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SimulatorTallyVoucherConfiguration : IEntityTypeConfiguration<SimulatorTallyVoucher>
{
    public void Configure(EntityTypeBuilder<SimulatorTallyVoucher> b)
    {
        b.ToTable("simulator_tally_vouchers", IntegrationsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Company).HasMaxLength(200);
        b.Property(x => x.RemoteId).HasMaxLength(200);
        b.Property(x => x.VoucherType).HasMaxLength(64);
        b.Property(x => x.Number).HasMaxLength(100);
        b.Property(x => x.Xml).HasMaxLength(SimulatedTallyCompany.MaxVoucherXml);
        b.HasIndex(x => new { x.Company, x.RemoteId }).IsUnique();
        b.HasIndex(x => new { x.Company, x.Date });
    }
}

/// <summary>
/// Reads and writes the simulator's Tally company. It uses its own DbContext, like <see cref="Simulator.SimulatorInbox"/>:
/// the outbox dispatcher holds an open transaction on the scoped context, and training data must neither ride on nor
/// roll back with its work.
/// </summary>
internal sealed class SimulatedTallyCompany(IDbContextFactory<HospitalDbContext> contexts, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>A voucher with dozens of ledger lines stays well below this.</summary>
    public const int MaxVoucherXml = 65_536;

    /// <summary>Creates each voucher, or alters the one the company already holds with its remote id.</summary>
    public async Task<IReadOnlyList<AccountingVoucherOutcome>> ImportAsync(
        string company, IReadOnlyList<(AccountingVoucher Voucher, XElement Xml)> vouchers, Guid? facilityId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var ids = vouchers.Select(v => v.Voucher.RemoteId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var held = await db.Set<SimulatorTallyVoucher>()
            .Where(v => v.Company == company && ids.Contains(v.RemoteId))
            .ToDictionaryAsync(v => v.RemoteId, StringComparer.Ordinal, cancellationToken);
        var now = time.GetUtcNow();
        var outcomes = new List<AccountingVoucherOutcome>(vouchers.Count);
        foreach (var (voucher, xml) in vouchers)
        {
            // Tally creates a new voucher every time one without a remote id is imported.
            var remoteId = voucher.RemoteId ?? "NO-REMOTEID-" + Guid.CreateVersion7().ToString("N");
            var text = xml.ToString(SaveOptions.DisableFormatting);
            if (text.Length > MaxVoucherXml)
            {
                throw new IntegrationException($"Voucher {voucher.Number} is too large for the simulator.");
            }

            if (held.TryGetValue(remoteId, out var row))
            {
                row.Alterations++;
                outcomes.Add(AccountingVoucherOutcome.Altered);
            }
            else
            {
                row = new SimulatorTallyVoucher { Company = company, RemoteId = remoteId, CreatedAt = now };
                db.Add(row);
                held[remoteId] = row;
                outcomes.Add(AccountingVoucherOutcome.Created);
            }

            row.Date = voucher.Date;
            row.VoucherType = IntegrationTrace.Truncate(voucher.VoucherType.Trim(), 64)!;
            row.Number = IntegrationTrace.Truncate(voucher.Number.Trim(), 100)!;
            row.Amount = Money.Round(voucher.Amount);
            row.Xml = text;
            row.FacilityId = facilityId ?? currentUser.FacilityId;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return outcomes;
    }

    /// <summary>The company's vouchers of the day, as Tally's collection export would list them.</summary>
    public async Task<string> ExportDayAsync(string company, DateOnly date, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var vouchers = await db.Set<SimulatorTallyVoucher>().AsNoTracking()
            .Where(v => v.Company == company && v.Date == date)
            .OrderBy(v => v.CreatedAt)
            .Select(v => v.Xml)
            .ToListAsync(cancellationToken);
        return "<ENVELOPE><HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER><BODY><DESC></DESC><DATA><COLLECTION>"
               + string.Concat(vouchers)
               + "</COLLECTION></DATA></BODY></ENVELOPE>";
    }
}
