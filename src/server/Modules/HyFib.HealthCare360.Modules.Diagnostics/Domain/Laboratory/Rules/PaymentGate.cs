namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// A test checked before collection: its charge source reference, the name shown to staff, and whether it is a
/// health-checkup package component (paid under the package, so it has no per-test charge to check).
/// </summary>
public sealed record PaymentLine(string Reference, string Name, bool CoveredByPackage = false);

/// <summary>
/// Collection payment gate (pure; unit-tested). With the setting <c>lab.requirePaymentBeforeCollection</c> on (the default),
/// out-patients and external patients must have paid before blood is drawn, which prevents revenue leakage at OP
/// counters. In-patient, emergency and day-care work is billed to the running account and is never held up, and
/// package components are settled with the package by Billing.
/// </summary>
internal static class PaymentGate
{
    public static Result Check(
        PatientClass patientClass, bool paymentRequired, IReadOnlyCollection<PaymentLine> lines, IReadOnlyDictionary<string, ChargeBillingState> states)
    {
        if (!paymentRequired || !patientClass.PaysBeforeService())
        {
            return Result.Success();
        }

        var unpaid = lines
            .Where(line => !line.CoveredByPackage)
            .Where(line => !states.TryGetValue(line.Reference, out var state) || state != ChargeBillingState.Paid)
            .ToList();
        if (unpaid.Count == 0)
        {
            return Result.Success();
        }

        return Error.Rule(
            "lab.payment_pending",
            $"Payment is pending for {string.Join(", ", unpaid.Select(u => u.Name))}. Collect payment at the billing counter before drawing the sample.")
            with
            {
                Data = new
                {
                    tests = unpaid.Select(u => new
                    {
                        reference = u.Reference,
                        name = u.Name,
                        state = (states.TryGetValue(u.Reference, out var s) ? s : ChargeBillingState.NotFound).ToString(),
                    }).ToList(),
                },
            };
    }
}
