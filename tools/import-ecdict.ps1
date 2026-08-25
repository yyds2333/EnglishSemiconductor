[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CsvPath,

    [Parameter(Mandatory = $true)]
    [string]$DatabasePath,

    [Parameter(Mandatory = $true)]
    [string]$ProviderVersion
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$csvPathFull = [System.IO.Path]::GetFullPath($CsvPath, (Get-Location).Path)
$databasePathFull = [System.IO.Path]::GetFullPath($DatabasePath, (Get-Location).Path)

if (-not (Test-Path -LiteralPath $csvPathFull -PathType Leaf)) {
    throw "ECDICT CSV file does not exist: $csvPathFull"
}

dotnet run --project (Join-Path $repoRoot 'tools\WordPin.DictionaryImport\WordPin.DictionaryImport.csproj') `
    --configuration Release `
    --no-restore `
    -- --csv $csvPathFull --database $databasePathFull --version $ProviderVersion
