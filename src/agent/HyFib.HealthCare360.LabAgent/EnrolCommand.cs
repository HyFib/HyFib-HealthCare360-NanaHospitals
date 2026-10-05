using System.Security.Cryptography;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// <c>HC360LabAgent.exe enrol --server https://HC360-SRV:7360/ --code ABCD-EFGH --ca-fingerprint "AB12 …" [--name "Lab PC 1"]</c>,
/// run by Install-HC360LabAgent.ps1 as an administrator.
/// <list type="number">
/// <item>Downloads the server's CA certificate and refuses it unless its fingerprint equals the one IT gave (printed by
/// the server installer). There is no person at a service to confirm a fingerprint, so it is always required.</item>
/// <item>Checks the server's own certificate chains to that CA and names the address given.</item>
/// <item>Exchanges the one-time code (Administration › Devices, device type "Lab analyser agent") for a device id and
/// secret, and saves them with the secret protected for this PC.</item>
/// </list>
/// </summary>
internal static class EnrolCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var server = options.GetValueOrDefault("server");
        var code = new string((options.GetValueOrDefault("code") ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var expected = ServerTrust.NormalizeFingerprint(options.GetValueOrDefault("ca-fingerprint"));
        var name = (options.GetValueOrDefault("name") ?? $"{Environment.MachineName} lab agent").Trim();
        var path = options.GetValueOrDefault("config") ?? AgentConfiguration.DefaultPath;

        if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            return Fail(error, "Give the server's address with --server, for example https://HC360-SRV:7360/");
        }

        if (code.Length != 8)
        {
            return Fail(error, "Give the 8-character enrolment code with --code, for example ABCD-EFGH. Create one in Administration › Devices.");
        }

        if (expected is null)
        {
            return Fail(error, "Give the hospital CA fingerprint with --ca-fingerprint (64 hexadecimal characters). The server installer prints it.");
        }

        if (name.Length is 0 or > 100)
        {
            return Fail(error, "Give this PC a name of up to 100 characters with --name, for example \"Haematology PC\".");
        }

        var baseUri = new Uri(serverUri.GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            var (pem, fingerprint) = await ServerApi.FetchCaCertificateAsync(baseUri, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(fingerprint), System.Text.Encoding.ASCII.GetBytes(expected)))
            {
                return Fail(error,
                    "The server's CA fingerprint does not match the one given, so the connection may be intercepted. Nothing was saved. Check the address and the fingerprint with IT.");
            }

            using var trust = new ServerTrust(pem);
            using var api = new ServerApi(baseUri, trust);
            var info = await api.GetSystemInfoAsync(cancellationToken);
            if (info is null || !string.Equals(info.CaFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(error, "The server's certificate does not belong to that CA. Nothing was saved.");
            }

            var enrolled = await api.EnrolAsync(code, name, cancellationToken);
            if (enrolled.CaFingerprint is not null && !string.Equals(enrolled.CaFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(error, "The server's certificate changed during enrolment. Run the installer again.");
            }

            var configuration = new AgentConfiguration
            {
                ServerUrl = baseUri.ToString(),
                CaCertificatePem = pem,
                CaFingerprint = fingerprint,
                DeviceId = enrolled.DeviceId,
                DeviceName = name,
                DeviceSecret = enrolled.DeviceSecret,
            };
            configuration.Save(path);
            await output.WriteLineAsync($"Enrolled as \"{name}\" with {info.ServerName} ({info.FacilityName ?? "HC360"}). The configuration is in {path}.");
            await output.WriteLineAsync("Next, in HC360: Lab setup › Analysers › Connection, choose Serial, this agent and the COM port.");
            return 0;
        }
        catch (AgentProblemException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Fail(error, $"Enrolment failed: {ex.Message}");
        }
    }

    /// <summary><c>--name value</c> pairs (case-insensitive names).</summary>
    internal static Dictionary<string, string> Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                options[args[i][2..]] = args[++i];
            }
        }

        return options;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 1;
    }
}
