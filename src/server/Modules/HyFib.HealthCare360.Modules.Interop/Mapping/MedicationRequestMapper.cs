using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Signed e-prescription lines → FHIR <c>MedicationRequest</c> (one per medicine; the prescription number is the
/// <c>groupIdentifier</c>). Drafts are never shared, and neither are prescriptions cancelled before signing. A signed
/// line is <c>active</c> during its course and <c>completed</c> after it; a signed prescription cancelled later is
/// <c>cancelled</c>. Indian frequencies (OD, BD, TDS, QID, HS, SOS, STAT, weekly) become timing codes and repeats.
/// </summary>
internal static class MedicationRequestMapper
{
    public static R4.MedicationRequest Map(PrescriptionItemRow i, ReferenceDirectory refs, DateOnly today)
    {
        var request = new R4.MedicationRequest
        {
            Id = FhirIds.Of(i.Id),
            Meta = FhirElements.Meta(FhirElements.Latest(i.CreatedAt, i.UpdatedAt, i.PrescriptionUpdatedAt), null),
            Status = Status(i, today),
            StatusReason = i.Status == PrescriptionStatus.Cancelled && FhirElements.Clean(i.CancelReason) is { } reason ? FhirElements.Text(reason) : null,
            Intent = R4.MedicationRequest.MedicationRequestIntent.Order,
            Category = [Category(i.IsInpatient)],
            Medication = FhirElements.Text(MedicationText(i)),
            Subject = FhirReferences.Patient(i.PatientId),
            Encounter = FhirReferences.Context(i.EncounterId, i.VisitId, i.AdmissionId, refs),
            AuthoredOn = FhirTime.DateTime(i.SignedAt ?? i.PrescribedAt),
            Requester = refs.Practitioner(i.PractitionerId),
            GroupIdentifier = new R4.Identifier(FhirSystems.PrescriptionNumber, i.PrescriptionNumber),
            DosageInstruction = [Dosage(i)],
            DispenseRequest = new R4.MedicationRequest.DispenseRequestComponent
            {
                Quantity = i.Quantity > 0 ? new R4.Quantity { Value = FhirElements.Normalize(i.Quantity) } : null,
                NumberOfRepeatsAllowed = i.RefillsAllowed,
                ExpectedSupplyDuration = i.DurationDays > 0 ? Days(i.DurationDays) : null,
            },
        };

        return request;
    }

    /// <summary>
    /// Cancelled after signing → cancelled. Otherwise active until the last day of the course (the signing day is day 1,
    /// dates in IST), completed afterwards.
    /// </summary>
    public static R4.MedicationRequest.MedicationrequestStatus Status(PrescriptionItemRow i, DateOnly today)
    {
        if (i.Status == PrescriptionStatus.Cancelled)
        {
            return R4.MedicationRequest.MedicationrequestStatus.Cancelled;
        }

        var start = DateOnly.FromDateTime((i.SignedAt ?? i.PrescribedAt).ToOffset(FhirTime.Ist).DateTime);
        var lastDay = start.AddDays(Math.Max(i.DurationDays, 1) - 1);
        return today > lastDay ? R4.MedicationRequest.MedicationrequestStatus.Completed : R4.MedicationRequest.MedicationrequestStatus.Active;
    }

    public static R4.CodeableConcept Category(bool inpatient) => inpatient
        ? FhirElements.Concept(FhirSystems.MedicationRequestCategory, "inpatient", "Inpatient")
        : FhirElements.Concept(FhirSystems.MedicationRequestCategory, "outpatient", "Outpatient");

    /// <summary>
    /// "Dolo 650 Tablet (Paracetamol)": the brand, then the strength and form unless the name already says them (a brand
    /// such as "Dolo 650" carries its strength), then the generic.
    /// </summary>
    public static string MedicationText(PrescriptionItemRow i)
    {
        var text = FhirElements.Clean(i.DrugName) ?? "Medicine";
        foreach (var part in new[] { i.Strength, i.DosageForm })
        {
            if (FhirElements.Clean(part) is { } value && !Mentions(text, value))
            {
                text += " " + value;
            }
        }

        if (FhirElements.Clean(i.GenericName) is { } generic && !text.Contains(generic, StringComparison.OrdinalIgnoreCase))
        {
            text += $" ({generic})";
        }

        return text;
    }

    /// <summary>The name contains the part, or the part's leading number as a word ("650 mg" in "Dolo 650").</summary>
    private static bool Mentions(string name, string part)
    {
        if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var number = new string(part.TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        return number.Length > 0
               && name.Split([' ', '-', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Contains(number, StringComparer.Ordinal);
    }

    public static R4.Dosage Dosage(PrescriptionItemRow i)
    {
        var (frequency, _) = FrequencyLabels.For(i.Frequency, i.CustomFrequency);
        var parts = new List<string> { FhirElements.Clean(i.Dose) ?? string.Empty, frequency.ToLowerInvariant() };
        if (i.DurationDays > 0 && i.Frequency != DoseFrequency.STAT)
        {
            parts.Add(i.DurationDays == 1 ? "for 1 day" : $"for {i.DurationDays} days");
        }

        var text = string.Join(", ", parts.Where(p => p.Length > 0));
        if (FhirElements.Clean(i.Instructions) is { } instructions)
        {
            text += ". " + instructions;
        }

        var dosage = new R4.Dosage
        {
            Text = text,
            PatientInstruction = FhirElements.Clean(i.Instructions),
            Timing = Timing(i.Frequency, i.DurationDays),
            AsNeeded = i.Frequency == DoseFrequency.SOS ? new R4.FhirBoolean(true) : null,
            Route = FhirElements.Clean(i.Route) is { } route ? FhirElements.Text(route) : null,
        };
        return dosage;
    }

    /// <summary>OD/BD/TDS/QID/HS/weekly as v3 timing abbreviations with explicit repeats; STAT is a single dose.</summary>
    public static R4.Timing? Timing(DoseFrequency frequency, int durationDays)
    {
        var (code, perDay) = frequency switch
        {
            DoseFrequency.OD => ("QD", 1),
            DoseFrequency.BD => ("BID", 2),
            DoseFrequency.TDS => ("TID", 3),
            DoseFrequency.QID => ("QID", 4),
            DoseFrequency.HS => ("BED", 1),
            _ => ((string?)null, 0),
        };

        var repeat = new R4.Timing.RepeatComponent();
        switch (frequency)
        {
            case DoseFrequency.STAT:
                repeat.Count = 1;
                break;
            case DoseFrequency.Weekly:
                code = "WK";
                repeat.Frequency = 1;
                repeat.Period = 1;
                repeat.PeriodUnit = R4.Timing.UnitsOfTime.Wk;
                break;
            default:
                if (perDay > 0)
                {
                    repeat.Frequency = perDay;
                    repeat.Period = 1;
                    repeat.PeriodUnit = R4.Timing.UnitsOfTime.D;
                }

                break;
        }

        if (frequency == DoseFrequency.HS)
        {
            repeat.When = [R4.Timing.EventTiming.HS];
        }

        if (durationDays > 0 && frequency != DoseFrequency.STAT)
        {
            repeat.Bounds = Days(durationDays);
        }

        var hasRepeat = repeat.Count is not null || repeat.Frequency is not null || repeat.Bounds is not null;
        if (code is null && !hasRepeat)
        {
            return null;
        }

        return new R4.Timing
        {
            Code = code is null ? null : FhirElements.Concept(FhirSystems.TimingAbbreviation, code, code),
            Repeat = hasRepeat ? repeat : null,
        };
    }

    private static R4.Duration Days(int days) =>
        new() { Value = days, Unit = days == 1 ? "day" : "days", System = FhirSystems.Ucum, Code = "d" };
}
