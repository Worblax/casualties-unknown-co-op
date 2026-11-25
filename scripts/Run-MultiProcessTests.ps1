param(
    [string]$Scenario = 'tests/multiprocess/pose-bidirectional.json',
    [string]$GameDirectory = 'N:\Steam\steamapps\common\Casualties Unknown',
    [string]$DotnetPath,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180,
    [ValidateRange(0, 1000)][int]$LatencyMs = 40,
    [ValidateRange(0, 1000)][int]$JitterMs = 5,
    [ValidateRange(0.0, 1.0)][double]$UnreliableLoss = 0.02,
    [switch]$NoBuild,
    [switch]$KeepInstalls
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$exe = Join-Path $game 'CasualtiesUnknown.exe'
$plugins = Join-Path $game 'BepInEx/plugins'
$loader = Join-Path $game 'BepInEx/core/BepInEx.dll'
if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath $loader) -or
    !(Test-Path -LiteralPath $plugins -PathType Container)) {
    throw 'The game directory must contain an initialized BepInEx installation.'
}
if (!$DotnetPath) { $DotnetPath = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source }
$project = Join-Path $root 'src/CasualtiesCoop.Testing/CasualtiesCoop.Testing.csproj'
$output = Join-Path $root 'src/CasualtiesCoop.Testing/bin/Release/netstandard2.1'
$files = @('CasualtiesCoop.dll', 'CasualtiesCoop.Core.dll', 'CasualtiesCoop.Protocol.dll',
    'CasualtiesCoop.TestKit.dll', 'CasualtiesCoop.Testing.dll')
