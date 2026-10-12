namespace HyFib.HealthCare360.WindowsInterop.Updates;

/// <summary>
/// The release signing keys HC360 trusts (SRS DED-18): HC360 Desktop installs a package by itself, and the Server Node
/// offers one to workstations, only when its manifest is signed by one of these. They are built into the programs, so
/// nothing on a workstation or the server can add one.
/// </summary>
/// <remarks>
/// HyFib makes its key pair once with <c>tools/release/Sign-HC360Release.ps1 -NewKey</c>, stores the private key as the
/// <c>HC360_RELEASE_SIGNING_KEY</c> secret of the release workflow, and adds the public key here (docs/security/SECURITY.md,
/// "Release signing key"). To rotate it, add the new key next to the old one and release that version signed with the old
/// key; sign the releases after it with the new key, and remove the old one a release or two later, once every
/// workstation has a version that trusts the new one. Until a key is added here, no package is ever installed by itself:
/// workstations are upgraded with Install-HC360Desktop.ps1, as before.
/// </remarks>
public static class ReleaseKeys
{
    public static IReadOnlyList<ReleaseKey> Trusted { get; } =
    [
        // ReleaseKey.FromPem("""
        //     -----BEGIN PUBLIC KEY-----
        //     ...
        //     -----END PUBLIC KEY-----
        //     """),
    ];
}
