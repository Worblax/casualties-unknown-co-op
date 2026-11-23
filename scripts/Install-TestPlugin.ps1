param(
    [string]$GameDirectory = 'N:\Steam\steamapps\common\Casualties Unknown',
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$plugin = Join-Path $projectRoot 'src/CasualtiesCoop/bin/Release/netstandard2.1/CasualtiesCoop.dll'
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$loader = Join-Path $game 'BepInEx/core/BepInEx.dll'
$plugins = Join-Path $game 'BepInEx/plugins'

if (!(Test-Path -LiteralPath $loader -PathType Leaf) -or !(Test-Path -LiteralPath $plugins -PathType Container)) {
    throw 'The selected game installation has no initialized BepInEx loader.'
}
if (!(Test-Path -LiteralPath $plugin -PathType Leaf)) {
    throw 'Build the Release plugin before installing it.'
}
if (Get-Process -Name CasualtiesUnknown -ErrorAction SilentlyContinue) {
    throw 'Close Casualties Unknown before replacing its test plugin.'
}

$destination = Join-Path $plugins 'CasualtiesCoop.dll'
Copy-Item -LiteralPath $plugin -Destination $destination
Get-FileHash -LiteralPath $destination

if ($Launch) {
    Start-Process 'steam://rungameid/4576490' -WindowStyle Hidden
}
