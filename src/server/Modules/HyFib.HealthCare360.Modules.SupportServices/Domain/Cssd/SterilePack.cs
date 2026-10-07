using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public enum PackStatus
{
    /// <summary>In a load that has not been released yet.</summary>
    InProcess = 0,

    /// <summary>Sterile and on the CSSD shelf.</summary>
    Available,

    Issued,

    /// <summary>Came back soiled for reprocessing.</summary>
    Returned,

    Recalled,

    /// <summary>Its load failed before release: not sterile, send back for reprocessing.</summary>
    Reprocess,
}

/// <summary>
/// One labelled pack of an instrument set in a sterilizer load. The label carries the cycle number and the expiry date,
/// which is what makes issue with expiry and recall by load possible (CSSD-04/05). The concurrency version stops the
/// same pack being issued twice from two counters.
/// </summary>
public sealed class SterilePack : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LoadId { get; set; }
    public Guid InstrumentSetId { get; set; }

    /// <summary>The wash cycle the pack's tray went through before packing, when the load names it.</summary>
    public Guid? WashCycleId { get; set; }

    public string PackNumber { get; set; } = string.Empty;

    /// <summary>Snapshot of the set's shelf life when the pack was loaded.</summary>
    public int ShelfLifeDays { get; set; }

    public DateOnly? SterileUntil { get; set; }
    public PackStatus Status { get; set; }
    public Guid? IssuedToDepartmentId { get; set; }
    public uint Version { get; set; }

    /// <summary>Sterility lasts through the <see cref="SterileUntil"/> date.</summary>
    public bool IsExpired(DateOnly today) => SterileUntil is { } until && until < today;

    public void MarkSterile(DateOnly releaseDate)
    {
        if (Status == PackStatus.InProcess)
        {
            Status = PackStatus.Available;
            SterileUntil = releaseDate.AddDays(ShelfLifeDays);
        }
    }

    public Result Issue(Guid departmentId, DateOnly today)
    {
        if (Status != PackStatus.Available)
        {
            return Error.Rule("cssd.pack_unavailable", $"Pack {PackNumber} is {Status.ToString().ToLowerInvariant()} and cannot be issued.");
        }

        if (IsExpired(today))
        {
            return Error.Rule("cssd.pack_expired",
                $"Pack {PackNumber} expired on {SterileUntil!.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}. Send it for reprocessing.");
        }

        Status = PackStatus.Issued;
        IssuedToDepartmentId = departmentId;
        return Result.Success();
    }
}
