[CmdletBinding(DefaultParameterSetName = 'Single')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Single')][string]$Scenario,
    [Parameter(Mandatory, ParameterSetName = 'All')][switch]$All,
    [Parameter(Mandatory, ParameterSetName = 'Catalog')][switch]$Catalog,
    [Parameter(ParameterSetName = 'Catalog')][string[]]$ProbeItem = @(),
    [Parameter(ParameterSetName = 'Catalog')][string[]]$ProbeMob = @(),
    [Nullable[int]]$Seed,
    [string]$GameDirectory = 'N:\Steam\steamapps\common\Casualties Unknown',
    [string]$DotnetPath,
    [string]$GameManagedDir,
    [string]$BepInExCoreDir,
    [ValidateRange(10, 3600)][int]$TimeoutSeconds = 90,
    [switch]$Visible,
    [switch]$HoldOnFailure,
    [ValidateRange(0, 3600)][int]$HoldOnFailureSeconds = 15,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$exe = Join-Path $game 'CasualtiesUnknown.exe'
$plugins = Join-Path $game 'BepInEx/plugins'
$loader = Join-Path $game 'BepInEx/core/BepInEx.dll'
$gameAssembly = Join-Path $game 'CasualtiesUnknown_Data/Managed/Assembly-CSharp.dll'
if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or !(Test-Path -LiteralPath $loader -PathType Leaf) -or
    !(Test-Path -LiteralPath $plugins -PathType Container)) {
    throw 'Use an initialized BepInEx installation of the Steam game.'
}
if (!$GameManagedDir) { $GameManagedDir = Split-Path -Parent $gameAssembly }
if (!$BepInExCoreDir) { $BepInExCoreDir = Split-Path -Parent $loader }
if (!$DotnetPath) { $DotnetPath = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source }
$referenceAssembly = Join-Path $GameManagedDir 'Assembly-CSharp.dll'
$gameHash = (Get-FileHash -LiteralPath $gameAssembly).Hash
if ((Get-FileHash -LiteralPath $referenceAssembly).Hash -ne $gameHash) { throw 'Game and build reference hashes differ.' }

$output = Join-Path $root 'src/CasualtiesCoop.Testing/bin/Release/netstandard2.1'
$project = Join-Path $root 'src/CasualtiesCoop.Testing/CasualtiesCoop.Testing.csproj'
$buildCache = Join-Path $root 'obj/game-test-build.json'
$files = @('CasualtiesCoop.dll', 'CasualtiesCoop.Core.dll', 'CasualtiesCoop.Protocol.dll',
    'CasualtiesCoop.TestKit.dll', 'CasualtiesCoop.Testing.dll')
if (!$NoBuild) {
    & $DotnetPath build $project -c Release "-p:GameManagedDir=$GameManagedDir" "-p:BepInExCoreDir=$BepInExCoreDir"
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the game test plugin.' }
}

$hashes = [ordered]@{ 'Assembly-CSharp.dll' = $gameHash; 'BepInEx.dll' = (Get-FileHash -LiteralPath $loader).Hash }
foreach ($file in $files) {
    $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $output $file)).Hash
}
if ($NoBuild) {
    $cached = Get-Content -Raw -LiteralPath $buildCache | ConvertFrom-Json
    foreach ($name in $hashes.Keys) {
        if ($cached.$name -ne $hashes[$name]) { throw "Cached game test build hash mismatch for $name." }
    }
} else {
    New-Item -ItemType Directory -Path (Split-Path -Parent $buildCache) -Force | Out-Null
    $hashes | ConvertTo-Json | Set-Content -LiteralPath $buildCache -Encoding UTF8
}

$scenarioFiles = if ($All) {
    @(Get-ChildItem -LiteralPath (Join-Path $root 'tests/scenarios') -File -Recurse -Filter '*.json' |
        Where-Object { $_.Directory.Name -notin @('controls', 'blocked') -and ($Visible -or $_.Directory.Name -ne 'visible') } | Sort-Object Name | Select-Object -ExpandProperty FullName)
} elseif ($Catalog) {
    @('<catalog>')
} else {
    @((Resolve-Path -LiteralPath $Scenario).Path)
}
if (!$scenarioFiles.Count) { throw 'No test scenarios were selected.' }
if ($ProbeItem.Count + $ProbeMob.Count -gt 24) { throw 'At most 24 explicit catalog probes can be requested.' }

