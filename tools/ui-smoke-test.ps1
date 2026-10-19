<#
  UI smoke test for Dynamic Island, meant for a Windows CI runner with an interactive desktop.

  Starts the app, drives it with the mouse and UI Automation, and saves screenshots of every state
  to $OutDir. Fails when the app crashes, a control cannot be found, or it does not exit cleanly.
  With -EmitBase64 the screenshots are also written to the log, inside collapsed groups.
#>
param(
    [string]$Exe = "publish\DynamicIsland.exe",
    [string]$OutDir = "screenshots",
    [switch]$EmitBase64
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class SmokeInput
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int action, int param, string value, int flags);
    [DllImport("user32.dll")] public static extern bool SetSysColors(int count, int[] elements, int[] colors);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static void LeftClick(int x, int y)  { SetCursorPos(x, y); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void RightClick(int x, int y) { SetCursorPos(x, y); mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero); }

    // SRCCOPY | CAPTUREBLT: CAPTUREBLT is required to include layered (transparent) windows such as the island.
    public static Bitmap Capture(int x, int y, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            IntPtr dest = graphics.GetHdc();
            IntPtr screen = GetDC(IntPtr.Zero);
            BitBlt(dest, 0, 0, width, height, screen, x, y, 0x00CC0020 | 0x40000000);
            ReleaseDC(IntPtr.Zero, screen);
            graphics.ReleaseHdc(dest);
        }
        return bitmap;
    }
}
"@

[SmokeInput]::SetProcessDPIAware() | Out-Null

# Plain grey desktop: makes the black island easy to judge and keeps screenshots small.
[SmokeInput]::SystemParametersInfo(0x0014, 0, "", 3) | Out-Null   # SPI_SETDESKWALLPAPER: none
[SmokeInput]::SetSysColors(1, @(1), @(0x00707070)) | Out-Null       # COLOR_DESKTOP

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$centerX = [int]($screen.Left + $screen.Width / 2)
Write-Host "Screen: $($screen.Width)x$($screen.Height)"

function Save-Shot([string]$Name, [int]$Width = 520, [int]$Height = 220) {
    $x = [int]($screen.Left + ($screen.Width - $Width) / 2)
    $bitmap = [SmokeInput]::Capture($x, $screen.Top, $Width, $Height)
    $path = Join-Path $OutDir "$Name.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Saved $path"

    if ($EmitBase64) {
        $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
        Write-Host "::group::PNG $Name ($($base64.Length) chars)"
        for ($i = 0; $i -lt $base64.Length; $i += 4000) {
            Write-Host ("B64:${Name}:" + $base64.Substring($i, [Math]::Min(4000, $base64.Length - $i)))
        }
        Write-Host "::endgroup::"
    }
}

function Assert-Running {
    if ($script:process.HasExited) { throw "Dynamic Island exited unexpectedly (code $($script:process.ExitCode))" }
}

function Find-ByName([string]$Name, [int]$TimeoutSeconds = 10) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Name
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        # The island and its popups (context menu, submenu) are separate top-level windows of our process.
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $script:process.Id))
        foreach ($window in $windows) {
            $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($found) { return $found }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "UI element '$Name' not found"
}

function Invoke-ByName([string]$Name) {
    $element = Find-ByName $Name
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "Invoked '$Name'"
}

function Expand-ByName([string]$Name) {
    $element = Find-ByName $Name
    $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Write-Host "Expanded '$Name'"
}

function Move-Away { [SmokeInput]::SetCursorPos($centerX, [int]($screen.Height / 2)) | Out-Null }

# Fresh settings so the run always starts from the defaults.
$dataDir = Join-Path $env:APPDATA "DynamicIsland"
Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue

Move-Away
$script:process = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 5
Assert-Running
Save-Shot "01-idle"

# Hover → expanded clock with quick timers.
$islandY = 24
[SmokeInput]::SetCursorPos($centerX, $islandY) | Out-Null
Start-Sleep -Milliseconds 1500
Assert-Running
Save-Shot "02-clock"

# Start a timer from the quick chips → expanded timer.
Invoke-ByName "25 mnt"
Start-Sleep -Milliseconds 1500
Save-Shot "03-timer-expanded"

Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "04-timer-compact"

# Context menu and its Timer submenu.
[SmokeInput]::RightClick($centerX, $islandY)
Start-Sleep -Milliseconds 1200
Save-Shot "05-menu" 760 520
Expand-ByName "Timer"
Start-Sleep -Milliseconds 1000
Save-Shot "06-submenu" 900 560

# 1-minute timer from the submenu, then wait for the alarm.
Invoke-ByName "1 menit"
Move-Away
Start-Sleep -Seconds 63
Assert-Running
Save-Shot "07-timer-done"

# Dismiss the alarm by clicking it.
[SmokeInput]::LeftClick($centerX, $islandY)
Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "08-after-dismiss"

# Switch to the notch style.
[SmokeInput]::RightClick($centerX, $islandY)
Start-Sleep -Milliseconds 1000
Invoke-ByName "Gaya notch (menempel di atas)"
Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "09-notch-idle"
[SmokeInput]::SetCursorPos($centerX, 14) | Out-Null
Start-Sleep -Milliseconds 1500
Save-Shot "10-notch-clock"

# Exit through the menu and make sure the process really ends.
Move-Away
Start-Sleep -Milliseconds 800
[SmokeInput]::RightClick($centerX, 14)
Start-Sleep -Milliseconds 1000
Invoke-ByName "Keluar"
if (-not $script:process.WaitForExit(10000)) { throw "Dynamic Island did not exit after 'Keluar'" }
Write-Host "Exited with code $($script:process.ExitCode)"

$log = Join-Path $dataDir "error.log"
if (Test-Path $log) {
    Write-Host "::group::error.log"
    Get-Content $log | Write-Host
    Write-Host "::endgroup::"
} else {
    Write-Host "No error.log written."
}
