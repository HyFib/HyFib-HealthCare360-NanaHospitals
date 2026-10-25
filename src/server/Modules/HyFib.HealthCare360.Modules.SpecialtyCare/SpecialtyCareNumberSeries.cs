using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SpecialtyCare;

/// <summary>The numbers the Specialty care module issues (administrators set their formats under Hospital setup).</summary>
public static class SpecialtyCareNumberSeries
{
    public static NumberSeriesDefinition BloodRequest { get; } =
        new("blood-request", "Blood request", "Blood bank", "{FAC}BR{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>One number per donor for the whole hospital chain, so it is shared by every facility.</summary>
    public static NumberSeriesDefinition BloodDonor { get; } =
        new("blood-donor", "Blood donor", "Blood bank", "{FAC}DN{SEQ:6}", NumberResetPolicy.Never, MaxLength: 20) { PerFacility = false };

    /// <summary>Bag numbers; each component made from the bag is labelled with the bag number and a letter.</summary>
    public static NumberSeriesDefinition BloodBag { get; } =
        new("blood-bag", "Blood bag", "Blood bank", "{FAC}BB{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition SurgeryBooking { get; } =
        new("ot-booking", "Surgery booking", "Operation theatre", "{FAC}OT{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition DialysisSession { get; } =
        new("dialysis-session", "Dialysis session", "Dialysis", "{FAC}DL{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>The serial number of the MTP admission register (Form III), from 1 each calendar year.</summary>
    public static NumberSeriesDefinition MtpRegister { get; } =
        new("mtp-register", "MTP admission register", "Termination of pregnancy", "{FAC}MTP{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [BloodRequest, BloodDonor, BloodBag, SurgeryBooking, DialysisSession, MtpRegister];
}
