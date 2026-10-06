<#
.SYNOPSIS
    Builds the release package: self-contained publish of the WPF client and the background
    service, then the NSIS installer that packages them.

.DESCRIPTION
    Both projects publish self-contained (-r win-x64 --self-contained true) into the shared
    .\publish directory that installer.nsi expects. Self-contained is required because the
    target machine (BIGHYPERV and every other rollout target) is not guaranteed to have a
    .NET 9 runtime installed, and this product must not depend on someone installing one first
    — the published output carries its own CLR.

    Both projects publish into the SAME output directory on purpose: installer.nsi's
    File "publish\*.exe" / "publish\*.dll" wildcards expect one flat directory holding both
    the client's and the service's files together (see the RuntimeIdentifier comment in each
    .csproj for why a flat, unambiguous layout matters here).

.PARAMETER Configuration
    Build configuration to publish. Defaults to Release.

.PARAMETER SkipInstaller
    Publishes the applications but does not invoke makensis. Useful when only testing the
    published output (e.g. running the .exe directly) without producing a setup package.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$publishDir = Join-Path $PSScriptRoot "publish"
$rid = "win-x64"

Write-Host "=== Cleaning previous publish output ($publishDir) ===" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

function Publish-Project([string]$ProjectPath, [string]$Name) {
    Write-Host "=== Publishing $Name ($Configuration, $rid, self-contained) ===" -ForegroundColor Cyan
    & dotnet publish $ProjectPath `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Name (exit code $LASTEXITCODE)"
    }
}

# The WPF client and the service publish into the same flat directory; the second publish adds
# its own files alongside the first's without disturbing them (different assembly names).
Publish-Project "TouchMappingAgent.WPF\TouchMappingAgent.WPF.csproj" "TouchMappingAgent.WPF"
Publish-Project "TouchMappingAgent.Service\TouchMappingAgent.Service.csproj" "TouchMappingAgent.Service"

$exeCount = (Get-ChildItem -Path $publishDir -Filter "*.exe").Count
$dllCount = (Get-ChildItem -Path $publishDir -Filter "*.dll").Count
$satelliteOk = Test-Path (Join-Path $publishDir "en\TouchMappingAgent.WPF.resources.dll")
Write-Host ("Publish output: {0} .exe, {1} .dll, en\ satellite present: {2}" -f $exeCount, $dllCount, $satelliteOk) -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "SkipInstaller set - done." -ForegroundColor Yellow
    return
}

Write-Host "=== Determining product version ===" -ForegroundColor Cyan
# Directory.Build.props' VersionPrefix is the single source of truth: it already drives the
# AssemblyVersion/FileVersion baked into every .exe/.dll just published above. Reading it here
# and passing it into makensis keeps the installer's own file-version resource and its
# Add/Remove Programs entry from silently drifting out of sync with what's actually inside the
# package — see installer.nsi's PRODUCT_VERSION comment for the other half of this.
[xml]$buildProps = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props")
$versionPrefix = $buildProps.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
if (-not $versionPrefix) {
    throw "Could not read VersionPrefix from Directory.Build.props"
}
# NSIS's VIProductVersion/VIFileVersion require a strict 4-part X.Y.Z.W numeric version; pad the
# 3-part semantic VersionPrefix with a trailing .0.
$nsisVersion = "$versionPrefix.0"
Write-Host "Product version: $versionPrefix (installer file version: $nsisVersion)" -ForegroundColor Green

Write-Host "=== Building the NSIS installer ===" -ForegroundColor Cyan
$makensis = "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
if (-not (Test-Path $makensis)) {
    $makensis = "$env:ProgramFiles\NSIS\makensis.exe"
}
if (-not (Test-Path $makensis)) {
    throw "makensis.exe not found under Program Files (x86)\NSIS or Program Files\NSIS. Install NSIS or adjust `$makensis in this script."
}

& $makensis "/DPRODUCT_VERSION=$nsisVersion" /WX installer.nsi
if ($LASTEXITCODE -ne 0) {
    throw "makensis failed (exit code $LASTEXITCODE)"
}

$setupExe = Join-Path $PSScriptRoot "TouchMappingAgent-Setup.exe"
Write-Host ("=== Done: {0} ({1:N1} MB) ===" -f $setupExe, ((Get-Item $setupExe).Length / 1MB)) -ForegroundColor Green
