<#
  UI smoke test for Dynamic Island, meant for a Windows CI runner with an interactive desktop.

  Starts the app, drives it with the mouse, OLE drag & drop and UI Automation, and saves screenshots
  of every state to $OutDir. Fails when the app crashes, a control cannot be found, a drop does not
  land on the shelf, or the app does not exit cleanly.
  Checks that depend on the runner (a sound card for the live equalizer, Windows' notification
  service for app notifications) are reported but do not fail the run.
  With -EmitBase64 the screenshots are also written to the log, inside collapsed groups.
#>
param(
    [string]$Exe = "publish\DynamicIsland.exe",
    [string]$OutDir = "screenshots",
    [switch]$EmitBase64
)

$ErrorActionPreference = "Stop"

# Whatever fails, show what the app logged: that is usually the reason.
trap {
    $log = Join-Path $env:APPDATA "DynamicIsland\error.log"
    if (Test-Path $log) {
        Write-Host "::group::error.log"
        Get-Content $log | Write-Host
        Write-Host "::endgroup::"
    }
    break
}
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
    public static void Key(byte key)            { keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero); }
    public static void Chord(byte modifier, byte key) { keybd_event(modifier, 0, 0, UIntPtr.Zero); Key(key); keybd_event(modifier, 0, 2, UIntPtr.Zero); }

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

# A few bars of made-up music (kick, snare, hi-hat, bass line, chords) for the live equalizer.
Add-Type -TypeDefinition @"
using System;
using System.IO;
public static class TestSong
{
    public static void Write(string path, int seconds)
    {
        const int rate = 44100;
        int frames = rate * seconds;
        var random = new Random(7);
        double[] bass = { 55.0, 43.65, 65.41, 49.0 };
        double[][] chords = { new[] { 220.0, 261.63, 329.63 }, new[] { 174.61, 220.0, 261.63 }, new[] { 261.63, 329.63, 392.0 }, new[] { 196.0, 246.94, 293.66 } };
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' }); writer.Write(36 + frames * 4);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); writer.Write(16);
            writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' }); writer.Write(frames * 4);
            double hat = 0, previousNoise = 0;
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / rate;
                double beat = t % 0.5, half = t % 0.25, bar = t % 2.0;
                int chord = (int)(t / 2.0) % 4;
                double kick = Math.Sin(2 * Math.PI * (45 + 75 * Math.Exp(-beat / 0.03)) * beat) * Math.Exp(-beat / 0.12) * 0.9;
                double noise = random.NextDouble() * 2 - 1;
                double snareTime = (t + 0.5) % 1.0;
                double snare = (noise * 0.6 + Math.Sin(2 * Math.PI * 185 * snareTime) * 0.4) * Math.Exp(-snareTime / 0.09) * 0.5;
                hat = (noise - previousNoise) * 0.5; previousNoise = noise;
                double hats = half > 0.125 ? hat * Math.Exp(-(half - 0.125) / 0.025) * 0.35 : 0;
                double note = bass[chord] * ((int)(bar / 0.25) % 2 == 0 ? 1 : 2);
                double bassLine = Math.Sin(2 * Math.PI * note * t) * Math.Exp(-(t % 0.25) / 0.2) * 0.35;
                double pad = 0;
                foreach (double frequency in chords[chord]) pad += Math.Sin(2 * Math.PI * frequency * t);
                pad *= 0.06 * Math.Min(1, bar / 0.3) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * t / 8));
                double sample = Math.Tanh((kick + snare + hats + bassLine + pad) * 0.9) * 0.8;
                short value = (short)(sample * 32767);
                writer.Write(value); writer.Write(value);
            }
        }
    }
}
"@

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

# Screenshot of a region centred on the screen (dialogs open there).
function Save-CenterShot([string]$Name, [int]$Width, [int]$Height) {
    $x = [int]($screen.Left + ($screen.Width - $Width) / 2)
    $y = [int]($screen.Top + ($screen.Height - $Height) / 2)
    $bitmap = [SmokeInput]::Capture($x, $y, $Width, $Height)
    $bitmap.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Emit-Shot $Name
}

