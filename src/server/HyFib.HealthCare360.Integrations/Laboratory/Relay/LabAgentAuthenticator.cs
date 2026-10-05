using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Integrations.Laboratory.Relay;

/// <summary>
/// Checks the credential a lab agent presents on every call: its device id (<c>X-Device-Id</c>) and the secret it was
/// given at enrolment (<c>X-Device-Key</c>). The secret is stored only as a SHA-256 hash and compared in constant time,
/// also when the device does not exist, so the answer takes as long either way. Only an enrolled, unrevoked device of
/// kind LabAgent is accepted. Failures are logged with the device id, never the key.
/// </summary>
internal sealed class LabAgentAuthenticator(HospitalDbContext db, ILogger<LabAgentAuthenticator> logger)
{
    private static readonly string NoDevice = SecretTokens.Hash("no such device");

    public async Task<Device?> AuthenticateAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var key = http.Request.Headers[LabAgentProtocol.DeviceKeyHeader].ToString();
        if (!Guid.TryParse(http.Request.Headers[LabAgentProtocol.DeviceIdHeader].ToString(), out var deviceId) || key.Length is 0 or > 256)
        {
            logger.LogWarning("A lab agent call without a device credential was refused");
            return null;
        }

        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        var matches = SecretTokens.FixedTimeEquals(device?.SecretHash ?? NoDevice, SecretTokens.Hash(key));
        if (device is null || !matches || device.IsRevoked || device.Kind != DeviceKind.LabAgent)
        {
            logger.LogWarning("Lab agent {DeviceId} was refused: not enrolled as a lab agent, revoked, or a wrong key", deviceId);
            return null;
        }

        return device;
    }
}
