param([string]$Scene, [string]$OutDir = (Join-Path $env:TEMP 'dlssnramd-test'),
      [string[]]$Steps = @('A_nr_on', 'B_fsr_only', 'C_moving', 'D_exposure2'),
      [string[]]$VamArgs = @('-vrmode', 'None', '-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0'),
      [switch]$StartOff, [string]$VamDir, [string]$FsrVersion)
# Scenario driver: starts VaM with -Scene (path relative to the VaM folder, e.g. Saves/scene/x.json) loaded,
# runs the steps through the plugin's command.txt and in-game captures, restores the settings it touched and
# closes VaM. By default this repository folder sits inside the VaM folder; -VamDir points elsewhere. Steps:
#   A_nr_on      dump with DLSS-NR on (exposure 1)
#   B_fsr_only   dump with the mod's Enabled=0 (FSR only)
#   C_moving     dump two frames while orbiting the camera (right-drag)
#   D_exposure2  dump with exposure 2
$ErrorActionPreference = 'Stop'
# powershell -File passes list arguments as one comma-joined string.
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$VamArgs = @($VamArgs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Scene) { throw '-Scene is required: a scene path relative to the VaM folder, e.g. Saves/scene/x.json' }
$root = if ($VamDir) { $VamDir } else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$bep = "$root\BepInEx\LogOutput.log"
$ini = "$root\dlssnr_on_amd.ini"
$cfg = "$root\BepInEx\config\local.dlssnramd.cfg"
$pdir = "$root\BepInEx\plugins\DlssNrAmd"
$dumpDir = "$pdir\dump"
$cmd = "$pdir\command.txt"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Recurse -File | Remove-Item
if (Test-Path $dumpDir) { Remove-Item -Recurse -Force $dumpDir }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W32c {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@
[void][W32c]::SetProcessDPIAware()

function ReadShared([string]$path) { $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs); $t = $sr.ReadToEnd(); $sr.Close(); $t }
function Command([string]$text) { [IO.File]::WriteAllText($cmd, $text); $t = Get-Date; while ((Test-Path $cmd) -and ((Get-Date) - $t).TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 } }
function Shot($p, [string]$name) {
    [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep -Milliseconds 400
    $r = New-Object W32c+RECT; [void][W32c]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T; $bmp = New-Object Drawing.Bitmap $w, $h; $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object Drawing.Size $w, $h)); $bmp.Save("$OutDir\$name.png")
}
# Asks the plugin where a UI text is; returns client coordinates (origin top left) or $null.
function UiPos([string]$text) {
    $key = "ui position of '" + [regex]::Escape($text) + "': ([^\r\n]*)"
    $n = ([regex]::Matches((ReadShared $bep), $key)).Count
    Command "uipos $text"
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt 10) {
        $m = [regex]::Matches((ReadShared $bep), $key)
        if ($m.Count -gt $n) {
            $v = $m[$m.Count - 1].Groups[1].Value
            Write-Host "  uipos '$text': $v"
            if ($v -match '^(\d+) (\d+) of (\d+)x(\d+)') { return @{ X = [int]$Matches[1]; Y = [int]$Matches[4] - [int]$Matches[2] } }
            return $null
        }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "  uipos '$text': no answer"
    return $null
}
# A real left click at client coordinates (brings VaM to the front).
function Click($p, $pos) {
    [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep -Milliseconds 500
    $pt = New-Object W32c+POINT; $pt.X = $pos.X; $pt.Y = $pos.Y
    [void][W32c]::ClientToScreen($p.MainWindowHandle, [ref]$pt)
    [void][W32c]::SetCursorPos($pt.X, $pt.Y); Start-Sleep -Milliseconds 300
    [W32c]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80
    [W32c]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
    Write-Host "  clicked client $($pos.X),$($pos.Y) (screen $($pt.X),$($pt.Y))"
}
# A real key chord (virtual-key codes, pressed in order, released in reverse; brings VaM to the front).
function Chord($p, [byte[]]$vks) {
    [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep -Milliseconds 500
    foreach ($k in $vks) { [W32c]::keybd_event($k, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 400  # held across a few frames, also when VaM hitches while loading
    for ($i = $vks.Count - 1; $i -ge 0; $i--) { [W32c]::keybd_event($vks[$i], 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
}
function SaveDump([string]$name) {
    $t = Get-Date
    while (-not (Test-Path "$dumpDir\frame1\output.bmp") -and ((Get-Date) - $t).TotalSeconds -lt 15) { Start-Sleep -Milliseconds 200 }
    Start-Sleep 1
    if (-not (Test-Path "$dumpDir\frame1\output.bmp")) { "  dump ${name}: not written"; return }
    foreach ($fr in Get-ChildItem $dumpDir -Directory) {
        $d = "$OutDir\$name\$($fr.Name)"; New-Item -ItemType Directory -Force -Path $d | Out-Null
        Get-ChildItem $fr.FullName -Filter *.bmp | ForEach-Object { $img = [Drawing.Image]::FromFile($_.FullName); $img.Save((Join-Path $d ($_.BaseName + '.png')), [Drawing.Imaging.ImageFormat]::Png); $img.Dispose() }
        Get-ChildItem $fr.FullName -Filter *.raw | Copy-Item -Destination $d
    }
    Remove-Item -Recurse -Force $dumpDir
    "  dump ${name} saved"
}

$regKey = 'HKCU:\Software\MeshedVR\VaM'
$saved = @{}; (Get-ItemProperty $regKey).PSObject.Properties | Where-Object { $_.Name -like 'Screenmanager*' } | ForEach-Object { $saved[$_.Name] = $_.Value }
$iniOriginal = [IO.File]::ReadAllText($ini)
$cfgOriginal = [IO.File]::ReadAllText($cfg)
$c = $cfgOriginal
if ($c -match '(?m)^LoadSceneOnStart = .*$') { $c = [regex]::Replace($c, '(?m)^LoadSceneOnStart = .*$', "LoadSceneOnStart = $Scene") } else { $c = $c.TrimEnd() + "`r`n`r`n[Debug]`r`n`r`nLoadSceneOnStart = $Scene`r`n" }
# command.txt is only read with [Debug] TestCommands on (repeated section headers are fine for BepInEx).
if ($c -match '(?m)^TestCommands = .*$') { $c = [regex]::Replace($c, '(?m)^TestCommands = .*$', 'TestCommands = true') } else { $c = $c.TrimEnd() + "`r`n`r`n[Debug]`r`n`r`nTestCommands = true`r`n" }
# -FsrVersion: the FSR provider for this run (read at start-up), e.g. 4.1 or 3.1.
if ($FsrVersion) { $c = [regex]::Replace($c, '(?m)^FsrVersion = .*$', "FsrVersion = $FsrVersion") }
[IO.File]::WriteAllText($cfg, $c)

$t0 = Get-Date
$p = Start-Process -FilePath "$root\VaM.exe" -ArgumentList $VamArgs -WorkingDirectory $root -PassThru
try {
    $ok = $false
    while (((Get-Date) - $t0).TotalSeconds -lt 300 -and -not $p.HasExited) {
        Start-Sleep 3
        if ((Get-Item $bep).LastWriteTime -lt $t0) { continue }
        $txt = ReadShared $bep
        if ($txt -match 'scene loaded' -and ($StartOff -or $txt -match 'running \|')) { $ok = $true; break }
    }
    "ready: $ok after $([int]((Get-Date) - $t0).TotalSeconds) s"
    if ($ok) {
        Start-Sleep 8; $p.Refresh()
        foreach ($step in $Steps) {
            switch ($step) {
                'A_nr_on' { Shot $p $step; Command 'dump'; SaveDump $step }
                'B_fsr_only' {
                    [IO.File]::WriteAllText($ini, ($iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=0')); Start-Sleep 4
                    Shot $p $step; Command 'dump'; SaveDump $step
                    [IO.File]::WriteAllText($ini, $iniOriginal); Start-Sleep 4
                }
                'C_moving' {
                    [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep -Milliseconds 400
                    $r = New-Object W32c+RECT; [void][W32c]::GetWindowRect($p.MainWindowHandle, [ref]$r)
                    [void][W32c]::SetCursorPos([int](($r.L + $r.R) / 2), [int](($r.T + $r.B) / 2))
                    [W32c]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
                    for ($i = 0; $i -lt 60; $i++) {
                        [W32c]::mouse_event(0x0001, 5, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 25
                        if ($i -eq 25) { [IO.File]::WriteAllText($cmd, 'dump') }
                        if ($i -eq 40) { Shot $p $step }
                    }
                    [W32c]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
                    SaveDump $step
                }
                'D_exposure2' { Command 'exposure 2'; Start-Sleep 3; Shot $p $step; Command 'dump'; SaveDump $step; Command 'exposure 1'; Start-Sleep 2 }
                'E_move' { Command 'move 25 0.25 4'; Start-Sleep -Milliseconds 1200; Command 'dump'; Shot $p $step; SaveDump $step; Start-Sleep 3 }
                'F_fps' { Start-Sleep 16; Shot $p $step; Command 'disable'; Start-Sleep 16; Command 'enable'; Start-Sleep 16 }
                'G_turn_on' { Start-Sleep 8; Command 'enable'; Start-Sleep 25; Shot $p $step }
                'S_sweep' {
                    Command 'hud off'; Command 'enable'; Start-Sleep 10
                    foreach ($q in 'NativeAA', 'Quality', 'Balanced', 'Performance', 'UltraPerformance') { Command "quality $q"; Start-Sleep 16 }
                    Command 'quality Performance'; Command 'disable'; Start-Sleep 16; Command 'enable'; Start-Sleep 8
                }
                'H_hud' { Command 'hud'; Start-Sleep 4; Shot $p $step; Command 'hud'; Start-Sleep 2 }
                'V_inshots' {
                    # Captured by VaM itself, so window focus and other windows do not matter. Needs the effect on.
                    Command 'hud off'; Start-Sleep 3; Command "shot $OutDir\V_on.png"; Start-Sleep 3
                    Command 'hud on'; Start-Sleep 3; Command "shot $OutDir\V_on_hud.png"; Start-Sleep 3
                    Command 'disable'; Start-Sleep 4; Command "shot $OutDir\V_off_hud.png"; Start-Sleep 3
                    Command 'hud off'; Start-Sleep 3; Command "shot $OutDir\V_off.png"; Start-Sleep 3
                    Command 'enable'; Start-Sleep 6
                }
                'I_click' {
                    # Real mouse clicks: the bottom bar's "Toggle UI" and the main menu's Edit/Play buttons,
                    # with the effect on, then off. 'state' logs what VaM registered.
                    foreach ($s in 'on', 'off') {
                        if ($s -eq 'on') { Command 'enable' } else { Command 'disable' }
                        Command 'ui on'; Command 'hud on'; Start-Sleep 10; Command 'state'
                        $b = UiPos 'Toggle UI'
                        if ($b) {
                            Click $p $b; Start-Sleep 2; Command 'state'; Command "shot $OutDir\I_${s}_bar.png"; Start-Sleep 3
                            Click $p $b; Start-Sleep 2; Command 'state'
                        }
                        Command 'ui on'; Command 'hud on'; Start-Sleep 2
                        $e = UiPos 'Edit Mode'
                        if ($e) { Click $p $e; Start-Sleep 2; Command 'state'; Command "shot $OutDir\I_${s}_edit.png"; Start-Sleep 3 }
                        $q = UiPos 'Play Mode'
                        if ($q) { Click $p $q; Start-Sleep 2; Command 'state' }
                    }
                    Command 'hud off'; Command 'enable'; Start-Sleep 6
                }
                'K_pkg' { Command 'pkgdiag'; Start-Sleep 6 }
                'Y_hotkeys' {
                    # Real key presses: the panel (Ctrl+Shift+P) open and closed, the effect (Ctrl+Shift+N) on and off.
                    # Presses can be lost while VaM still hitches on texture loading or the focus change: wait first.
                    Start-Sleep 12; [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep 2
                    Chord $p @(0x11, 0x10, 0x50); Start-Sleep 3; Command "shot $OutDir\Y_panel.png"; Start-Sleep 3
                    Chord $p @(0x11, 0x10, 0x50); Start-Sleep 2
                    Chord $p @(0x11, 0x10, 0x4E); Start-Sleep 15
                    Chord $p @(0x11, 0x10, 0x4E); Start-Sleep 4
                }
                'Z_halo' {
                    # Ring around the character on a black background: the same view with one part switched at a time.
                    Command 'hud off'; Command 'enable'; Start-Sleep 15
                    $nrOn = $iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=1'
                    $noNr = $iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=0'
                    $variants = @(
                        @('nr_default', $nrOn, 'exposure 2', 'glow on'),
                        @('nr_structure0', ($nrOn -replace '(?m)^LocalStructure=.*$', 'LocalStructure=0'), 'exposure 2', 'glow on'),
                        @('fsr_only', $noNr, 'exposure 2', 'glow on'),
                        @('nr_exposure1', $nrOn, 'exposure 1', 'glow on'),
                        @('nr_noglow', $nrOn, 'exposure 2', 'glow off'),
                        @('fsr_noglow', $noNr, 'exposure 2', 'glow off'))
                    foreach ($v in $variants) {
                        [IO.File]::WriteAllText($ini, $v[1]); Command $v[2]; Command $v[3]; Start-Sleep 7
                        Command "shot $OutDir\Z_$($v[0]).png"; Start-Sleep 3
                    }
                    [IO.File]::WriteAllText($ini, $iniOriginal); Command 'exposure 2'; Command 'glow on'
                    Command 'disable'; Start-Sleep 5; Command "shot $OutDir\Z_off.png"; Start-Sleep 3
                    Command 'glow off'; Start-Sleep 3; Command "shot $OutDir\Z_off_noglow.png"; Start-Sleep 3
                    Command 'glow on'; Command 'enable'; Start-Sleep 3
                }
                'X_fsrcmp' {
                    # Upscaler comparison (run once per -FsrVersion): Performance and Quality, DLSS-NR on and off.
                    Command 'hud off'; Command 'enable'; Start-Sleep 15
                    $nrOn = $iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=1'
                    $noNr = $iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=0'
                    foreach ($q in 'Performance', 'Quality') {
                        foreach ($nr in 'on', 'off') {
                            [IO.File]::WriteAllText($ini, $(if ($nr -eq 'on') { $nrOn } else { $noNr }))
                            Command "quality $q"; Write-Host "  phase $q NR $nr"; Start-Sleep 17
                            Command "shot $OutDir\X_${q}_nr$nr.png"; Start-Sleep 3
                        }
                    }
                    [IO.File]::WriteAllText($ini, $iniOriginal); Command 'quality Performance'; Start-Sleep 3
                }
                'U_fsrswitch' {
                    # Upscaler switched while running: by command with DLSS-NR on, then by a real click in the panel with DLSS-NR off.
                    Start-Sleep 12; [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep 2
                    [IO.File]::WriteAllText($ini, ($iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=1'))
                    Command 'hud off'; Command 'fsr 3.1'; Command 'enable'; Start-Sleep 15
                    Command 'fsr 4.1'; Start-Sleep 14
                    Command 'fsr 3.1'; Start-Sleep 14
                    [IO.File]::WriteAllText($ini, ($iniOriginal -replace '(?m)^Enabled=.*$', 'Enabled=0')); Start-Sleep 8
                    Command 'panel on'; Start-Sleep 3
                    $n = ([regex]::Matches((ReadShared $bep), 'panel rects:')).Count
                    Command 'panelrects'; Start-Sleep 2
                    $m = [regex]::Matches((ReadShared $bep), 'panel rects: effect \d+ \d+ fsr (\d+) (\d+) (\d+)')
                    if ($m.Count -gt $n) {
                        $g = $m[$m.Count - 1].Groups
                        Click $p @{ X = [int]$g[1].Value + [int]([int]$g[3].Value * 0.75); Y = [int]$g[2].Value }; Start-Sleep 12
                        Command "shot $OutDir\U_panel.png"; Start-Sleep 3
                    } else { Write-Host '  panel rects: no answer' }
                    Command 'panel off'; Command 'fsr 3.1'; Start-Sleep 6
                }
                'Z_halo2' {
                    # Ring vs detail: DLSS-NR structure settings and exposure, one at a time.
                    Command 'hud off'; Command 'enable'; Start-Sleep 15
                    function NrIni([string]$structure, [string]$skin, [string]$enabled) {
                        $t = $iniOriginal -replace '(?m)^LocalStructure=.*$', "LocalStructure=$structure"
                        $t = $t -replace '(?m)^SkinStructure=.*$', "SkinStructure=$skin"
                        $t -replace '(?m)^Enabled=.*$', "Enabled=$enabled"
                    }
                    $variants = @(
                        @('nr_default', (NrIni '1' '-1' '1'), 'exposure 2'),
                        @('fsr_only', (NrIni '1' '-1' '0'), 'exposure 2'),
                        @('nr_struct05', (NrIni '0.5' '-1' '1'), 'exposure 2'),
                        @('nr_skinonly', (NrIni '0' '1' '1'), 'exposure 2'),
                        @('nr_skin_bg02', (NrIni '0.2' '1' '1'), 'exposure 2'),
                        @('nr_exposure3', (NrIni '1' '-1' '1'), 'exposure 3'))
                    foreach ($v in $variants) {
                        [IO.File]::WriteAllText($ini, $v[1]); Command $v[2]; Start-Sleep 8
                        Command "shot $OutDir\Z2_$($v[0]).png"; Start-Sleep 3
                    }
                    [IO.File]::WriteAllText($ini, $iniOriginal); Command 'exposure 2'; Start-Sleep 3
                }
                'Q_hairzoom' {
                    # Hair motion close up (turn and zoom values suit one test scene with simulated hair), hair motion on, then off.
                    Command 'hud off'; Command 'enable'; Command 'move 27 0 1'; Start-Sleep 2; Command 'fov 13'; Start-Sleep 15
                    Command "shot $OutDir\Q_view.png"; Start-Sleep 3
                    foreach ($hm in 'on', 'off') {
                        Command "hairmotion $hm"; Start-Sleep 2
                        Command 'spin 30 8'; Start-Sleep 2.5
                        Command 'dump'; Command "shot $OutDir\Q_$hm.png"; SaveDump "Q_$hm"
                        Start-Sleep 7
                    }
                    Command 'hairmotion on'; Command 'fov 0'; Start-Sleep 2
                }
                'H_hairmotion' {
                    # Hair motion: dumps and shots while the characters spin, hair motion on, then off (skin motion on).
                    Command 'hud off'; Command 'enable'; Start-Sleep 15
                    foreach ($hm in 'on', 'off') {
                        Command "hairmotion $hm"; Start-Sleep 2
                        Command 'spin 40 8'; Start-Sleep 2
                        Command 'dump'; Command "shot $OutDir\H_$hm.png"; SaveDump "H_$hm"
                        Start-Sleep 7
                    }
                    Command 'hairmotion on'; Start-Sleep 2
                }
                'P_panel' {
                    # In-game panel: open it; with the real mouse click "Effect on" twice and the DLSS-NR Tone slider at 75 %.
                    Command 'hud off'; Command 'enable'; Command 'panel on'; Start-Sleep 8
                    Command "shot $OutDir\P_panel.png"; Start-Sleep 3
                    $n = ([regex]::Matches((ReadShared $bep), 'panel rects:')).Count
                    Command 'panelrects'; Start-Sleep 2
                    $m = [regex]::Matches((ReadShared $bep), 'panel rects: effect (\d+) (\d+) tone (\d+) (\d+) (\d+)')
                    if ($m.Count -gt $n) {
                        $g = $m[$m.Count - 1].Groups
                        $eff = @{ X = [int]$g[1].Value; Y = [int]$g[2].Value }
                        $tone = @{ X = [int]$g[3].Value + [int]([int]$g[5].Value * 0.75); Y = [int]$g[4].Value }
                        Write-Host "  panel: effect toggle at $($eff.X),$($eff.Y); tone slider 75% at $($tone.X),$($tone.Y)"
                        Click $p $eff; Start-Sleep 7; Command "shot $OutDir\P_panel_off.png"; Start-Sleep 3
                        Click $p $eff; Start-Sleep 7
                        Click $p $tone; Start-Sleep 3
                        Write-Host ('  ini after the tone click: ' + ((([IO.File]::ReadAllText($ini)) -split "`r?`n") | Where-Object { $_ -match '^LocalTone' }))
                        Command "shot $OutDir\P_panel_tone.png"; Start-Sleep 3
                    } else { Write-Host '  panel rects: no answer' }
                    Command 'panel off'; Start-Sleep 2
                }
                'P_skin' {
                    # Panel: "Skin structure same as Structure" clicked twice with the real mouse; SkinStructure read back from the ini.
                    # Input right after loading can be lost (texture-loading hitches, focus change): wait first.
                    Start-Sleep 12; [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep 2
                    Command 'hud off'; Command 'panel on'; Start-Sleep 6
                    $n = ([regex]::Matches((ReadShared $bep), 'panel rects:')).Count
                    Command 'panelrects'; Start-Sleep 2
                    $m = [regex]::Matches((ReadShared $bep), 'panel rects: .* skinsame (\d+) (\d+)')
                    if ($m.Count -gt $n) {
                        $s = @{ X = [int]$m[$m.Count - 1].Groups[1].Value; Y = [int]$m[$m.Count - 1].Groups[2].Value }
                        Write-Host ('  before: ' + ((([IO.File]::ReadAllText($ini)) -split "`r?`n") | Where-Object { $_ -match '^(Local|Skin)Structure' }))
                        Click $p $s; Start-Sleep 2
                        Write-Host ('  after the 1st click: ' + ((([IO.File]::ReadAllText($ini)) -split "`r?`n") | Where-Object { $_ -match '^SkinStructure' }))
                        Command "shot $OutDir\P_skin.png"; Start-Sleep 3
                        Click $p $s; Start-Sleep 2
                        Write-Host ('  after the 2nd click: ' + ((([IO.File]::ReadAllText($ini)) -split "`r?`n") | Where-Object { $_ -match '^SkinStructure' }))
                    } else { Write-Host '  panel rects: no answer' }
                    Command 'panel off'; Start-Sleep 2
                }
                'R_off' {
                    # Control for R_robust: the same captures with the effect off.
                    Command 'hud off'; Command 'disable'; Start-Sleep 4
                    Command "vamshot $OutDir\R_off_vamshot.jpg"; Start-Sleep 5
                    Command 'save Saves/scene/_dlssnramd_test_off.json'; Start-Sleep 8
                }
                'R_robust' {
                    # Effect on: VaM's own screenshot, a scene save (thumbnail), then a scene reload.
                    Command 'hud off'; Command 'enable'; Start-Sleep 8
                    Command "vamshot $OutDir\R_vamshot.jpg"; Start-Sleep 5
                    Command 'save Saves/scene/_dlssnramd_test.json'; Start-Sleep 8
                    Command "shot $OutDir\R_after_save.png"; Start-Sleep 3
                    Command "load $Scene"; Start-Sleep 40
                    Command 'hud off'; Start-Sleep 3; Command "shot $OutDir\R_after_load.png"; Start-Sleep 3
                }
                'M_motion' {
                    # Object motion: dumps and shots while the character spins (camera still), object motion on, then off.
                    Command 'hud off'
                    foreach ($om in 'on', 'off') {
                        Command "objmotion $om"; Start-Sleep 2
                        Command 'spin 40 8'; Start-Sleep 2
                        Command 'dump'; Command "shot $OutDir\M_$om.png"; SaveDump "M_$om"
                        Start-Sleep 7
                    }
                    Command 'objmotion on'; Start-Sleep 2
                }
                'T_tone' {
                    # In-game shots with DLSS-NR on / off (FSR only) at exposure 1 and 2. Needs the effect on.
                    Command 'hud off'
                    foreach ($nr in 1, 0) {
                        [IO.File]::WriteAllText($ini, ($iniOriginal -replace '(?m)^Enabled=.*$', "Enabled=$nr")); Start-Sleep 4
                        foreach ($e in 1, 2) { Command "exposure $e"; Start-Sleep 5; Command "shot $OutDir\T_nr${nr}_e$e.png"; Start-Sleep 3 }
                    }
                    [IO.File]::WriteAllText($ini, $iniOriginal); Command 'exposure 2'; Start-Sleep 4
                    Command 'disable'; Start-Sleep 4; Command "shot $OutDir\T_off.png"; Start-Sleep 3; Command 'enable'; Start-Sleep 4
                }
            }
        }
        Start-Sleep 2
    }
} finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep 3 }
    [IO.File]::WriteAllText($ini, $iniOriginal)
    [IO.File]::WriteAllText($cfg, $cfgOriginal)
    if (Test-Path $cmd) { Remove-Item $cmd }
    foreach ($k in $saved.Keys) { Set-ItemProperty -Path $regKey -Name $k -Value $saved[$k] }
    "restored: ini $([IO.File]::ReadAllText($ini) -eq $iniOriginal), plugin cfg $([IO.File]::ReadAllText($cfg) -eq $cfgOriginal), registry " + (((Get-ItemProperty $regKey).PSObject.Properties | Where-Object { $_.Name -like 'Screenmanager*' } | ForEach-Object { "$($_.Value)" }) -join '/')
}
"===== plugin log"
Get-Content $bep | Where-Object { $_ -match 'DLSS-NR on AMD' -and $_ -notmatch 'dump D:|FSR provider|UnityPluginLoad|graphics device' } | Select-Object -Last 40
Get-Content $bep | Where-Object { $_ -match 'dump D:.*(motion|depth)' }
"===== mod log (exposure / motion lines, this session)"
$t = (ReadShared "$root\dlssnr_on_amd.log") -split "`n"
$i = 0; for ($k = $t.Count - 1; $k -ge 0; $k--) { if ($t[$k] -match 'loaded into VaM.exe') { $i = $k; break } }
$t[$i..($t.Count - 1)] | Where-Object { $_ -match 'exposure|Enabled ->|job \d+ motion' } | Select-Object -First 14
