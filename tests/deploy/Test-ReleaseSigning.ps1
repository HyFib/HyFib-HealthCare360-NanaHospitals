<#
.SYNOPSIS
    Checks the release workflow's signing of the HC360 Desktop package (tools/release/Sign-HC360Release.ps1, SRS DED-18)
    with a throwaway key, in the PowerShell that runs this script. CI runs it in Windows PowerShell 5.1 and in
    PowerShell 7; the release workflow runs the script in PowerShell 7.

.DESCRIPTION
    Makes a key pair with -NewKey, as HyFib does once, then signs a package like the release's (its HC360Desktop.exe is
    a copy of a Windows program, so it has a product version) with the key given as the workflow gives its secret: in
    the environment. Checks the manifest and the signature, that a changed manifest no longer verifies, that the key
    appears in no output, and that without the key the release is built unsigned and says so. Each run of the script is
    a process of its own, as in the workflow. SigningScriptTests (Desktop.Tests) checks that HC360's own code verifies
    what it signs. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$signer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\tools\release\Sign-HC360Release.ps1')).Path
# Put together, so this script never looks like a key to the secret scan.
$PrivateKeyLabel = 'PRIVATE' + ' KEY'
$shell = (Get-Process -Id $PID).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-signing-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root | Out-Null

# Runs the script in a process of its own, with the key (or none) in its environment; returns its exit code and output.
function Invoke-Signer([string[]] $Arguments, [string] $Key) {
    $previous = $env:HC360_RELEASE_SIGNING_KEY
    # What the script writes to standard error is part of its output here: Windows PowerShell would stop at it otherwise.
    $ErrorActionPreference = 'Continue'
    try {
        if ($Key) { $env:HC360_RELEASE_SIGNING_KEY = $Key } else { Remove-Item Env:\HC360_RELEASE_SIGNING_KEY -ErrorAction SilentlyContinue }
        $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $signer @Arguments 2>&1 | ForEach-Object { "$_" } | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        if ($null -ne $previous) { $env:HC360_RELEASE_SIGNING_KEY = $previous } else { Remove-Item Env:\HC360_RELEASE_SIGNING_KEY -ErrorAction SilentlyContinue }
    }
}

# A release's artifacts folder with an HC360 Desktop package whose HC360Desktop.exe is a copy of whoami.exe.
function New-Artifacts([string] $Name, [string] $Version) {
    $folder = Join-Path $root $Name
    $content = Join-Path $folder 'desktop'
    New-Item -ItemType Directory -Force -Path $content | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\whoami.exe') -Destination (Join-Path $content 'HC360Desktop.exe')
    if (-not $Version) { $Version = ((Get-Item -LiteralPath (Join-Path $content 'HC360Desktop.exe')).VersionInfo.ProductVersion -split '\+')[0] }
    $artifacts = Join-Path $folder 'artifacts'
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    Compress-Archive -Path (Join-Path $content '*') -DestinationPath (Join-Path $artifacts "HC360-Desktop-$Version-win-x64.zip")
    return [pscustomobject]@{ Artifacts = $artifacts; Version = $Version; Zip = (Join-Path $artifacts "HC360-Desktop-$Version-win-x64.zip") }
}

