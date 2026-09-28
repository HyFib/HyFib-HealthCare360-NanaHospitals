namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

public enum TheatreType
{
    Major = 0,
    Minor,
    Cardiac,
    Ortho,
    Emergency,
}

/// <summary>An operating room (OT-01). Bookings refer to it; inactive theatres keep their history but take no new cases.</summary>
public sealed class OperationTheatre : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>Short code shown on the OT board ("OT-1").</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public TheatreType Type { get; set; }

    /// <summary>Major equipment available in the room (C-arm, laminar flow, laparoscopy tower…), used when choosing a theatre.</summary>
    public List<string> Equipment { get; set; } = [];

    public string? Location { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