# Reads Windows' notification database (through the SQLite that ships with Windows) and the
# Do not disturb state, to explain what happened to a test notification.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WpnProbe
{
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong name, IntPtr type, IntPtr scope, out uint stamp, byte[] buffer, ref uint size);

    static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
    static string Text(IntPtr p) { if (p == IntPtr.Zero) return ""; int n = 0; while (Marshal.ReadByte(p, n) != 0) n++; var b = new byte[n]; Marshal.Copy(p, b, 0, n); return Encoding.UTF8.GetString(b); }

    public static string Query(string path, string sql)
    {
        var output = new StringBuilder();
        IntPtr db;
        int rc = sqlite3_open_v2(Utf8(path), out db, 1, IntPtr.Zero);
        if (rc != 0) return "open failed: " + rc;
        IntPtr statement;
        rc = sqlite3_prepare_v2(db, Utf8(sql), -1, out statement, IntPtr.Zero);
        if (rc != 0) { output.Append("prepare failed: " + rc + " " + Text(sqlite3_errmsg(db))); sqlite3_close_v2(db); return output.ToString(); }
        while ((rc = sqlite3_step(statement)) == 100)
        {
            for (int i = 0; i < sqlite3_column_count(statement); i++)
            {
                string value = Text(sqlite3_column_text(statement, i)).Replace("\r", " ").Replace("\n", " ");
                output.Append(i == 0 ? "  " : " | ").Append(value.Length > 400 ? value.Substring(0, 400) + "..." : value);
            }
            output.AppendLine();
        }
        output.Append("  (step ended with " + rc + ")");
        sqlite3_finalize(statement);
        sqlite3_close_v2(db);
        return output.ToString();
    }

    public static string QuietHours()
    {
        ulong name = 0x0D83063EA3BF1C75;
        var buffer = new byte[32];
        uint size = (uint)buffer.Length, stamp;
        int status = NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out stamp, buffer, ref size);
        return "status 0x" + status.ToString("X8") + ", " + size + " bytes: " + BitConverter.ToString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
    }
}
"@

function Get-QuietHours { [WpnProbe]::QuietHours() }

