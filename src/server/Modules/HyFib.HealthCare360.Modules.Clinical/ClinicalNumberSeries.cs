using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Clinical;

/// <summary>The numbers the Clinical module issues (administrators set their formats under Hospital setup).</summary>
public static class ClinicalNumberSeries
{
    /// <summary>Prescriptions and their refills.</summary>
    public static NumberSeriesDefinition Prescription { get; } =
        new("prescription", "Prescription", "Clinical", "{FAC}RX{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>A doctor's order of tests and procedures.</summary>
    public static NumberSeriesDefinition ClinicalOrder { get; } =
        new("clinical-order", "Clinical order", "Clinical", "{FAC}OR{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition RecordRequest { get; } =
        new("mrd-request", "Medical record request", "Medical records", "{FAC}RR{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition HealthCheckupBooking { get; } =
        new("healthcheck-booking", "Health check-up booking", "Health check-ups", "{FAC}HC{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>A corporate drive: one package booked for many employees on one date.</summary>
    public static NumberSeriesDefinition CorporateCheckupBatch { get; } =
        new("healthcheck-batch", "Corporate check-up drive", "Health check-ups", "{FAC}HCB{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>The hospital's register of births, live and still, from 1 each calendar year.</summary>
    public static NumberSeriesDefinition BirthRegister { get; } =
        new("birth-register", "Birth register", "Medical records", "{FAC}BRN{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>The hospital's register of deaths, from 1 each calendar year.</summary>
    public static NumberSeriesDefinition DeathRegister { get; } =
        new("death-register", "Death register", "Medical records", "{FAC}DTH{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>The hospital's medico-legal case register, from 1 each calendar year.</summary>
    public static NumberSeriesDefinition MlcRegister { get; } =
        new("mlc-register", "Medico-legal case register", "Emergency", "{FAC}MLC{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } =
        [Prescription, ClinicalOrder, RecordRequest, HealthCheckupBooking, CorporateCheckupBatch, BirthRegister, DeathRegister, MlcRegister];
}
