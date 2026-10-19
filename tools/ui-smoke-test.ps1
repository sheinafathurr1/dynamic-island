<#
  UI smoke test for Dynamic Island, meant for a Windows CI runner with an interactive desktop.

  Starts the app, drives it with the mouse, OLE drag & drop and UI Automation, and saves screenshots
  of every state to $OutDir. Fails when the app crashes, a control cannot be found, a drop does not
  land on the shelf, or the app does not exit cleanly.
  With -EmitBase64 the screenshots are also written to the log, inside collapsed groups.
#>
param(
    [string]$Exe = "publish\DynamicIsland.exe",
    [string]$OutDir = "screenshots",
    [switch]$EmitBase64
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes

# Shared with the background job that performs the drag (it runs in its own process).
$helperSource = @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class SmokeInput
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int action, int param, string value, int flags);
    [DllImport("user32.dll")] public static extern bool SetSysColors(int count, int[] elements, int[] colors);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static void LeftClick(int x, int y)  { SetCursorPos(x, y); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void RightClick(int x, int y) { SetCursorPos(x, y); mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero); }
    public static void MiddleClick()            { mouse_event(0x0020, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0040, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftDown(int x, int y)   { SetCursorPos(x, y); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp()                 { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Wheel(int delta)         { mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero); }
    public static void HWheel(int delta)        { mouse_event(0x1000, 0, 0, unchecked((uint)delta), UIntPtr.Zero); }
    public static void ShiftWheel(int delta)    { keybd_event(0x10, 0, 0, UIntPtr.Zero); Wheel(delta); System.Threading.Thread.Sleep(150); keybd_event(0x10, 0, 2, UIntPtr.Zero); }

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
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition $helperSource

[SmokeInput]::SetProcessDPIAware() | Out-Null

# Plain grey, empty desktop: makes the black island easy to judge and keeps screenshots small.
[SmokeInput]::SystemParametersInfo(0x0014, 0, "", 3) | Out-Null   # SPI_SETDESKWALLPAPER: none
[SmokeInput]::SetSysColors(1, @(1), @(0x00707070)) | Out-Null       # COLOR_DESKTOP
(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$centerX = [int]($screen.Left + $screen.Width / 2)
Write-Host "Screen: $($screen.Width)x$($screen.Height)"

function Emit-Shot([string]$Name) {
    $path = Join-Path $OutDir "$Name.png"
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

function Save-Shot([string]$Name, [int]$Width = 520, [int]$Height = 220) {
    $x = [int]($screen.Left + ($screen.Width - $Width) / 2)
    $bitmap = [SmokeInput]::Capture($x, $screen.Top, $Width, $Height)
    $bitmap.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Emit-Shot $Name
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

# ── Test files: two already on the shelf, one to drag in later ─────────────────────────────────
$files = Join-Path $env:TEMP "island-test"
Remove-Item -Recurse -Force $files -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $files "Proyek Skripsi") | Out-Null
$photo = Join-Path $files "Foto pantai.png"
$sample = New-Object System.Drawing.Bitmap 320, 240
$graphics = [System.Drawing.Graphics]::FromImage($sample)
$sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, 320, 240), ([System.Drawing.Color]::FromArgb(255, 255, 159, 67)), ([System.Drawing.Color]::FromArgb(255, 30, 144, 255)), 90
$graphics.FillRectangle($sky, 0, 0, 320, 240)
$graphics.FillEllipse([System.Drawing.Brushes]::Gold, 200, 40, 70, 70)
$graphics.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 238, 214, 175))), 0, 180, 320, 60)
$graphics.Dispose(); $sample.Save($photo, [System.Drawing.Imaging.ImageFormat]::Png); $sample.Dispose()
$note = Join-Path $files "Catatan rapat.txt"
Set-Content -Path $note -Value "Agenda rapat"

# Fresh settings, with the photo and the folder already deposited on the shelf.
$dataDir = Join-Path $env:APPDATA "DynamicIsland"
Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
ConvertTo-Json @($photo, (Join-Path $files "Proyek Skripsi")) | Set-Content -Path (Join-Path $dataDir "shelf.json") -Encoding UTF8

# ── Launch ──────────────────────────────────────────────────────────────────────────────────────
Move-Away
$script:process = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 5
Assert-Running
Save-Shot "01-idle"

# Hover → expanded carousel. Nothing plays and no timer runs, so it opens on "today".
$islandY = 24
[SmokeInput]::SetCursorPos($centerX, $islandY) | Out-Null
Start-Sleep -Milliseconds 1500
Assert-Running
Save-Shot "02-today"

# Shift + wheel is volume (no audio device on CI: must simply not crash), middle click = mute.
[SmokeInput]::ShiftWheel(120)
[SmokeInput]::MiddleClick()
Start-Sleep -Milliseconds 500
Assert-Running

