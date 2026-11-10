using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>The numbers issued ahead of a downtime besides UHIDs, each from its own series.</summary>
public enum DowntimeNumberKind
{
    Visit = 0,
    OpBill,
    IpBill,
    Receipt,
}

/// <summary>
/// The series a <see cref="DowntimeNumberKind"/> takes its numbers from, registered by the module that issues them
/// (visits by Patients, bills and receipts by Billing), so a block reserves from the same series as the desk would.
/// </summary>
public sealed record DowntimeNumberSeries(DowntimeNumberKind Kind, NumberSeriesDefinition Series, BackEntryRecord Record, string Name);

/// <summary>
/// A block of visit, bill or receipt numbers issued ahead of time to one counter of a facility, printed on its downtime
/// pack. While HC360 is down the counter writes each visit slip, bill or receipt under the next number of the block, in
/// order; when HC360 is back each is entered under the number on the paper (<see cref="DowntimeNumber"/>). The numbers are
/// reserved from the series at once, so HC360 never issues them again, and a block no longer needed is returned: its
/// numbers not entered are voided together, and the return is audited.
/// </summary>
public sealed class DowntimeNumberBlock : AuditableEntity
{
    public Guid FacilityId { get; set; }
    public DowntimeNumberKind Kind { get; set; }

    /// <summary>The counter that keeps the block: "OPD billing counter 1", "Casualty registration".</summary>
    public string Counter { get; set; } = string.Empty;

    public int Count { get; set; }
    public string FirstNumber { get; set; } = string.Empty;
    public string LastNumber { get; set; } = string.Empty;
    public string? IssuedByName { get; set; }

    /// <summary>
    /// When the series starts again from 1, and the period the numbers were counted in ("2026", "202611"), at issue: a
    /// paper record dated in another period would carry a number of the wrong year or month (a GST invoice's serial
    /// number is unique within its financial year), so it is refused.
    /// </summary>
    public NumberResetPolicy ResetPolicy { get; set; }

    public string? Period { get; set; }

    public DateTimeOffset? ReturnedAt { get; set; }
    public string? ReturnedByName { get; set; }
    public string? ReturnNote { get; set; }
}

/// <summary>One number of a <see cref="DowntimeNumberBlock"/>: open, entered for the record made from the paper, or voided.</summary>
public sealed class DowntimeNumber : AuditableEntity
{
    public Guid BlockId { get; set; }

    /// <summary>Its place in the block, from 1, as printed.</summary>
    public int Position { get; set; }

    public string Number { get; set; } = string.Empty;

    /// <summary>The record entered under it: a visit, a bill or a receipt.</summary>
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }
    public Guid? PatientId { get; set; }

    /// <summary>The time on the paper.</summary>
    public DateTimeOffset? PaperAt { get; set; }

    public DateTimeOffset? EnteredAt { get; set; }
    public string? EnteredByName { get; set; }

    /// <summary>The late back-entry approval it was entered under, when the paper was older than the window.</summary>
    public Guid? LateGrantId { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedByName { get; set; }

    public DowntimeUhidStatus Status => EntityId is not null
        ? DowntimeUhidStatus.Entered
        : VoidedAt is not null ? DowntimeUhidStatus.Voided : DowntimeUhidStatus.Open;
}
