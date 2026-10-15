# Native Windows packaging for local builds and GitHub Actions.
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$Version = '0.7.5'
)
$ErrorActionPreference = 'Stop'
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
    throw 'Version must be semantic X.Y.Z, optionally with a prerelease suffix.'
}
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $sdk = 'dotnet'
    if (Test-Path '.tools/dotnet/dotnet.exe') { $sdk = '.tools/dotnet/dotnet.exe' }
    $publishDir = Join-Path $projectRoot "artifacts/publish/$Runtime"
    $destination = Join-Path $projectRoot 'artifacts/windows'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $sdk publish src/TraceScope.App/TraceScope.App.csproj -c Release -r $Runtime --self-contained true "-p:Version=$Version" -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=None -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    python scripts/licensing/generate.py --publish-dir $publishDir --rid $Runtime
    if ($LASTEXITCODE -ne 0) { throw "Third-party notice generation failed." }
    $executable = Join-Path $publishDir 'pngr.exe'
    $reportedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
    if ($reportedVersion -ne $Version) { throw "Application reports '$reportedVersion', expected '$Version'." }
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    New-Item $destination -ItemType Directory -Force | Out-Null
    Copy-Item "$publishDir/*" $destination -Recurse
    $archive = Join-Path $projectRoot "artifacts/pngr-$Runtime.zip"
    $temporaryArchive = Join-Path $projectRoot "artifacts/pngr-$Runtime.pending.zip"
    try {
        Compress-Archive -Path "$publishDir/*" -DestinationPath $temporaryArchive -CompressionLevel Optimal -Force
        Move-Item $temporaryArchive $archive -Force
    } finally {
        if (Test-Path $temporaryArchive) { Remove-Item $temporaryArchive -Force }
    }
    Write-Host "Built $destination ($Version)"
} finally {
    Pop-Location
}