# Plain wheel up → previous page: the timer with its start buttons.
[SmokeInput]::Wheel(120)
Start-Sleep -Milliseconds 1200
Save-Shot "03-timer-page"

Invoke-ByName "25 mnt"
Start-Sleep -Milliseconds 1500
Save-Shot "04-timer-running"

# Drag the page to the left like a finger swipe; screenshot halfway, then let go.
[SmokeInput]::LeftDown($centerX - 40, 60)
foreach ($step in 1..10) { [SmokeInput]::SetCursorPos($centerX - 40 - $step * 15, 60) | Out-Null; Start-Sleep -Milliseconds 15 }
Start-Sleep -Milliseconds 300
Save-Shot "05-swipe-halfway"
foreach ($step in 11..14) { [SmokeInput]::SetCursorPos($centerX - 40 - $step * 15, 60) | Out-Null; Start-Sleep -Milliseconds 10 }
[SmokeInput]::LeftUp()
[SmokeInput]::SetCursorPos($centerX, 40) | Out-Null   # back over the island so it stays open
Start-Sleep -Milliseconds 1200
Assert-Running
Save-Shot "06-swiped-to-today"

# Plain wheel down → next page: the file shelf with the two deposited items.
[SmokeInput]::Wheel(-120)
Start-Sleep -Milliseconds 1500
Save-Shot "07-shelf"
[SmokeInput]::HWheel(120)   # already the last page: edge bounce
Start-Sleep -Milliseconds 800
Assert-Running

# Click the first pagination dot (shelf page: island bottom = 8 + 134 + 16 = 158; dots 6+6+16 wide).
[SmokeInput]::LeftClick($centerX - 17, 146)
[SmokeInput]::SetCursorPos($centerX, 40) | Out-Null
Start-Sleep -Milliseconds 1200
Save-Shot "08-dot-click"

Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "09-timer-compact"

# ── Real OLE drag & drop: drag a file from another window onto the island, like from Explorer ──
$form = New-Object System.Windows.Forms.Form
$form.StartPosition = "Manual"
$form.Location = New-Object System.Drawing.Point 40, 560
$form.Size = New-Object System.Drawing.Size 180, 120
$form.TopMost = $true
$form.Text = "drag source"
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

$dropList = New-Object System.Collections.Specialized.StringCollection
[void]$dropList.Add($note)
$data = New-Object System.Windows.Forms.DataObject
$data.SetFileDropList($dropList)

