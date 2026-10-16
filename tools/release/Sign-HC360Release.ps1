#Requires -Version 5.1
<#
.SYNOPSIS
    Signs the HC360 Desktop package of a release, so that HC360 Desktop can update itself from the Server Node (SRS DED-18).

.DESCRIPTION
    The release workflow runs it after Publish-HC360.ps1. Next to HC360-Desktop-<version>-<runtime>.zip it writes:
      HC360-Desktop-<version>-<runtime>.manifest.json  the product, version, file name, size and SHA-256 of the package,
                                                       when it was released, and the oldest server it works with
      HC360-Desktop-<version>-<runtime>.manifest.sig   an ECDSA P-256 / SHA-256 signature of the manifest's bytes with
                                                       HyFib's release signing key, and that key's id
    First it checks that the HC360Desktop.exe in the package is the version its name says.

    The key is read from the HC360_RELEASE_SIGNING_KEY environment variable, never from a parameter, so it appears on no
    command line and in no log: a PKCS#8 private key in PEM form, the release workflow's secret. Without it
    the package is not signed: the script says so, in the job summary too, and succeeds, so the release is still built.
    HC360 Desktop never installs an unsigned package by itself.

    -NewKey makes HyFib's key pair, once. The private key goes to -KeyPath, a new file only the current user can read: put
    it in the HC360_RELEASE_SIGNING_KEY secret, keep a copy offline, and delete the file. The public key is printed, with
    its id, for src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs.

    Windows only (Windows CNG handles the keys), in Windows PowerShell 5.1 and PowerShell 7. Exit code 1 when it fails.

.EXAMPLE
    # The release workflow: the key comes from its secret.
    .\tools\release\Sign-HC360Release.ps1 -ArtifactsDir .\artifacts

.EXAMPLE
    # Once, on a computer HyFib trusts with the key: makes the key pair.
    .\tools\release\Sign-HC360Release.ps1 -NewKey -KeyPath E:\hc360-release-signing.key
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    # The release's packages; by default the repository's artifacts folder, where Publish-HC360.ps1 puts them.
    [Parameter(ParameterSetName = 'Sign')] [string] $ArtifactsDir,
    # Markdown for the job summary, and name=value outputs for the steps after (signed, keyId, package).
    [Parameter(ParameterSetName = 'Sign')] [string] $SummaryPath = $env:GITHUB_STEP_SUMMARY,
    [Parameter(ParameterSetName = 'Sign')] [string] $OutputPath = $env:GITHUB_OUTPUT,
    # The oldest server the package works with. By default the release's major and minor version, patch 0: a patch
    # release of HC360 Desktop works with every server of its minor version.
    [Parameter(ParameterSetName = 'Sign')] [string] $MinimumServerVersion,
    [Parameter(ParameterSetName = 'NewKey', Mandatory)] [switch] $NewKey,
    [Parameter(ParameterSetName = 'NewKey', Mandatory)] [string] $KeyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$KeyVariable = 'HC360_RELEASE_SIGNING_KEY'
# The PEM label of a PKCS#8 private key, put together here so the script itself never looks like a key to a secret scan.
$PrivateKeyLabel = 'PRIVATE' + ' KEY'
$Product = 'HC360 Desktop'
$Algorithm = 'ECDSA-P256-SHA256'
# A P-256 public key's SubjectPublicKeyInfo up to its point: id-ecPublicKey, prime256v1, an uncompressed point (04 X Y).
$SpkiPrefix = [byte[]] @(0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE,
    0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
$PackagePattern = '^HC360-Desktop-(?<version>\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?)-(?<runtime>win-[a-z0-9]+)\.zip$'
$Utf8 = New-Object System.Text.UTF8Encoding $false

function Write-Utf8File([string] $Path, [string] $Text) { [System.IO.File]::WriteAllText($Path, $Text, $Utf8) }

function ConvertTo-HexString([byte[]] $Bytes) { -join ($Bytes | ForEach-Object { $_.ToString('x2') }) }

function Get-Sha256([byte[]] $Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return , $sha.ComputeHash($Bytes) } finally { $sha.Dispose() }
}

# The key's SubjectPublicKeyInfo (DER), as HC360 reads a release key (ReleaseKey.FromSubjectPublicKeyInfo).
function Get-SubjectPublicKeyInfo([System.Security.Cryptography.ECDsa] $Key) {
    $point = $Key.ExportParameters($false).Q
    if ($Key.KeySize -ne 256 -or $point.X.Length -ne 32 -or $point.Y.Length -ne 32) { throw 'The release signing key must be an ECDSA key on the P-256 curve.' }
    return , [byte[]] ($SpkiPrefix + $point.X + $point.Y)
}