if (!$NoBuild) {
    & $DotnetPath build $project -c Release "-p:GameManagedDir=$(Join-Path $game 'CasualtiesUnknown_Data/Managed')" `
        "-p:BepInExCoreDir=$(Join-Path $game 'BepInEx/core')" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the test plugin.' }
}
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $output $file))) { throw "Missing $file." }
}
if (Get-Process -Name CasualtiesUnknown -ErrorAction SilentlyContinue) {
    throw 'Close all Casualties Unknown processes before starting the multiprocess test.'
}

$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $root "artifacts/multiprocess/$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$scenarioPath = if ([IO.Path]::IsPathRooted($Scenario)) { $Scenario } else { Join-Path $root $Scenario }
$runScenario = Join-Path $runRoot 'scenario.json'
Copy-Item -LiteralPath $scenarioPath -Destination $runScenario
$seed = (Get-Content -Raw -Encoding UTF8 -LiteralPath $runScenario | ConvertFrom-Json).seed
$hashes = [ordered]@{
    'Assembly-CSharp.dll' = (Get-FileHash -LiteralPath (Join-Path $game 'CasualtiesUnknown_Data/Managed/Assembly-CSharp.dll')).Hash
    'BepInEx.dll' = (Get-FileHash -LiteralPath $loader).Hash
    'scenario.json' = (Get-FileHash -LiteralPath $runScenario).Hash
}
foreach ($file in $files) { $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $output $file)).Hash }
$session = [guid]::NewGuid()
$hostId = [uint64]76561198000000001
$guestId = [uint64]76561198000000002
$tcpPort = 45100 + (Get-Random -Minimum 0 -Maximum 800)
$hostUdp = $tcpPort + 1
$guestUdp = $tcpPort + 2
$copies = @()
$started = @()
try {
    $roles = @(
        @{ Name = 'host'; Id = $hostId; Character = 0; Udp = $hostUdp },
        @{ Name = 'guest'; Id = $guestId; Character = 1; Udp = $guestUdp }
    )
    $runs = @()
    foreach ($role in $roles) {
        $copy = Join-Path $runRoot ("game-" + $role.Name)
        $copies += $copy
        & robocopy $game $copy /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Could not copy the game directory for $($role.Name)." }
        foreach ($file in $files) {
            $destination = Join-Path $copy "BepInEx/plugins/$file"
            Copy-Item -LiteralPath (Join-Path $output $file) -Destination $destination -Force
            if ((Get-FileHash -LiteralPath $destination).Hash -ne $hashes[$file]) {
                throw "Installed build hash mismatch for $file."
            }
        }
        $processRunId = [guid]::NewGuid().ToString('N')
        $run = Join-Path $runRoot $processRunId
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        Copy-Item -LiteralPath $runScenario -Destination (Join-Path $run 'scenario.json')
        $data = Join-Path $run 'data'
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $manifest = Join-Path $run 'request.json'
        $peers = @(
            [ordered]@{ id = $hostId; character = 0; udpPort = $hostUdp },
            [ordered]@{ id = $guestId; character = 1; udpPort = $guestUdp }
        )
        [ordered]@{
            runId = $processRunId; visible = $false; holdOnFailure = $false; seed = $seed
            buildHashes = $hashes; catalogOnly = $false; holdSeconds = 15; probeItem = @(); probeMob = @()
            role = $role.Name; dataRoot = $data; sessionId = $session.ToString()
            hostId = $hostId; localId = $role.Id; character = $role.Character
            transport = [ordered]@{ host = '127.0.0.1'; tcpPort = $tcpPort; localUdpPort = $role.Udp
                latencyMs = $LatencyMs; jitterMs = $JitterMs; unreliableLoss = $UnreliableLoss }
            peers = $peers
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifest -Encoding UTF8
        $process = Start-Process -FilePath (Join-Path $copy 'CasualtiesUnknown.exe') -ArgumentList @(
            '--coop-mp', ('"{0}"' -f $manifest), '-batchmode', '-nographics', '-logFile',
            ('"{0}"' -f (Join-Path $run 'player.log'))) -WindowStyle Hidden -PassThru
        $started += $process
        $runs += [pscustomobject]@{ Name = $role.Name; RunId = $processRunId; Run = $run; Process = $process }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds -and
        ($runs | Where-Object { !(Test-Path -LiteralPath (Join-Path $_.Run 'result.json')) })) {
        if ($runs | Where-Object { $_.Process.HasExited -and !(Test-Path -LiteralPath (Join-Path $_.Run 'result.json')) }) {
            break
        }
        Start-Sleep -Milliseconds 500
    }
    $summary = foreach ($run in $runs) {
        $resultPath = Join-Path $run.Run 'result.json'
        $result = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } else { $null }
        $valid = $result -and $result.runId -eq $run.RunId -and $result.role -eq $run.Name -and $result.pid -eq $run.Process.Id
        [pscustomobject]@{
            Role = $run.Name
            Status = if ($valid) { $result.status } elseif ($result) { 'INVALID_RESULT' } else { 'NO_RESULT' }
            Pid = if ($result) { $result.pid } else { $null }
            Result = $resultPath
            Failure = if ($result) { $result.failure } else { $null }
        }
    }
    $summary | Format-Table -AutoSize
    if ($summary.Where({ $_.Status -ne 'PASS' }).Count -gt 0) { throw 'Multiprocess test failed.' }
    "PASS: $runRoot"
}
finally {
    foreach ($process in $started) {
        if ($process.WaitForExit(1000)) { continue }
        try {
            $null = $process.CloseMainWindow()
            if ($process.WaitForExit(3000)) { continue }
            $process.Kill()
            if (!$process.WaitForExit(3000)) { throw "Test process $($process.Id) did not exit." }
        } catch {
            if (!$process.HasExited) { throw }
        }
    }
    if (!$KeepInstalls) {
        foreach ($copy in $copies) {
            $resolved = [IO.Path]::GetFullPath($copy)
            $rootPath = [IO.Path]::GetFullPath($runRoot) + [IO.Path]::DirectorySeparatorChar
            if (!$resolved.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to remove a path outside the multiprocess run.'
            }
            if (Test-Path -LiteralPath $resolved) { [IO.Directory]::Delete($resolved, $true) }
        }
    }
}
