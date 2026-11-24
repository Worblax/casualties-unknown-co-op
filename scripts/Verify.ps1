[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$GameManagedDir,
    [string]$BepInExCoreDir,
    [switch]$ChecksOnly,
    [switch]$GameTests,
    [string]$Scenario,
    [string]$GameDirectory,
    [switch]$Visible
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if (!$DotnetPath) {
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $DotnetPath = if ($dotnet) { $dotnet.Source } else { Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
}

function Invoke-Check([string]$Name, [string[]]$Arguments) {
    Write-Host $Name
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

if (!$ChecksOnly) {
    $build = @('build', (Join-Path $projectRoot 'src/CasualtiesCoop/CasualtiesCoop.csproj'), '-c', 'Release')
    if ($GameManagedDir) {
        $build += "-p:GameManagedDir=$GameManagedDir"
    }
    if ($BepInExCoreDir) {
        $build += "-p:BepInExCoreDir=$BepInExCoreDir"
    }
    Invoke-Check 'Release build' $build
}

Invoke-Check 'Protocol checks' @('run', '--project',
    (Join-Path $projectRoot 'tests/CasualtiesCoop.ProtocolChecks/CasualtiesCoop.ProtocolChecks.csproj'), '-c', 'Release')

$architecture = @('run', '--project',
    (Join-Path $projectRoot 'tests/CasualtiesCoop.ArchitectureChecks/CasualtiesCoop.ArchitectureChecks.csproj'),
    '-c', 'Release', '--', $projectRoot, $DotnetPath)
if (!$ChecksOnly) {
    $architecture += Join-Path $projectRoot 'src/CasualtiesCoop/bin/Release/netstandard2.1/CasualtiesCoop.dll'
}
Invoke-Check 'Architecture checks' $architecture

Invoke-Check 'Virtual network checks' @('run', '--project',
    (Join-Path $projectRoot 'tests/CasualtiesCoop.NetworkChecks/CasualtiesCoop.NetworkChecks.csproj'), '-c', 'Release')

if ($GameTests) {
    $parameters = if ($Scenario) { @{ Scenario = $Scenario } } else { @{ All = $true } }
    $parameters['DotnetPath'] = $DotnetPath
    if ($GameDirectory) { $parameters['GameDirectory'] = $GameDirectory }
    if ($GameManagedDir) { $parameters['GameManagedDir'] = $GameManagedDir }
    if ($BepInExCoreDir) { $parameters['BepInExCoreDir'] = $BepInExCoreDir }
    if ($Visible) { $parameters['Visible'] = $true }
    & (Join-Path $PSScriptRoot 'Run-GameTests.ps1') @parameters
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host 'Automated verification passed.'
