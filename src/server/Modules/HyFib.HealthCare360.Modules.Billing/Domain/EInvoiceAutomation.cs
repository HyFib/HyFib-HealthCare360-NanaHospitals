namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>What the outbox does after asking the portal for a document's IRN on HC360's own.</summary>
public enum EInvoiceAutoOutcome
{
    /// <summary>Nothing more: the IRN is recorded, or the document needs none now.</summary>
    Done,

    /// <summary>Asked again later, with the outbox's back-off: the portal did not answer, or the document waits for its invoice's IRN.</summary>
    Retry,

    /// <summary>Not asked again: the document's problems or the portal's refusal stop it, and holders of the register are told.</summary>
    GiveUp,
}

/// <summary>
/// Asking the portal for a document's IRN on HC360's own once the document is final (GST rule 48(4)): what each ending
/// leads to. Only what can change on its own is asked again, the portal answering or the connection being set up; a
/// refusal stays one until someone changes the document, the masters or the connection, and asking again would only spend
/// the portal's calls.
/// </summary>
public static class EInvoiceAutomation
{
    /// <summary>Who an IRN asked for automatically is recorded by.</summary>
    public const string RecordedBy = "HC360 (automatic)";

    /// <summary>What an automatic request leads to, by the code of the error it ended with (null: the IRN was recorded).</summary>
    public static EInvoiceAutoOutcome After(string? errorCode) => errorCode switch
    {
        null or "einvoice.registered" or "einvoice.document_cancelled" or "einvoice.not_applicable" => EInvoiceAutoOutcome.Done,
        "einvoice.irp_unreachable" or "einvoice.irp_not_connected" => EInvoiceAutoOutcome.Retry,
        _ => EInvoiceAutoOutcome.GiveUp,
    };
}
