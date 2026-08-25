[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildHome = Join-Path $repoRoot '.build-home'
$appDataRoot = Join-Path $repoRoot '.build-appdata'
$localAppDataRoot = Join-Path $repoRoot '.build-localappdata'
$packagesRoot = Join-Path $repoRoot '.nuget-packages'
$nugetConfig = Join-Path $repoRoot 'NuGet.Config'

$env:DOTNET_CLI_HOME = $buildHome
$env:APPDATA = $appDataRoot
$env:LOCALAPPDATA = $localAppDataRoot
$env:NUGET_PACKAGES = $packagesRoot
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet restore (Join-Path $repoRoot 'WordPin.slnx') `
    --runtime win-x64 `
    --configfile $nugetConfig `
    --packages $packagesRoot

dotnet build (Join-Path $repoRoot 'WordPin.slnx') `
    --configuration Release `
    --no-restore

if ($Test) {
    dotnet test (Join-Path $repoRoot 'tests\WordPin.Infrastructure.Tests\WordPin.Infrastructure.Tests.csproj') `
        --configuration Release `
        --no-restore `
        --logger "console;verbosity=normal"
}

if ($Publish) {
    $publishRoot = Join-Path $repoRoot 'artifacts\publish\win-x64'
    dotnet publish (Join-Path $repoRoot 'src\WordPin.App\WordPin.App.csproj') `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $publishRoot

    Write-Host "Published: $publishRoot"
}