function Import-SigningKey([string] $Pem) {
    $body = [regex]::Match($Pem, "-----BEGIN $PrivateKeyLabel-----(?<b>[^-]+)-----END $PrivateKeyLabel-----").Groups['b'].Value -replace '\s', ''
    $cng = [System.Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($body), [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    return New-Object System.Security.Cryptography.ECDsaCng $cng
}

try {
    Write-Host 'HyFib makes its key pair once'
    $keyPath = Join-Path $root 'hc360-release-signing.key'
    $made = Invoke-Signer @('-NewKey', '-KeyPath', $keyPath)
    Assert ($made.ExitCode -eq 0) "-NewKey succeeds (exit code $($made.ExitCode))"
    $pem = Get-Content -LiteralPath $keyPath -Raw
    Assert ($pem -match "^-----BEGIN $PrivateKeyLabel-----") 'the private key is written as a PKCS#8 PEM'
    $acl = Get-Acl -LiteralPath $keyPath
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
    Assert ($acl.AreAccessRulesProtected -and $rules.Count -eq 1 -and $rules[0].IdentityReference -eq $me) 'only the current user may read the key file'
    Assert ($made.Output -match '-----BEGIN PUBLIC KEY-----' -and $made.Output -match 'ReleaseKey\.FromPem') 'the public key is printed, ready for ReleaseKeys.cs'
    $keyBody = ($pem -split "`n")[1].Trim()
    Assert ($made.Output -notmatch [regex]::Escape($keyBody)) 'the private key is not printed'
    $again = Invoke-Signer @('-NewKey', '-KeyPath', $keyPath)
    Assert ($again.ExitCode -ne 0 -and (Get-Content -LiteralPath $keyPath -Raw) -eq $pem) 'an existing key file is never overwritten'

    Write-Host 'The release workflow signs the HC360 Desktop package with it'
    $release = New-Artifacts 'signed'
    $summary = Join-Path $root 'signed-summary.md'
    $outputs = Join-Path $root 'signed-outputs.txt'
    $signed = Invoke-Signer @('-ArtifactsDir', $release.Artifacts, '-SummaryPath', $summary, '-OutputPath', $outputs) -Key $pem
    Assert ($signed.ExitCode -eq 0) "signing succeeds (exit code $($signed.ExitCode)) $($signed.Output)"
    $manifestPath = Join-Path $release.Artifacts "HC360-Desktop-$($release.Version)-win-x64.manifest.json"
    $signaturePath = Join-Path $release.Artifacts "HC360-Desktop-$($release.Version)-win-x64.manifest.sig"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $signature = Get-Content -LiteralPath $signaturePath -Raw | ConvertFrom-Json
    $numbers = $release.Version -split '\.'
    Assert ($manifest.format -eq 1 -and $manifest.product -eq 'HC360 Desktop' -and $manifest.version -eq $release.Version) 'the manifest names the product and version'
    Assert ($manifest.file -eq (Split-Path -Leaf $release.Zip) -and $manifest.size -eq (Get-Item -LiteralPath $release.Zip).Length) 'and the file, with its size'
    Assert ($manifest.sha256 -eq (Get-FileHash -LiteralPath $release.Zip -Algorithm SHA256).Hash.ToLowerInvariant()) 'and its SHA-256'
    Assert ($manifest.minimumServerVersion -eq "$($numbers[0]).$($numbers[1]).0") 'and the oldest server it works with'
    Assert ([System.IO.File]::ReadAllBytes($manifestPath)[0] -eq [byte][char]'{') 'the manifest has no byte order mark'

    $key = Import-SigningKey $pem
    try {
        $bytes = [System.IO.File]::ReadAllBytes($manifestPath)
        $raw = [Convert]::FromBase64String($signature.signature)
        Assert ($signature.algorithm -eq 'ECDSA-P256-SHA256' -and $signature.keyId -match '^[0-9a-f]{16}$') 'the signature names its algorithm and key'
        Assert ($key.VerifyData($bytes, $raw, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) 'the signature verifies with the key'
        $changed = [System.Text.Encoding]::UTF8.GetBytes(([System.Text.Encoding]::UTF8.GetString($bytes)).Replace($release.Version, '99.0.0'))
        Assert (-not $key.VerifyData($changed, $raw, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) 'a changed manifest does not'
    }
    finally { $key.Dispose() }
    Assert ($signed.Output -match [regex]::Escape($signature.keyId)) 'the output names the key it signed with'
    Assert ((Get-Content -LiteralPath $outputs -Raw) -match 'signed=true' -and (Get-Content -LiteralPath $outputs -Raw) -match [regex]::Escape("package=$($release.Zip)")) 'the next steps are told it is signed, and which package'
    Assert ((Get-Content -LiteralPath $summary -Raw) -match 'signed with release key') 'the job summary says it is signed'
    Assert ($signed.Output -notmatch [regex]::Escape($keyBody) -and (Get-Content -LiteralPath $summary -Raw) -notmatch [regex]::Escape($keyBody)) 'the key appears in no output'

    Write-Host 'Without the key the release is built unsigned, and says so'
    $unsignedRelease = New-Artifacts 'unsigned'
    $unsignedSummary = Join-Path $root 'unsigned-summary.md'
    $unsignedOutputs = Join-Path $root 'unsigned-outputs.txt'
    $unsigned = Invoke-Signer @('-ArtifactsDir', $unsignedRelease.Artifacts, '-SummaryPath', $unsignedSummary, '-OutputPath', $unsignedOutputs)
    Assert ($unsigned.ExitCode -eq 0) "the release goes on (exit code $($unsigned.ExitCode))"
    Assert (@(Get-ChildItem -LiteralPath $unsignedRelease.Artifacts -Filter '*.manifest.*').Count -eq 0) 'no manifest or signature is written'
    Assert ((Get-Content -LiteralPath $unsignedSummary -Raw) -match 'NOT signed') 'the job summary says plainly that it is not signed'
    Assert ((Get-Content -LiteralPath $unsignedOutputs -Raw) -match 'signed=false') 'the next steps are told it is not signed'

    Write-Host 'A release that is not right is not signed'
    $wrongVersion = New-Artifacts 'wrong-version' -Version '1.0.0'
    $refused = Invoke-Signer @('-ArtifactsDir', $wrongVersion.Artifacts, '-SummaryPath', (Join-Path $root 'x.md'), '-OutputPath', (Join-Path $root 'x.txt')) -Key $pem
    Assert ($refused.ExitCode -ne 0 -and $refused.Output -match 'not 1\.0\.0') 'a package whose HC360Desktop.exe is another version than its name'
    Assert (@(Get-ChildItem -LiteralPath $wrongVersion.Artifacts -Filter '*.manifest.*').Count -eq 0) 'and nothing is signed for it'
    $notAKey = Invoke-Signer @('-ArtifactsDir', $release.Artifacts, '-SummaryPath', (Join-Path $root 'y.md'), '-OutputPath', (Join-Path $root 'y.txt')) -Key 'not a key'
    Assert ($notAKey.ExitCode -ne 0 -and $notAKey.Output -match 'not a PKCS#8 private key') 'a secret that is not a private key'
    Copy-Item -LiteralPath $release.Zip -Destination (Join-Path $release.Artifacts 'HC360-Desktop-0.9.0-win-x64.zip')
    $two = Invoke-Signer @('-ArtifactsDir', $release.Artifacts, '-SummaryPath', (Join-Path $root 'z.md'), '-OutputPath', (Join-Path $root 'z.txt')) -Key $pem
    Assert ($two.ExitCode -ne 0 -and $two.Output -match 'found 2') 'two HC360 Desktop packages in one release'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
