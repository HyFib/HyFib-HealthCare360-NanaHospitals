namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>A haemodialysis machine at a station (DIA-02), dedicated to one sero category.</summary>
public sealed class DialysisMachine : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Chair/bed position in the unit ("Station 3", "Isolation room B").</summary>
    public string? Station { get; set; }

    public SeroStatus SeroDedication { get; set; }
    public DialysisMachineStatus Status { get; set; }
    public DateTimeOffset? LastDisinfectedAt { get; set; }
    public string? LastDisinfectionMethod { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>A machine must be disinfected between patients; this is true after every session until that is recorded.</summary>
    public bool NeedsDisinfection => LastUsedAt is { } used && (LastDisinfectedAt is not { } cleaned || cleaned < used);

    /// <summary>Everything that must hold before a patient is connected: right sero category, in service, disinfected.</summary>
    public Result EnsureReadyFor(SeroStatus patientStatus)
    {
        var sero = SeroSegregation.EnsureCompatible(patientStatus, SeroDedication, Code);
        if (sero.IsFailure)
        {
            return sero;
        }

        if (!IsActive || Status is DialysisMachineStatus.Maintenance or DialysisMachineStatus.Retired)
        {
            return Error.Rule("dialysis.machine_unavailable", $"Machine {Code} is out of service.");
        }

        if (Status == DialysisMachineStatus.InUse)
        {
            return Error.Rule("dialysis.machine_busy", $"Machine {Code} is running another session.");
        }

        return NeedsDisinfection
            ? Error.Rule("dialysis.machine_not_disinfected", $"Record the disinfection of machine {Code} before connecting the next patient.")
            : Result.Success();
    }

    public void MarkInUse() => Status = DialysisMachineStatus.InUse;

    public void Release(DateTimeOffset now)
    {
        LastUsedAt = now;
        if (Status == DialysisMachineStatus.InUse)
        {
            Status = DialysisMachineStatus.Available;
        }
    }

    public Result RecordDisinfection(string method, DateTimeOffset now)
    {
        if (Status == DialysisMachineStatus.InUse)
        {
            return Error.Rule("dialysis.machine_busy", $"Machine {Code} is running a session; disinfect it after the session ends.");
        }

        LastDisinfectedAt = now;
        LastDisinfectionMethod = method.Trim();
        return Result.Success();
    }
}
