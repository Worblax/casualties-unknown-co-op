param(
    [ValidateSet('Capture', 'Click')]
    [string]$Action = 'Capture',

    [ValidateRange(0.0, 1.0)]
    [double]$X = 0.5,

    [ValidateRange(0.0, 1.0)]
    [double]$Y = 0.5
)

$PerformCoopClick = $Action -eq 'Click'
$CoopClickX = $X
$CoopClickY = $Y
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CoopWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr context, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint id);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@
$game = Get-Process -Name CasualtiesUnknown -ErrorAction Stop | Select-Object -First 1
if ($game.Path -ne 'N:\Steam\steamapps\common\Casualties Unknown\CasualtiesUnknown.exe' -or $game.MainWindowHandle -eq 0) { throw 'Unexpected game window.' }
$handle = $game.MainWindowHandle
[void][CoopWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
$rect = New-Object CoopWindow+Rect
if (![CoopWindow]::GetWindowRect($handle, [ref]$rect)) { throw 'Could not read the game window bounds.' }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0 -or $width -gt 10000 -or $height -gt 10000) { throw 'Unexpected game window size.' }
$bitmap = [Drawing.Bitmap]::new($width, $height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    if ($PerformCoopClick) {
        $unused = [uint32]0
        $foreground = [CoopWindow]::GetForegroundWindow()
        $workingThread = [CoopWindow]::GetCurrentThreadId()
        $foregroundThread = if ($foreground -ne [IntPtr]::Zero) { [CoopWindow]::GetWindowThreadProcessId($foreground, [ref]$unused) } else { 0 }
        $attached = $foregroundThread -ne 0 -and $workingThread -ne $foregroundThread -and [CoopWindow]::AttachThreadInput($workingThread, $foregroundThread, $true)
        try {
            [void][CoopWindow]::BringWindowToTop($handle)
            [void][CoopWindow]::SetForegroundWindow($handle)
        } finally {
            if ($attached) { [void][CoopWindow]::AttachThreadInput($workingThread, $foregroundThread, $false) }
        }
        Start-Sleep -Milliseconds 250
        if ([CoopWindow]::GetForegroundWindow() -eq $handle) {
            [void][CoopWindow]::SetCursorPos($rect.Left + [int]($width * $CoopClickX), $rect.Top + [int]($height * $CoopClickY))
            [CoopWindow]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 95
            [CoopWindow]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            'Foreground click delivered to the game.'
        } else {
            $point = [IntPtr](([int]($height * $CoopClickY) -shl 16) -bor [int]($width * $CoopClickX))
            [void][CoopWindow]::PostMessage($handle, 512, [IntPtr]::Zero, $point)
            [void][CoopWindow]::PostMessage($handle, 513, [IntPtr]1, $point)
            Start-Sleep -Milliseconds 95
            [void][CoopWindow]::PostMessage($handle, 514, [IntPtr]::Zero, $point)
            'Targeted window messages were sent.'
        }
        Start-Sleep -Seconds 4
    }
    $device = $graphics.GetHdc()
    try { $captured = [CoopWindow]::PrintWindow($handle, $device, 2) }
    finally { $graphics.ReleaseHdc($device) }
    if (!$captured) { throw 'Windows could not capture the game window.' }
    $path = Join-Path (Split-Path -Parent $PSScriptRoot) 'obj/coop-game-window.png'
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $preview = [Drawing.Bitmap]::new($bitmap, 1280, [int](1280 * $height / $width))
    try { $preview.Save($path, [Drawing.Imaging.ImageFormat]::Png) }
    finally { $preview.Dispose() }
    $path
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
Get-Content -LiteralPath 'N:/Steam/steamapps/common/Casualties Unknown/BepInEx/LogOutput.log' -Tail 36 | Where-Object { $_ -match 'Co-op|Steam lobby|Exception|Error' }
