param(
    [string]$GameDirectory = 'N:\Steam\steamapps\common\Casualties Unknown',
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = Join-Path $projectRoot 'src/CasualtiesCoop/bin/Release/netstandard2.1'
$assemblies = @('CasualtiesCoop.Protocol.dll', 'CasualtiesCoop.Core.dll', 'CasualtiesCoop.dll')
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$loader = Join-Path $game 'BepInEx/core/BepInEx.dll'
$plugins = Join-Path $game 'BepInEx/plugins'

if (!(Test-Path -LiteralPath $loader -PathType Leaf) -or !(Test-Path -LiteralPath $plugins -PathType Container)) {
    throw 'The selected game installation has no initialized BepInEx loader.'
}
foreach ($assembly in $assemblies) {
    if (!(Test-Path -LiteralPath (Join-Path $release $assembly) -PathType Leaf)) {
        throw "Missing $assembly. Build the Release plugin before installing it."
    }
}
if (Get-Process -Name CasualtiesUnknown -ErrorAction SilentlyContinue) {
    throw 'Close Casualties Unknown before replacing its test plugin.'
}

foreach ($assembly in $assemblies) {
    $destination = Join-Path $plugins $assembly
    Copy-Item -LiteralPath (Join-Path $release $assembly) -Destination $destination
    Get-FileHash -LiteralPath $destination
}

if ($Launch) {
    Start-Process 'steam://rungameid/4576490' -WindowStyle Hidden
}
