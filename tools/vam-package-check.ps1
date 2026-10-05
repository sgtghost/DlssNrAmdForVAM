param([string]$Label = 'test', [string]$VamDir)
# Launches VaM windowed, waits for its package refresh, reports package-related log lines, closes VaM.
# By default this repository folder sits inside the VaM folder; -VamDir points elsewhere.
$ErrorActionPreference = 'Stop'
$root = if ($VamDir) { $VamDir } else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$ol = "$env:USERPROFILE\AppData\LocalLow\MeshedVR\VaM\output_log.txt"
function ReadShared([string]$path) { $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs); $t = $sr.ReadToEnd(); $sr.Close(); $t }
$regKey = 'HKCU:\Software\MeshedVR\VaM'
$saved = @{}; (Get-ItemProperty $regKey).PSObject.Properties | Where-Object { $_.Name -like 'Screenmanager*' } | ForEach-Object { $saved[$_.Name] = $_.Value }
# pkgdiag goes through command.txt, which the plugin only reads with [Debug] TestCommands on.
$cfg = "$root\BepInEx\config\local.dlssnramd.cfg"
$cfgOriginal = [IO.File]::ReadAllText($cfg)
if ($cfgOriginal -match '(?m)^TestCommands = .*$') { $c = [regex]::Replace($cfgOriginal, '(?m)^TestCommands = .*$', 'TestCommands = true') } else { $c = $cfgOriginal.TrimEnd() + "`r`n`r`n[Debug]`r`n`r`nTestCommands = true`r`n" }
[IO.File]::WriteAllText($cfg, $c)
$t0 = Get-Date
$p = Start-Process -FilePath "$root\VaM.exe" -ArgumentList '-vrmode', 'None', '-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0' -WorkingDirectory $root -PassThru
try {
    $done = $false
    while (((Get-Date) - $t0).TotalSeconds -lt 150 -and -not $p.HasExited) {
        Start-Sleep 3
        if ((Get-Item $ol).LastWriteTime -lt $t0) { continue }
        if ((ReadShared $ol) -match 'Refresh package handlers took') { $done = $true; break }
    }
    Start-Sleep 15
    "[$Label] package refresh finished: $done after $([int]((Get-Date) - $t0).TotalSeconds) s"
    $cmd = "$root\BepInEx\plugins\DlssNrAmd\command.txt"
    if (Test-Path "$root\BepInEx\plugins\DlssNrAmd\DlssNrAmd.dll") {
        [IO.File]::WriteAllText($cmd, 'pkgdiag')
        $w = Get-Date
        while (((Get-Date) - $w).TotalSeconds -lt 45) { Start-Sleep 2; if ((ReadShared "$root\BepInEx\LogOutput.log") -match 'registered uids \(first') { break } }
        if (Test-Path $cmd) { Remove-Item $cmd }
    }
} finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(15000)) { Stop-Process -Id $p.Id -Force } ; Start-Sleep 3 }
    foreach ($k in $saved.Keys) { Set-ItemProperty -Path $regKey -Name $k -Value $saved[$k] }
    [IO.File]::WriteAllText($cfg, $cfgOriginal)
}
$log = (ReadShared $ol) -split "`n"
"[$Label] BepInEx active: " + [bool]($log -match 'Chainloader startup complete')
"[$Label] package refresh exception lines: " + @($log | Where-Object { $_ -match 'Exception during package refresh' }).Count
"[$Label] 'does not exist' lines: " + @($log | Where-Object { $_ -match 'does not exist' }).Count
$log | Where-Object { $_ -match 'Exception during package refresh|does not exist|Refresh package handlers|Duplicate|duplicate|Invalid key|key file' } | Select-Object -First 12 | ForEach-Object { "   " + $_.Trim() }
$bep = "$root\BepInEx\LogOutput.log"
if (Test-Path $bep) { (ReadShared $bep) -split "`n" | Where-Object { $_ -match 'pkgdiag' } | Select-Object -First 6 | ForEach-Object { "   " + $_.Trim() } }