function Write-Json($Path, $Value) {
    $temp = "$Path.tmp"
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $Path
}

function Find-TestProcess([string]$Manifest, [string]$Executable) {
    $processes = Get-CimInstance Win32_Process -Filter "Name = 'CasualtiesUnknown.exe'" -ErrorAction SilentlyContinue
    $match = $processes | Where-Object { $_.ExecutablePath -eq $Executable -and
        $_.CommandLine -and $_.CommandLine.Contains($Manifest) } | Select-Object -First 1
    if ($match) { Get-Process -Id $match.ProcessId -ErrorAction SilentlyContinue }
}

function Invoke-Scenario([string]$File) {
    $runId = [guid]::NewGuid().ToString('N')
    $run = Join-Path $root "artifacts/game-tests/$runId"
    $backup = Join-Path $run 'backup'
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $manifest = Join-Path $run 'request.json'
    $resultPath = Join-Path $run 'result.json'
    $startedPath = Join-Path $run 'started.json'
    $saved = @{}
    $installed = @()
    $owned = $null
    $launchAttempted = $false
    $status = 'FAIL'
    $failure = $null

    try {
        $targetScenario = Join-Path $run 'scenario.json'
        if ($Catalog) {
            Write-Json $targetScenario ([ordered]@{
                schemaVersion = 1; name = 'catalog.capture'; level = 'UNITY_ACTORS'; seed = 42; timeoutMs = 60000
                network = [ordered]@{}
                setup = @(@{ op = 'world.loadSandbox' }, @{ op = 'peer.add'; id = 'host'; role = 'host' })
                steps = @(@{ op = 'catalog.capture' })
            })
        } else {
            Copy-Item -LiteralPath $File -Destination $targetScenario
        }
        Write-Json $manifest ([ordered]@{
            runId = $runId; visible = [bool]$Visible; holdOnFailure = [bool]$HoldOnFailure
            seed = $Seed; buildHashes = $hashes; catalogOnly = [bool]$Catalog
            holdSeconds = $HoldOnFailureSeconds; probeItem = @($ProbeItem); probeMob = @($ProbeMob)
        })

        if (Get-Process -Name CasualtiesUnknown -ErrorAction SilentlyContinue) {
            $status = 'BLOCKED'
            throw 'Another Casualties Unknown process is open. It was left running.'
        }
        $steam = Get-Process -Name steam -ErrorAction SilentlyContinue | Select-Object -First 1
        if (!$steam -or !$steam.Path) {
            $status = 'BLOCKED'
            throw 'The signed-in Steam client must be running.'
        }
        foreach ($testModule in 'CasualtiesCoop.Testing.dll', 'CasualtiesCoop.TestKit.dll') {
            if (Test-Path -LiteralPath (Join-Path $plugins $testModule)) {
                $status = 'BLOCKED'
                throw "A pre-existing test module $testModule is installed. The existing plugin files were preserved."
            }
        }
        foreach ($module in $files) {
            $destination = Join-Path $plugins $module
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                $saved[$module] = Join-Path $backup $module
                Copy-Item -LiteralPath $destination -Destination $saved[$module]
            }
            $installed += $module
            Copy-Item -LiteralPath (Join-Path $output $module) -Destination $destination
            if ((Get-FileHash -LiteralPath $destination).Hash -ne $hashes[$module]) { throw "Installed hash mismatch for $module." }
        }

        $launchAttempted = $true
        Start-Process -FilePath $steam.Path -ArgumentList @('-applaunch', '4576490', '--coop-test', ('"{0}"' -f $manifest)) -WindowStyle Hidden
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if (!$owned -and (Test-Path -LiteralPath $startedPath)) {
                $started = Get-Content -Raw -LiteralPath $startedPath | ConvertFrom-Json
                if ($started.runId -ne $runId) { throw 'Conflicting startup report.' }
                $owned = Get-Process -Id $started.pid -ErrorAction SilentlyContinue
                if (!$owned -or $owned.Path -ne $exe) { throw 'Test reported an unexpected or exited process.' }
                $null = $owned.Handle
            }
            if (Test-Path -LiteralPath $resultPath) { break }
            if ($owned -and $owned.HasExited) { throw 'Test player exited without a result.' }
            Start-Sleep -Milliseconds 200
        }
        if (!(Test-Path -LiteralPath $resultPath)) {
            throw "Emergency watchdog expired after $TimeoutSeconds seconds without a result."
        }

        $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
        if ($result.runId -ne $runId -or $result.status -notin @('PASS', 'FAIL', 'BLOCKED')) { throw 'Invalid or stale test report.' }
        if (!$owned) { throw 'The result arrived without an owned test process.' }
        if ($result.status -ne 'PASS' -and $HoldOnFailure -and $Visible -and $HoldOnFailureSeconds -eq 0) {
            Write-Host "Observation remains open. Use End observation in the game window. Report: $resultPath"
            while (!$owned.WaitForExit(1000)) { }
        } else {
            $exitWait = if ($result.status -ne 'PASS' -and $HoldOnFailure -and $Visible) { $HoldOnFailureSeconds + 4 } else { 4 }
            if (!$owned.WaitForExit($exitWait * 1000)) { throw 'The test player did not exit after reporting.' }
        }
        if ($result.status -eq 'PASS' -and ($owned.ExitCode -ne 0 -or !$result.assertions.Count)) {
            throw "PASS requires assertions and a zero player exit code. Exit code $($owned.ExitCode)."
        }
        if ($result.status -ne 'PASS' -and $owned.ExitCode -eq 0) { throw 'A failed or blocked player returned zero.' }
        $status = $result.status
    } catch {
        $failure = $_.Exception.Message
    } finally {
        if ($launchAttempted -and !$owned) { $owned = Find-TestProcess $manifest $exe }
        if ($owned -and !$owned.HasExited -and $owned.Path -eq $exe) {
            $null = $owned.CloseMainWindow()
            if (!$owned.WaitForExit(2500)) {
                Stop-Process -Id $owned.Id -ErrorAction SilentlyContinue
                $owned.WaitForExit(5000) | Out-Null
            }
        }
        foreach ($module in $installed) {
            try {
                $destination = Join-Path $plugins $module
                if ($saved.ContainsKey($module)) {
                    Copy-Item -LiteralPath $saved[$module] -Destination $destination
                    if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $saved[$module]).Hash) {
                        throw "Failed to verify restored $module."
                    }
                } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
                    Remove-Item -LiteralPath $destination
                }
            } catch {
                $status = 'FAIL'
                $failure = "$failure Cleanup for ${module}: $($_.Exception.Message)"
            }
        }
        if ($launchAttempted) {
            $bepLog = Join-Path $game 'BepInEx/LogOutput.log'
            if (Test-Path -LiteralPath $bepLog) {
                Copy-Item -LiteralPath $bepLog -Destination (Join-Path $run 'bepinex.log') -ErrorAction Continue
            }
        }
    }
    if ($failure) {
        if (Test-Path -LiteralPath $resultPath) {
            Move-Item -LiteralPath $resultPath -Destination (Join-Path $run 'player-result.json')
        }
        Write-Json $resultPath ([ordered]@{ runId = $runId; scenario = [IO.Path]::GetFileNameWithoutExtension($File)
            status = $status; failure = $failure; buildHashes = $hashes; assertions = @() })
    }
    "[$status] $File -> $resultPath" | Write-Host
    return ($status -eq 'PASS')
}

$failed = $false
foreach ($file in $scenarioFiles) {
    if (!(Invoke-Scenario $file)) { $failed = $true }
}
if ($failed) { exit 1 }
exit 0
