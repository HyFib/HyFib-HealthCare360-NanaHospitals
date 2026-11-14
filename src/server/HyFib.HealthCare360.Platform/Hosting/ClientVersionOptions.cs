namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>
/// The oldest HC360 Desktop and lab agent the hospital still supports (<c>HealthCare360:Clients</c>). A server update can
/// leave a workstation or an analyser PC on the old program for weeks, because the update is offered, not forced
/// (SRS DED-18). A device below its minimum is listed in System health and is told to update in the answer to its next
/// heartbeat (<see cref="Networking.OutdatedClients"/>); it is never refused, so no one is locked out mid-shift.
/// </summary>
public sealed class ClientVersionOptions
{
    /// <summary>
    /// The oldest HC360 Desktop (workstations, kiosks, queue displays and tablets), such as <c>1.0.0</c>. Unset: the server's
    /// release (its major and minor version), so a 1.1 server asks for a 1.1 desktop and a 1.0.3 desktop still passes a
    /// 1.0.9 server.
    /// </summary>
    public string? MinimumDesktopVersion { get; set; }

    /// <summary>The oldest lab agent. Unset: the server's release, as <see cref="MinimumDesktopVersion"/>.</summary>
    public string? MinimumLabAgentVersion { get; set; }
}
