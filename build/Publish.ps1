[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'ProPdfReader\ProPdfReader.csproj'
$publishDirectory = Join-Path $root 'artifacts\publish\win-x64'
$installerDirectory = Join-Path $root 'artifacts\installer'
$portableArchive = Join-Path $root "artifacts\ProPdfReader-$Version-win-x64-portable.zip"
$installerScript = Join-Path $root 'build\ProPdfReader.iss'
$distributionDirectory = Join-Path $root 'distribution'

function Reset-Directory([string]$Path) {
    $fullRoot = [IO.Path]::GetFullPath($root)
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath | Out-Null
}

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path $root 'artifacts\tools\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )

    return $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

Reset-Directory $publishDirectory
Reset-Directory $installerDirectory

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishProfile=Windows-x64 `
    -p:Version=$Version `
    -p:PublishDir=$publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not $SkipInstaller) {
    $compiler = Find-InnoCompiler
    if (-not $compiler) {
        throw 'Inno Setup 6 or 7 is required to build the installer. Run again with -SkipInstaller to build only the portable ZIP.'
    }

    & $compiler "/DMyAppVersion=$Version" "/DSourceDir=$publishDirectory" "/DOutputDir=$installerDirectory" $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
}

Copy-Item -LiteralPath (Join-Path $distributionDirectory 'Register portable copy.cmd') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $distributionDirectory 'Remove portable registration.cmd') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $distributionDirectory 'README-portable.txt') -Destination $publishDirectory

if (Test-Path -LiteralPath $portableArchive) {
    Remove-Item -LiteralPath $portableArchive -Force
}

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

Write-Host ''
Write-Host 'Build outputs:'
Write-Host "  Portable:  $portableArchive"
if (-not $SkipInstaller) {
    Get-ChildItem -LiteralPath $installerDirectory -Filter '*.exe' | ForEach-Object {
        Write-Host "  Installer: $($_.FullName)"
    }
}
