namespace HyFib.HealthCare360.Integrations.Irp;

/// <summary>
/// The Invoice Registration Portal connection (<c>integrations.irp</c>), per facility: the NIC e-invoice API v1.03 itself
/// (<c>nic</c>), a GST suvidha provider passing the same API through (<c>gsp</c>), or the training <c>simulator</c>.
/// </summary>
internal sealed record IrpSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    public string? Provider { get; init; }

    /// <summary>The API's address: NIC's production or sandbox host, or the GSP's.</summary>
    public string BaseUrl { get; init; } = "https://einv-apisandbox.nic.in";

    public string AuthPath { get; init; } = "/eivital/v1.04/auth";

    public string InvoicePath { get; init; } = "/eicore/v1.03/Invoice";

    public string EwayBillPath { get; init; } = "/eiewb/v1.03/ewaybill";

    /// <summary>
    /// Whether requests and answers carry NIC's encryption (the app key under NIC's public key, the payloads under the
    /// session key). True for <c>nic</c>; most GSPs take plain JSON, and it is false for <c>gsp</c> unless set.
    /// </summary>
    public bool? Encrypt { get; init; }

    public bool Encrypts => Encrypt ?? string.Equals(Provider?.Trim(), ProviderKeys.Nic, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The portal credentials (<c>integrations.irp.secret</c>): the API client id and secret NIC or the GSP issued, the API
/// user created on the e-invoice portal for the GSTIN, and, for NIC, its public key (PEM) that encrypts the app key.
/// </summary>
internal sealed record IrpSecrets
{
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? UserName { get; init; }

    public string? Password { get; init; }

    public string? PublicKeyPem { get; init; }
}
