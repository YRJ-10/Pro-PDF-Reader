[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $root 'artifacts'
$portableArchive = Join-Path $artifactDirectory "ProPdfReader-$Version-win-x64-portable.zip"
$installer = Join-Path $artifactDirectory "installer\ProPdfReader-$Version-win-x64-setup.exe"
$checksumPath = Join-Path $artifactDirectory 'SHA256SUMS.txt'

foreach ($path in @($portableArchive, $installer, $checksumPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing release artifact: $path"
    }
}

$expectedHashes = @{}
foreach ($line in Get-Content -LiteralPath $checksumPath) {
    if ($line -notmatch '^([A-Fa-f0-9]{64}) \*(.+)$') {
        throw "Invalid checksum line: $line"
    }

    $expectedHashes[$Matches[2]] = $Matches[1].ToUpperInvariant()
}

foreach ($path in @($portableArchive, $installer)) {
    $file = Get-Item -LiteralPath $path
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ($expectedHashes[$file.Name] -ne $actualHash) {
        throw "Checksum mismatch: $($file.Name)"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($portableArchive)
try {
    $requiredEntries = @(
        'ProPdfReader.exe',
        'Register portable copy.cmd',
        'Remove portable registration.cmd',
        'README-portable.txt'
    )

    foreach ($entryName in $requiredEntries) {
        if (-not ($zip.Entries | Where-Object FullName -eq $entryName)) {
            throw "Portable ZIP is missing $entryName."
        }
    }
}
finally {
    $zip.Dispose()
}

$publishedExecutable = Join-Path $artifactDirectory 'publish\win-x64\ProPdfReader.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable)) {
    throw "Published executable is missing: $publishedExecutable"
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable)
if ($versionInfo.ProductName -ne 'Pro PDF Reader' -or $versionInfo.ProductVersion -ne $Version) {
    throw "Unexpected executable metadata: $($versionInfo.ProductName) $($versionInfo.ProductVersion)"
}

$signature = Get-AuthenticodeSignature -LiteralPath $installer
[pscustomobject]@{
    Version = $Version
    PortableMB = [math]::Round((Get-Item $portableArchive).Length / 1MB, 1)
    InstallerMB = [math]::Round((Get-Item $installer).Length / 1MB, 1)
    Checksums = 'Valid'
    PortableContents = 'Valid'
    InstallerSignature = $signature.Status
} | Format-List

if ($signature.Status -ne 'Valid') {
    Write-Warning 'The installer is not code-signed. Sign public release artifacts before distribution.'
}
