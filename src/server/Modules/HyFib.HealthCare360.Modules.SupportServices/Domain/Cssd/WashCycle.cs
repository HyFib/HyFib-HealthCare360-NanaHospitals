namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public enum WashCycleStatus
{
    /// <summary>The trays are in the washer-disinfector.</summary>
    Running = 0,

    /// <summary>Disinfected and inspected clean: the trays can be packed and sterilized.</summary>
    Passed,

    /// <summary>The cycle did not disinfect, the trays were not clean, or it was stopped: wash them again.</summary>
    Failed,
}

/// <summary>
/// One washer-disinfector cycle (CSSD-04): the trays received soiled are cleaned and thermally disinfected before they are
/// packed and sterilized. A cycle passes when its disinfection reaches <see cref="RequiredA0"/> and the trays are inspected
/// clean afterwards. Packs made from its trays name it, so a pack traces back through its sterilizer load to its wash.
/// </summary>
public sealed class WashCycle : AuditableEntity, IFacilityScoped
{
    /// <summary>
    /// The A0 a cycle must reach (ISO 15883-1, annex B): 3000 for surgical instruments, for example 90 °C held for
    /// 5 minutes or 93 °C for about 2.5 minutes.
    /// </summary>
    public const int RequiredA0 = 3000;

    public Guid FacilityId { get; set; }
    public string CycleNumber { get; set; } = string.Empty;
    public string WasherName { get; set; } = string.Empty;
    public string? Program { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The temperature held during thermal disinfection, from the washer's printout.</summary>
    public decimal? DisinfectionTemperatureC { get; set; }

    public decimal? HoldMinutes { get; set; }

    /// <summary>The disinfection the cycle reached, from <see cref="DisinfectionTemperatureC"/> and <see cref="HoldMinutes"/>.</summary>
    public int? A0 { get; set; }

    public bool? InspectedClean { get; set; }
    public WashCycleStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? OperatorName { get; set; }
    public Guid? OperatorUserId { get; set; }
    public string? Notes { get; set; }
    public List<WashCycleTray> Trays { get; set; } = [];

    /// <summary>
    /// A0 for a temperature held for a time: 10^((T - 80) / 10) for each second, as ISO 15883-1 defines it with z = 10 °C.
    /// </summary>
    public static int A0For(decimal temperatureC, decimal holdMinutes) =>
        (int)Math.Floor(Math.Pow(10, (double)(temperatureC - 80m) / 10) * (double)(holdMinutes * 60m));

    /// <summary>
    /// Records the washer's printout and the inspection of the trays. A cycle that does not reach
    /// <see cref="RequiredA0"/>, or whose trays are not clean, fails; the trays are washed again in a new cycle.
    /// </summary>
    public Result Complete(decimal temperatureC, decimal holdMinutes, bool inspectedClean, DateTimeOffset now)
    {
        if (Status != WashCycleStatus.Running)
        {
            return Error.Rule("cssd.wash_not_running", "Only a running wash cycle can be completed.");
        }

        DisinfectionTemperatureC = temperatureC;
        HoldMinutes = holdMinutes;
        A0 = A0For(temperatureC, holdMinutes);
        InspectedClean = inspectedClean;
        CompletedAt = now;
        if (A0 < RequiredA0)
        {
            Fail($"Disinfection reached A0 {A0}, below the {RequiredA0} required (for example 90 °C held for 5 minutes).");
        }
        else if (!inspectedClean)
        {
            Fail("The trays were not clean on inspection.");
        }
        else
        {
            Status = WashCycleStatus.Passed;
        }

        return Result.Success();
    }

    /// <summary>The operator stops a cycle before it ends (a washer fault, a power cut): its trays are washed again.</summary>
    public Result Abort(string reason, DateTimeOffset now)
    {
        if (Status != WashCycleStatus.Running)
        {
            return Error.Rule("cssd.wash_closed", "Only a running wash cycle can be stopped.");
        }

        CompletedAt = now;
        Fail(reason.Trim());
        return Result.Success();
    }

    /// <summary>
    /// Whether <paramref name="quantity"/> trays of the set may be packed from this cycle: it passed, washed that set, and
    /// washed that many trays of it beyond the <paramref name="alreadyPacked"/> already packed (each washed tray makes one
    /// pack). The error says why not.
    /// </summary>
    public Result CanPack(Guid instrumentSetId, string setName, int quantity = 1, int alreadyPacked = 0)
    {
        if (Status != WashCycleStatus.Passed)
        {
            return Error.Rule("cssd.wash_not_passed", Status == WashCycleStatus.Running
                ? $"Wash cycle {CycleNumber} has not finished. Complete it before packing its trays."
                : $"Wash cycle {CycleNumber} failed. Wash its trays again before packing them.");
        }

        var washed = Trays.Where(t => t.InstrumentSetId == instrumentSetId).Sum(t => t.Quantity);
        if (washed == 0)
        {
            return Error.Rule("cssd.wash_set_mismatch", $"{setName} was not washed in cycle {CycleNumber}.");
        }

        var left = Math.Max(washed - alreadyPacked, 0);
        if (quantity <= left)
        {
            return Result.Success();
        }

        return Error.Rule("cssd.wash_trays_used", left == 0
            ? $"The {TrayCount(washed)} of {setName} washed in cycle {CycleNumber} {(washed == 1 ? "is" : "are")} already packed. Wash any others in a new cycle."
            : alreadyPacked == 0
                ? $"Wash cycle {CycleNumber} washed {TrayCount(washed)} of {setName}: pack at most {left} from it."
                : $"Wash cycle {CycleNumber} washed {TrayCount(washed)} of {setName}, and {alreadyPacked} {(alreadyPacked == 1 ? "is" : "are")} already packed: pack at most {left} more from it.");
    }

    private static string TrayCount(int count) => count == 1 ? "1 tray" : $"{count} trays";

    private void Fail(string reason)
    {
        Status = WashCycleStatus.Failed;
        FailureReason = reason;
    }
}

/// <summary>Trays of one instrument set washed in a cycle.</summary>
public sealed class WashCycleTray : Entity
{
    public Guid WashCycleId { get; set; }
    public Guid InstrumentSetId { get; set; }
    public int Quantity { get; set; }
}