# A key's id: the first 8 bytes of the SHA-256 of its SubjectPublicKeyInfo, in lower-case hex (ReleaseKey.Id).
function Get-KeyId([byte[]] $SubjectPublicKeyInfo) { ConvertTo-HexString ((Get-Sha256 $SubjectPublicKeyInfo)[0..7]) }

function ConvertTo-Pem([string] $Label, [byte[]] $Der) {
    $base64 = [Convert]::ToBase64String($Der)
    $lines = for ($i = 0; $i -lt $base64.Length; $i += 64) { $base64.Substring($i, [Math]::Min(64, $base64.Length - $i)) }
    return (@("-----BEGIN $Label-----") + $lines + @("-----END $Label-----")) -join "`n"
}

# The private key from a PKCS#8 PEM. Its bytes are cleared once Windows has the key.
function Read-SigningKey([string] $Pem) {
    $match = [regex]::Match($Pem, "-----BEGIN $PrivateKeyLabel-----(?<body>[A-Za-z0-9+/=\s]+)-----END $PrivateKeyLabel-----")
    if (-not $match.Success) { throw "$KeyVariable is not a PKCS#8 private key PEM ($PrivateKeyLabel)." }
    $der = [Convert]::FromBase64String(($match.Groups['body'].Value -replace '\s', ''))
    try {
        $cng = [System.Security.Cryptography.CngKey]::Import($der, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    }
    catch {
        throw "$KeyVariable could not be read as an ECDSA P-256 private key."
    }
    finally {
        [Array]::Clear($der, 0, $der.Length)
    }
    $key = New-Object System.Security.Cryptography.ECDsaCng $cng
    $null = Get-SubjectPublicKeyInfo $key
    return $key
}

function Write-Summary([string[]] $Lines) {
    if ($SummaryPath) { [System.IO.File]::AppendAllText($SummaryPath, (($Lines -join "`n") + "`n"), $Utf8) }
}

function Write-Outputs([hashtable] $Values) {
    if ($OutputPath) { [System.IO.File]::AppendAllText($OutputPath, ((($Values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "`n") + "`n"), $Utf8) }
}

# The version HC360Desktop.exe in the package reports (its product version, without build metadata).
function Get-PackagedVersion([string] $Package) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Package)
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-sign-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        $entry = $zip.GetEntry('HC360Desktop.exe')
        if (-not $entry) { throw "$(Split-Path -Leaf $Package) has no HC360Desktop.exe at its top level." }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $temp)
        return ((Get-Item -LiteralPath $temp).VersionInfo.ProductVersion -split '\+')[0].Trim()
    }
    finally {
        $zip.Dispose()
        Remove-Item -LiteralPath $temp -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- a new key pair (once)
if ($NewKey) {
    if (Test-Path -LiteralPath $KeyPath) { throw "$KeyPath already exists: a key file is never overwritten." }
    $parameters = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    # No name ([NullString], as PowerShell would pass $null as ""): a key that lives only in this process, never one kept
    # in the user's key store.
    $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $parameters)
    if (-not $cng.IsEphemeral) {
        $cng.Delete()
        throw 'Windows kept the new key in its key store; it was deleted. Run -NewKey again.'
    }
    $key = New-Object System.Security.Cryptography.ECDsaCng $cng
    $spki = Get-SubjectPublicKeyInfo $key
    $pkcs8 = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    try {
        # The file is made readable only by the current user before the key is written to it.
        $null = New-Item -ItemType File -Path $KeyPath
        $acl = New-Object System.Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                    [System.Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'Allow')))
        Set-Acl -LiteralPath $KeyPath -AclObject $acl
        Write-Utf8File $KeyPath ((ConvertTo-Pem $PrivateKeyLabel $pkcs8) + "`n")
    }
    finally {
        [Array]::Clear($pkcs8, 0, $pkcs8.Length)
        $key.Dispose()
        $cng.Dispose()
    }
    $publicPem = ConvertTo-Pem 'PUBLIC KEY' $spki
    Write-Host "Release signing key $(Get-KeyId $spki) made. Its private key is in $KeyPath (only you can read it)."
    Write-Host ''
    Write-Host 'Next:'
    Write-Host "  1. Store the private key as the release workflow's secret, then delete the file:"
    Write-Host "       Get-Content -Raw '$KeyPath' | gh secret set $KeyVariable --repo HyFib/HyFib-HealthCare360-NanaHospitals"
    Write-Host '     Keep one copy offline (an encrypted USB drive in the safe, or the company password manager).'
    Write-Host '  2. Add the public key to ReleaseKeys.Trusted in'
    Write-Host '     src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs, and release that version:'
    Write-Host ''
    Write-Host '        ReleaseKey.FromPem("""'
    $publicPem -split "`n" | ForEach-Object { Write-Host "            $_" }
    Write-Host '            """),'
    exit 0
}