# DoDragDrop blocks this thread, so a separate process moves the mouse, screenshots and lets go.
$mover = Start-Job -ArgumentList $helperSource, $centerX, $OutDir -ScriptBlock {
    param($source, $cx, $out)
    Add-Type -AssemblyName System.Drawing
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition $source
    [SmokeInput]::SetProcessDPIAware() | Out-Null
    Start-Sleep -Milliseconds 800
    foreach ($i in 1..30) {
        $x = [int](130 + ($cx - 130) * $i / 30); $y = [int](620 + (24 - 620) * $i / 30)
        [SmokeInput]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 25
    }
    foreach ($i in 1..6) { [SmokeInput]::SetCursorPos($cx + $i, 30) | Out-Null; Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 900
    $width = 520; $height = 220
    $shot = [SmokeInput]::Capture([int]($cx - $width / 2), 0, $width, $height)
    $shot.Save((Join-Path $out "10-drop-zone.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $shot.Dispose()
    [SmokeInput]::LeftUp()
}
# Injected input is asynchronous: wait until Windows really sees the button held, or OLE ends the
# drag on its very first check.
[SmokeInput]::LeftDown(130, 620)
$deadline = (Get-Date).AddSeconds(3)
while ([System.Windows.Forms.Control]::MouseButtons -ne [System.Windows.Forms.MouseButtons]::Left -and (Get-Date) -lt $deadline) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 20
}
Write-Host "Button held before drag: $([System.Windows.Forms.Control]::MouseButtons)"
$dragWatch = [System.Diagnostics.Stopwatch]::StartNew()
$effect = $form.DoDragDrop($data, [System.Windows.Forms.DragDropEffects]::Copy -bor [System.Windows.Forms.DragDropEffects]::Link)
$dragWatch.Stop()
Receive-Job $mover -Wait | Out-Host
Remove-Job $mover
$form.Close()
Write-Host "Drop effect: $effect after $([int]$dragWatch.Elapsed.TotalMilliseconds) ms"
Emit-Shot "10-drop-zone"

Start-Sleep -Milliseconds 900
Assert-Running
Save-Shot "11-after-drop"
$shelf = Get-Content (Join-Path $dataDir "shelf.json") -Raw | ConvertFrom-Json
if (-not ($shelf -contains $note)) { throw "Dropped file did not land on the shelf: $($shelf -join ', ')" }
Write-Host "Shelf now holds: $($shelf -join ' | ')"

Move-Away
Start-Sleep -Milliseconds 2500

# ── Context menu and its Timer submenu ─────────────────────────────────────────────────────────
[SmokeInput]::RightClick($centerX, $islandY)
Start-Sleep -Milliseconds 1200
Save-Shot "12-menu" 760 520
Expand-ByName "Timer"
Start-Sleep -Milliseconds 1000
Save-Shot "13-submenu" 900 560

# 1-minute timer from the submenu, then wait for the alarm.
Invoke-ByName "1 menit"
Move-Away
Start-Sleep -Seconds 63
Assert-Running
Save-Shot "14-timer-done"

# Dismiss the alarm by clicking it.
[SmokeInput]::LeftClick($centerX, $islandY)
Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "15-after-dismiss"

# ── Lyrics: a media session like Spotify's, created through the same Windows API (SMTC) ──────
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try {
    $probe = Invoke-RestMethod -TimeoutSec 15 -Headers @{ "User-Agent" = "DynamicIsland-CI" } -Uri "https://lrclib.net/api/get?track_name=Bohemian%20Rhapsody&artist_name=Queen&album_name=A%20Night%20at%20the%20Opera&duration=354"
    Write-Host "LRCLIB probe: synced lyrics $([bool]$probe.syncedLyrics), duration $($probe.duration)"
} catch {
    Write-Host "LRCLIB probe failed: $_"
}

$player = $null
try {
    [void][Windows.Media.Playback.MediaPlayer, Windows.Media.Playback, ContentType = WindowsRuntime]
    [void][Windows.Media.SystemMediaTransportControlsTimelineProperties, Windows.Media, ContentType = WindowsRuntime]
    $player = [Windows.Media.Playback.MediaPlayer]::new()
    $player.CommandManager.IsEnabled = $false   # we drive the system media controls ourselves
    $smtc = $player.SystemMediaTransportControls
    $smtc.IsEnabled = $true
    $smtc.IsPlayEnabled = $true
    $smtc.IsPauseEnabled = $true
    $smtc.IsNextEnabled = $true
    $smtc.IsPreviousEnabled = $true
    $updater = $smtc.DisplayUpdater
    $updater.Type = [Windows.Media.MediaPlaybackType]::Music
    $updater.MusicProperties.Title = "Bohemian Rhapsody"
    $updater.MusicProperties.Artist = "Queen"
    $updater.MusicProperties.AlbumTitle = "A Night at the Opera"
    $updater.Update()
    $timeline = [Windows.Media.SystemMediaTransportControlsTimelineProperties]::new()
    $timeline.StartTime = [TimeSpan]::Zero
    $timeline.MinSeekTime = [TimeSpan]::Zero
    $timeline.EndTime = [TimeSpan]::FromSeconds(354)
    $timeline.MaxSeekTime = [TimeSpan]::FromSeconds(354)
    $timeline.Position = [TimeSpan]::FromSeconds(55)
    $smtc.UpdateTimelineProperties($timeline)
    $smtc.PlaybackStatus = [Windows.Media.MediaPlaybackStatus]::Playing
    Write-Host "Fake media session: Queen - Bohemian Rhapsody"
} catch {
    Write-Host "Could not create a media session: $_"
}

Move-Away
Start-Sleep -Seconds 6   # session pick-up + lyrics download
Assert-Running
Save-Shot "18-compact-lyrics"
[SmokeInput]::SetCursorPos($centerX, $islandY) | Out-Null
Start-Sleep -Milliseconds 1500
Save-Shot "19-media-page"
[SmokeInput]::Wheel(-120)   # next page: lyrics
Start-Sleep -Milliseconds 1800
Assert-Running
Save-Shot "20-lyrics-page"
Move-Away
Start-Sleep -Milliseconds 1500

# Switch to the notch style.
[SmokeInput]::RightClick($centerX, $islandY)
Start-Sleep -Milliseconds 1000
Invoke-ByName "Gaya notch (menempel di atas)"
Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "16-notch-idle"
[SmokeInput]::SetCursorPos($centerX, 14) | Out-Null
Start-Sleep -Milliseconds 1500
Save-Shot "17-notch-today"

# Exit through the menu and make sure the process really ends.
Move-Away
Start-Sleep -Milliseconds 800
[SmokeInput]::RightClick($centerX, 14)
Start-Sleep -Milliseconds 1000
Invoke-ByName "Keluar"
if (-not $script:process.WaitForExit(10000)) { throw "Dynamic Island did not exit after 'Keluar'" }
Write-Host "Exited with code $($script:process.ExitCode)"
if ($player) { $player.Dispose() }

$log = Join-Path $dataDir "error.log"
if (Test-Path $log) {
    Write-Host "::group::error.log"
    Get-Content $log | Write-Host
    Write-Host "::endgroup::"
} else {
    Write-Host "No error.log written."
}