# Windows Server turns Do not disturb on by default; switch it off in Settings → System →
# Notifications like a user would, so toasts reach the island. Windows' rules stay respected.
function Disable-DoNotDisturb {
    Write-Host "Do not disturb before: $(Get-QuietHours)"
    if ((Get-QuietHours) -match ': 00-00-00-00') { return }
    Start-Process "ms-settings:notifications"
    $any = [System.Windows.Automation.Condition]::TrueCondition
    $switch = $null
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 1000
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $any)) {
            if ($window.Current.Name -notlike "*Settings*") { continue }
            $switch = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $any) |
                Where-Object { $pattern = $null; $_.Current.Name -like "*Do not disturb*" -and $_.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern) } |
                Select-Object -First 1
            if ($switch) { $settings = $window; break }
        }
    } while (-not $switch -and (Get-Date) -lt $deadline)

    if ($switch) {
        $toggle = $switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        Write-Host "Settings switch '$($switch.Current.Name)' is $($toggle.Current.ToggleState)"
        if ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
        Start-Sleep -Milliseconds 800
        Save-CenterShot "22a-settings-dnd" 900 700
        try { $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    } else {
        Write-Host "No 'Do not disturb' switch found in Settings"
        Save-CenterShot "22a-settings-dnd" 900 700
        Get-Process SystemSettings -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    Move-Away
    Start-Sleep -Milliseconds 1000
    Write-Host "Do not disturb after: $(Get-QuietHours)"
}

# What Windows did with the test toast: its own banner (bottom right), the notification settings,
# Do not disturb, and the newest rows of the notification database the island reads.
function Show-NotificationDiagnostics {
    $bitmap = [SmokeInput]::Capture($screen.Right - 460, $screen.Bottom - 300, 460, 300)
    $bitmap.Save((Join-Path $OutDir "22b-windows-toast.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Emit-Shot "22b-windows-toast"
    foreach ($key in @("HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications",
                       "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings",
                       "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe")) {
        $values = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        Write-Host "$key -> $(if ($values) { ($values.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; ' } else { 'missing' })"
    }
    Write-Host "Quiet hours (Do not disturb) state: $([WpnProbe]::QuietHours())"
    $db = Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Notifications\wpndatabase.db"
    Write-Host "Now as FILETIME: $([DateTime]::UtcNow.ToFileTimeUtc())"
    Write-Host "Notification table:"
    Write-Host ([WpnProbe]::Query($db, "SELECT sql FROM sqlite_master WHERE name IN ('Notification', 'NotificationHandler')"))
    Write-Host "Newest notifications:"
    Write-Host ([WpnProbe]::Query($db, "SELECT n.rowid, n.Id, n.Type, h.PrimaryId, n.ArrivalTime, CAST(n.Payload AS TEXT) FROM Notification n LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId ORDER BY n.rowid DESC LIMIT 6"))
}

# The settings window: right-click the island → "Pengaturan…". The script is read as ANSI by
# Windows PowerShell, so the ellipsis is spelled as a char code.
function Open-Settings([int]$Y) {
    [SmokeInput]::RightClick($centerX, $Y)
    Start-Sleep -Milliseconds 1000
    Invoke-ByName ("Pengaturan" + [char]0x2026)
    Start-Sleep -Milliseconds 1500
}

function Close-Settings {
    Invoke-ByName "Tutup pengaturan"
    Start-Sleep -Milliseconds 800
}

function Toggle-ByName([string]$Name) {
    (Find-ByName $Name).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Write-Host "Toggled '$Name'"
}

function Select-ByName([string]$Name) {
    (Find-ByName $Name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Host "Selected '$Name'"
}

# Like Find-ByName, but for checks that depend on the runner: $null instead of failing.
function Try-FindByName([string]$Name, [int]$TimeoutSeconds = 6) {
    try { return Find-ByName $Name $TimeoutSeconds } catch { return $null }
}

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

# A pretend call later on: Windows notes apps that record from the microphone in its privacy
# consent store, which is what the island watches. Make sure the store exists before launching.
# (The key name holds "C:", which PowerShell's registry paths dislike: use .NET's registry API.)
$consent = "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged"
[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($consent).Dispose()
$callKey = "$consent\C:#Program Files#Zoom#bin#Zoom.exe"
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($callKey, $false)
function Set-CallTimes([long]$Start, [long]$Stop) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($callKey)
    $key.SetValue("LastUsedTimeStart", $Start, [Microsoft.Win32.RegistryValueKind]::QWord)
    $key.SetValue("LastUsedTimeStop", $Stop, [Microsoft.Win32.RegistryValueKind]::QWord)
    $key.Dispose()
}

# ── Launch ──────────────────────────────────────────────────────────────────────────────────────
Move-Away
$script:process = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 5
Assert-Running
Save-Shot "01-idle"

# Idle, the island is a thin line at the very top edge: push the pointer up there to open it.
# Nothing plays and no timer runs, so it opens on "today". Then into the open island.
$islandY = 24
[SmokeInput]::SetCursorPos($centerX, 1) | Out-Null
Start-Sleep -Milliseconds 1200
[SmokeInput]::SetCursorPos($centerX, $islandY + 16) | Out-Null
Start-Sleep -Milliseconds 600
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

# Plain wheel down → next pages: quick controls, system, then the file shelf with the two deposited items.
[SmokeInput]::Wheel(-120)
Start-Sleep -Milliseconds 1500
Save-Shot "07a-controls"
Invoke-ByName "Mode gelap"
Start-Sleep -Milliseconds 800
$light = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme
Write-Host "Dark mode switch: AppsUseLightTheme=$light"
if ($light -ne 0) { throw "The dark mode switch did not switch Windows to dark mode" }
Save-Shot "07a-controls-dark"
Invoke-ByName "Mode gelap"   # and back
Start-Sleep -Milliseconds 500
[SmokeInput]::Wheel(-120)
Start-Sleep -Milliseconds 2600   # two readings, so CPU and network show real numbers
Save-Shot "07b-system"
[SmokeInput]::Wheel(-120)
Start-Sleep -Milliseconds 1500
Save-Shot "07-shelf"
[SmokeInput]::HWheel(120)   # already the last page: edge bounce
Start-Sleep -Milliseconds 800
Assert-Running

# Click the first pagination dot (shelf page: island bottom = 8 + 134 + 16 = 158; five dots of
# 6+6 px with a 16 px pill for the current one, 6 px apart).
[SmokeInput]::LeftClick($centerX - 29, 146)
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

# 1-minute timer from the submenu; peek at the last seconds (the ring turns red), then wait for the alarm.
Invoke-ByName "1 menit"
Move-Away
Start-Sleep -Seconds 51
[SmokeInput]::SetCursorPos($centerX, $islandY) | Out-Null
Start-Sleep -Milliseconds 1500
Save-Shot "13b-timer-last-seconds"
Move-Away
Start-Sleep -Seconds 12
Assert-Running
Save-Shot "14-timer-done"

# Dismiss the alarm by clicking it.
[SmokeInput]::LeftClick($centerX, $islandY)
Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "15-after-dismiss"

# ── Lyrics: a media session like Spotify's, created through the same Windows API (SMTC) ──────
# A public-domain song, so no copyrighted lyrics end up in screenshots. Pick a version that LRCLIB
# has with synced lyrics, and check it really is the traditional text.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$track = $null
foreach ($candidate in @(@("Silent Night", "holy night"), @("Amazing Grace", "wretch like me"), @("Auld Lang Syne", "auld acquaintance"))) {
    try {
        $results = Invoke-RestMethod -TimeoutSec 15 -Headers @{ "User-Agent" = "DynamicIsland-CI" } `
            -Uri ("https://lrclib.net/api/search?track_name=" + [Uri]::EscapeDataString($candidate[0]))
        $track = $results | Where-Object {
            $_.syncedLyrics -and $_.albumName -and $_.duration -gt 90 -and $_.syncedLyrics -match $candidate[1]
        } | Select-Object -First 1
        if ($track) { break }
    } catch {
        Write-Host "LRCLIB search failed: $_"
    }
}

# Start a few lines in, so the screenshots show lyrics mid-song.
$position = [TimeSpan]::FromSeconds(30)
if ($track) {
    $stamps = [regex]::Matches($track.syncedLyrics, '\[(\d+):(\d+(?:\.\d+)?)\]')
    if ($stamps.Count -gt 5) {
        $position = [TimeSpan]::FromSeconds([int]$stamps[5].Groups[1].Value * 60 + [double]$stamps[5].Groups[2].Value + 0.5)
    }
    Write-Host "Test track: $($track.artistName) - $($track.trackName) ($($track.albumName), $($track.duration)s) from $position"
} else {
    Write-Host "No public-domain test track found on LRCLIB; the lyrics screenshots will show the fallback state."
    $track = [pscustomobject]@{ trackName = "Silent Night"; artistName = "Traditional"; albumName = ""; duration = 180 }
}

# Cover art for the fake track, so the ambient glow and the tinted equalizer show up too.
$cover = Join-Path $files "cover.png"
$art = New-Object System.Drawing.Bitmap 300, 300
$graphics = [System.Drawing.Graphics]::FromImage($art)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$night = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, 300, 300), ([System.Drawing.Color]::FromArgb(255, 12, 52, 66)), ([System.Drawing.Color]::FromArgb(255, 70, 34, 110)), 45
$graphics.FillRectangle($night, 0, 0, 300, 300)
$graphics.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 40, 200, 220))), 30, 40, 170, 170)
$graphics.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 230, 90, 200))), 140, 120, 140, 140)
$graphics.FillEllipse([System.Drawing.Brushes]::White, 200, 50, 18, 18)
$graphics.Dispose(); $art.Save($cover, [System.Drawing.Imaging.ImageFormat]::Png); $art.Dispose()

# PowerShell 5.1 cannot await WinRT operations directly; bridge them to .NET tasks.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq "AsTask" -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await-WinRt($operation, [Type]$resultType) {
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.Wait(-1) | Out-Null
    $task.Result
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
    $updater.MusicProperties.Title = $track.trackName
    $updater.MusicProperties.Artist = $track.artistName
    $updater.MusicProperties.AlbumTitle = $track.albumName
    try {
        [void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        [void][Windows.Storage.Streams.RandomAccessStreamReference, Windows.Storage.Streams, ContentType = WindowsRuntime]
        $coverFile = Await-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync($cover)) ([Windows.Storage.StorageFile])
        $updater.Thumbnail = [Windows.Storage.Streams.RandomAccessStreamReference]::CreateFromFile($coverFile)
    } catch {
        Write-Host "Could not attach cover art: $_"
    }
    $updater.Update()
    $timeline = [Windows.Media.SystemMediaTransportControlsTimelineProperties]::new()
    $timeline.StartTime = [TimeSpan]::Zero
    $timeline.MinSeekTime = [TimeSpan]::Zero
    $timeline.EndTime = [TimeSpan]::FromSeconds([double]$track.duration)
    $timeline.MaxSeekTime = [TimeSpan]::FromSeconds([double]$track.duration)
    $timeline.Position = $position
    $smtc.UpdateTimelineProperties($timeline)
    $smtc.PlaybackStatus = [Windows.Media.MediaPlaybackStatus]::Playing
    Write-Host "Fake media session: $($track.artistName) - $($track.trackName)"
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
# Hover the progress bar (island top 8 + margin 18 + artwork 60 + gap 13 + half of the 16px strip): it grows and shows its knob.
[SmokeInput]::SetCursorPos($centerX + 40, 107) | Out-Null
Start-Sleep -Milliseconds 700
Save-Shot "19b-media-hover"
[SmokeInput]::Wheel(-120)   # next page: lyrics
Start-Sleep -Milliseconds 1800
Assert-Running
Save-Shot "20-lyrics-page"
Move-Away
Start-Sleep -Milliseconds 1500

# ── Live equalizer: play real music on the (virtual) sound card ─────────────────────────────────
Get-CimInstance Win32_SoundDevice | ForEach-Object { Write-Host "Sound device: $($_.Name) ($($_.Status))" }
$song = Join-Path $files "test-song.wav"
[TestSong]::Write($song, 20)
$sound = $null
try {
    $sound = New-Object System.Media.SoundPlayer $song
    $sound.PlayLooping()
} catch {
    Write-Host "Could not play the test song: $_"
}
Start-Sleep -Seconds 3
Assert-Running
Save-Shot "21-compact-live"
Start-Sleep -Milliseconds 400
Save-Shot "21b-compact-live"
if (Try-FindByName "Equalizer (live)") { Write-Host "Equalizer: follows the real audio" }
else { Write-Host "Equalizer: canned animation (no audio heard on this runner)" }

# ── App notification: a real toast, like WhatsApp would send ───────────────────────────────────
$avatar = Join-Path $files "avatar.png"
$face = New-Object System.Drawing.Bitmap 128, 128
$graphics = [System.Drawing.Graphics]::FromImage($face)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
$warm = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, 128, 128), ([System.Drawing.Color]::FromArgb(255, 255, 140, 90)), ([System.Drawing.Color]::FromArgb(255, 220, 60, 130)), 45
$graphics.FillRectangle($warm, 0, 0, 128, 128)
$font = New-Object System.Drawing.Font "Segoe UI Semibold", 54
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$graphics.DrawString("R", $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, 128, 128), $format)
$graphics.Dispose(); $face.Save($avatar, [System.Drawing.Imaging.ImageFormat]::Png); $face.Dispose()

Write-Host "Notification database present: $(Test-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Notifications\wpndatabase.db'))"
Disable-DoNotDisturb
# The runner's notification service sometimes needs a moment ("platform unavailable"): retry.
[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
$powerShellApp = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
function Send-Toast([string]$Title, [string]$Message) {
    foreach ($attempt in 1..4) {
        try {
            $toastXml = [Windows.Data.Xml.Dom.XmlDocument]::new()
            $toastXml.LoadXml(@"
<toast><visual><binding template="ToastGeneric">
  <text>$Title</text>
  <text>$Message</text>
  <image placement="appLogoOverride" hint-crop="circle" src="$([Uri]::new($avatar).AbsoluteUri)"/>
</binding></visual></toast>
"@)
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($powerShellApp).Show([Windows.UI.Notifications.ToastNotification]::new($toastXml))
            Write-Host "Toast sent: $Title"
            return
        } catch {
            Write-Host "Could not send a toast (attempt $attempt): $_"
            Start-Sleep -Seconds 4
        }
    }
}

# Windows' own banner shows in the bottom-right corner.
function Save-CornerShot([string]$Name) {
    $bitmap = [SmokeInput]::Capture($screen.Right - 460, $screen.Bottom - 300, 460, 300)
    $bitmap.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Emit-Shot $Name
}

$bannerKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\$powerShellApp"
Send-Toast "Rina" "Jadi rapat jam 3 nanti? Aku bawa laptopnya ya"
if (Try-FindByName "Rina" 8) {
    Start-Sleep -Milliseconds 700
    Save-Shot "22-notification"
    Save-CornerShot "22b-windows-corner-first"
    Write-Host "Notification: shown in the island"

    # The island now owns this app's pop-ups: Windows' banner for it is switched off, so the next
    # message shows once (in the island), not twice.
    $showBanner = (Get-ItemProperty -Path $bannerKey -Name ShowBanner -ErrorAction SilentlyContinue).ShowBanner
    Write-Host "Windows banner switch for the test app after the first message: ShowBanner=$showBanner"
    if ($showBanner -ne 0) { throw "The island did not take over the app's Windows banner (ShowBanner=$showBanner)" }
    Move-Away
    Start-Sleep -Seconds 7
    Send-Toast "Budi" "Nanti malam main badminton?"
    if (Try-FindByName "Budi" 8) {
        Start-Sleep -Milliseconds 700
        Save-Shot "22c-notification-second"
        Save-CornerShot "22d-windows-corner-second"
        Write-Host "Second notification: shown in the island"
    } else {
        Write-Host "Second notification: not shown in the island"
        Show-NotificationDiagnostics
    }
} else {
    Save-Shot "22-notification"
    Write-Host "Notification: not shown in the island; diagnostics:"
    Show-NotificationDiagnostics
}
Move-Away
Start-Sleep -Seconds 6
Assert-Running

# ── Settings window: what used to be the long right-click menu; set a city for the weather ─────
Open-Settings $islandY
Save-CenterShot "26-settings" 540 700
(Find-ByName "Kota untuk cuaca").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("Jakarta")
Close-Settings   # the city applies when the window closes
$settingsJson = Get-Content (Join-Path $dataDir "settings.json") -Raw | ConvertFrom-Json
if ($settingsJson.WeatherCity -ne "Jakarta") { throw "The weather city was not saved: '$($settingsJson.WeatherCity)'" }
Move-Away
Start-Sleep -Milliseconds 1500

# ── Calendar: paste an iCal feed in the dialog, then look at the agenda ────────────────────────
$now = Get-Date
function Ics-Time($date) { $date.ToString("yyyyMMdd'T'HHmmss") }
$yogaDay = @("SU", "MO", "TU", "WE", "TH", "FR", "SA")[[int]$now.AddDays(2).DayOfWeek]
$calendarFile = Join-Path $files "agenda.ics"
@"
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Dynamic Island//Smoke test//ID
BEGIN:VEVENT
UID:standup@test
DTSTART:$(Ics-Time $now.AddMinutes(-20))
DTEND:$(Ics-Time $now.AddMinutes(25))
SUMMARY:Rapat tim mingguan
END:VEVENT
BEGIN:VEVENT
UID:lunch@test
DTSTART:$(Ics-Time $now.AddHours(2))
DTEND:$(Ics-Time $now.AddHours(3))
SUMMARY:Makan siang bareng Rina
END:VEVENT
BEGIN:VEVENT
UID:birthday@test
DTSTART;VALUE=DATE:$($now.AddDays(1).ToString("yyyyMMdd"))
DTEND;VALUE=DATE:$($now.AddDays(2).ToString("yyyyMMdd"))
SUMMARY:Ulang tahun Budi
END:VEVENT
BEGIN:VEVENT
UID:yoga@test
DTSTART:$(Ics-Time $now.Date.AddDays(-19).AddHours(19))
DURATION:PT1H
RRULE:FREQ=WEEKLY;BYDAY=$yogaDay
SUMMARY:Kelas yoga
END:VEVENT
END:VCALENDAR
"@ | Set-Content -Path $calendarFile -Encoding UTF8

[SmokeInput]::RightClick($centerX, $islandY)
Start-Sleep -Milliseconds 1000
Invoke-ByName ("Kalender (agenda)" + [char]0x2026)   # the script is read as ANSI by Windows PowerShell: no literal ellipsis
Start-Sleep -Milliseconds 1500
$box = Find-ByName "Link iCal"
$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($calendarFile)
Start-Sleep -Milliseconds 500
Save-CenterShot "23-calendar-dialog" 620 560
Invoke-ByName "Simpan"
Start-Sleep -Milliseconds 1500
$settingsJson = Get-Content (Join-Path $dataDir "settings.json") -Raw | ConvertFrom-Json
if ($settingsJson.CalendarSource -ne $calendarFile) { throw "Calendar link was not saved: '$($settingsJson.CalendarSource)'" }

# Open the island and scroll to "today": music, lyrics, timer, today.
[SmokeInput]::SetCursorPos($centerX, $islandY) | Out-Null
Start-Sleep -Milliseconds 1500
foreach ($i in 1..3) { [SmokeInput]::Wheel(-120); Start-Sleep -Milliseconds 900 }
Start-Sleep -Milliseconds 800
Assert-Running
Save-Shot "24-today-agenda"
if (-not (Try-FindByName "Rapat tim mingguan")) { throw "The agenda does not show the calendar's current event" }
Write-Host "Agenda: shows the calendar"
if (Try-FindByName "Cuaca tidak bisa dimuat" 1) { Write-Host "Weather: could not be loaded on this runner" } else { Write-Host "Weather: shown" }
Move-Away
Start-Sleep -Milliseconds 1500
if ($sound) { $sound.Stop() }

# ── Call pill: an app records from the microphone, as Windows' privacy store reports it ─────────
$callStart = [DateTime]::UtcNow.AddSeconds(-125).ToFileTimeUtc()
Set-CallTimes $callStart 0
Start-Sleep -Milliseconds 4500   # first the "microphone on" note, then the pill
Save-Shot "29-call-pill"
if (-not (Try-FindByName "Zoom" 3)) { throw "The call pill does not show the app using the microphone" }
[SmokeInput]::SetCursorPos($centerX, $islandY) | Out-Null
Start-Sleep -Milliseconds 1500
Save-Shot "30-call-page"
try {
    Invoke-ByName "Bisukan mikrofon"
    Start-Sleep -Milliseconds 900
    Save-Shot "30b-call-muted"
    Invoke-ByName "Bisukan mikrofon"   # and unmute again
} catch {
    Write-Host "Microphone mute not available on this runner: $_"
}
Move-Away
Set-CallTimes $callStart ([DateTime]::UtcNow.ToFileTimeUtc())
Start-Sleep -Milliseconds 2000
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($callKey, $false)

# ── Screenshot preview: Win+PrtSc saves the screen to Pictures\Screenshots ─────────────────────
[SmokeInput]::Chord(0x5B, 0x2C)
if (-not (Try-FindByName "Screenshot" 6)) {
    Write-Host "Win+PrtSc saved nothing on this runner; dropping a picture into the Screenshots folder instead"
    $screenshots = Join-Path ([Environment]::GetFolderPath("MyPictures")) "Screenshots"
    Copy-Item $cover (Join-Path $screenshots "Screenshot test.png")
    if (-not (Try-FindByName "Screenshot" 6)) { throw "No screenshot preview" }
}
Start-Sleep -Milliseconds 600
Save-Shot "31-screenshot"
Move-Away
Start-Sleep -Seconds 7

# ── Finished download: like a browser, write "x.crdownload" and rename it at the end ───────────
$downloads = (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path
$partial = Join-Path $downloads "Laporan keuangan.pdf.crdownload"
[IO.File]::WriteAllText($partial, "%PDF-1.4`n% test download`n" + ("x" * 4000))
Start-Sleep -Milliseconds 500
Rename-Item $partial "Laporan keuangan.pdf"
$downloaded = Join-Path $downloads "Laporan keuangan.pdf"
if (-not (Try-FindByName "Unduhan selesai" 8)) { throw "The finished download was not shown" }
Start-Sleep -Milliseconds 500
Save-Shot "32-download"
Invoke-ByName "Titipkan"
Start-Sleep -Milliseconds 1000
$shelf = Get-Content (Join-Path $dataDir "shelf.json") -Raw | ConvertFrom-Json
if (-not ($shelf -contains $downloaded)) { throw "'Titipkan' did not put the download on the shelf" }
Write-Host "Download: shown and kept on the shelf"
Move-Away
Start-Sleep -Milliseconds 1500

# Switch to the notch style in the settings window.
Open-Settings $islandY
Select-ByName "Notch (menempel)"
Close-Settings
Move-Away
Start-Sleep -Milliseconds 1500
Save-Shot "16-notch-idle"
[SmokeInput]::SetCursorPos($centerX, 14) | Out-Null
Start-Sleep -Milliseconds 1500
Save-Shot "17-notch-today"

# "Don't cover windows": the top strip is reserved, so a maximised window starts below the island.
Move-Away
Start-Sleep -Milliseconds 800
Open-Settings 14
Toggle-ByName "Jangan tutupi jendela (sisakan ruang di atas)"
Close-Settings
Move-Away
Start-Sleep -Milliseconds 1500
$workTop = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Top
Write-Host "Work area top with the strip reserved: $workTop px"
if ($workTop -lt 25) { throw "Reserving space did not move the work area down (top = $workTop)" }
$window = New-Object System.Windows.Forms.Form
$window.Text = "Jendela yang di-maximize"
$window.BackColor = [System.Drawing.Color]::FromArgb(255, 245, 245, 247)
$window.WindowState = "Maximized"
$window.Show()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 1200
[System.Windows.Forms.Application]::DoEvents()
Write-Host "Maximised window starts at y = $($window.Top) px"
Save-Shot "25-reserved-space" 760 120
$window.Close()
Open-Settings 14
Toggle-ByName "Jangan tutupi jendela (sisakan ruang di atas)"
Close-Settings
Move-Away
Start-Sleep -Milliseconds 1500
$workTop = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Top
Write-Host "Work area top after giving the strip back: $workTop px"
if ($workTop -ne 0) { throw "The reserved strip was not given back (top = $workTop)" }

# Exit through the menu and make sure the process really ends.
[SmokeInput]::RightClick($centerX, 14)
Start-Sleep -Milliseconds 1000
Invoke-ByName "Keluar"
if (-not $script:process.WaitForExit(10000)) { throw "Dynamic Island did not exit after 'Keluar'" }
Write-Host "Exited with code $($script:process.ExitCode)"

# Closing the island gives Windows its banners back.
$showBanner = (Get-ItemProperty -Path $bannerKey -Name ShowBanner -ErrorAction SilentlyContinue).ShowBanner
Write-Host "Windows banner switch after exit: '$showBanner'"
if ($showBanner -eq 0) { throw "Windows' banner was not switched back on when the island closed" }
if ($player) { $player.Dispose() }

$log = Join-Path $dataDir "error.log"
if (Test-Path $log) {
    Write-Host "::group::error.log"
    Get-Content $log | Write-Host
    Write-Host "::endgroup::"
} else {
    Write-Host "No error.log written."
}