# ---------------------------------------------------------------- the release's HC360 Desktop package
# Windows PowerShell has no $PSScriptRoot yet in the parameters of a script run with -File.
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $PSScriptRoot '..\..\artifacts' }
$packages =@(Get-ChildItem -LiteralPath $ArtifactsDir -File | Where-Object { $_.Name -match $PackagePattern })
if ($packages.Count -ne 1) { throw "Expected one HC360-Desktop-<version>-<runtime>.zip in $ArtifactsDir, found $($packages.Count)." }
$package = $packages[0]
$null = $package.Name -match $PackagePattern
$version = $Matches['version']
$packaged = Get-PackagedVersion $package.FullName
if ($packaged -ne $version) { throw "$($package.Name) holds HC360Desktop.exe $packaged, not $version." }

$manifestPath = Join-Path $package.DirectoryName ($package.BaseName + '.manifest.json')
$signaturePath = Join-Path $package.DirectoryName ($package.BaseName + '.manifest.sig')
Remove-Item -LiteralPath $manifestPath, $signaturePath -ErrorAction SilentlyContinue

$pem = [Environment]::GetEnvironmentVariable($KeyVariable)
if ([string]::IsNullOrWhiteSpace($pem)) {
    $message = "$($package.Name) is not signed: the $KeyVariable secret is not set. HC360 Desktop installs only signed packages by itself, so the Server Node will not offer this version to workstations; they are upgraded with Install-HC360Desktop.ps1."
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::warning title=HC360 Desktop not signed::$message" } else { Write-Warning $message }
    Write-Summary @(
        '### HC360 Desktop package NOT signed',
        '',
        "The ``$KeyVariable`` secret is not set, so ``$($package.Name)`` has no signed manifest. HC360 Desktop installs only signed",
        'packages by itself: the Server Node will not offer this version to workstations, and they are upgraded with',
        '`Install-HC360Desktop.ps1` instead. How to set up the key: "Release signing key" in `docs/security/SECURITY.md`.')
    Write-Outputs @{ signed = 'false'; package = $package.FullName }
    exit 0
}

$key = Read-SigningKey $pem
$pem = $null
try {
    $spki = Get-SubjectPublicKeyInfo $key
    $keyId = Get-KeyId $spki
    $numbers = ($version -split '-')[0] -split '\.'
    if (-not $MinimumServerVersion) { $MinimumServerVersion = "$($numbers[0]).$($numbers[1]).0" }

    $stream = [System.IO.File]::OpenRead($package.FullName)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $hash = ConvertTo-HexString $sha.ComputeHash($stream) } finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }

    $manifest = [ordered]@{
        format               = 1
        product              = $Product
        version              = $version
        file                 = $package.Name
        size                 = [long] $package.Length
        sha256               = $hash
        releasedAt           = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
        minimumServerVersion = $MinimumServerVersion
    }
    # HC360 checks the signature of exactly these bytes, so the manifest is written once and signed as it is on disk.
    Write-Utf8File $manifestPath (($manifest | ConvertTo-Json) + "`n")
    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    $signature = $key.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    if ($signature.Length -ne 64 -or -not $key.VerifyData($manifestBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
        throw 'The signature just made does not verify.'
    }
    $signatureFile = [ordered]@{ algorithm = $Algorithm; keyId = $keyId; signature = [Convert]::ToBase64String($signature) }
    Write-Utf8File $signaturePath (($signatureFile | ConvertTo-Json) + "`n")
}
finally {
    $key.Dispose()
}

Write-Host "Signed $($package.Name) ($Product $version, $($package.Length) bytes) with release key $keyId."
Write-Summary @(
    '### HC360 Desktop package signed',
    '',
    "``$($package.Name)`` ($Product $version) is signed with release key ``$keyId``: ``$(Split-Path -Leaf $manifestPath)`` and",
    "``$(Split-Path -Leaf $signaturePath)`` are attached to the release. Workstations take it from the Server Node once it has",
    "the package (servers $MinimumServerVersion and later).")
Write-Outputs @{ signed = 'true'; keyId = $keyId; package = $package.FullName }
