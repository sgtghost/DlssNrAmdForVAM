# Builds the native (llvm-mingw clang++) and managed (csc v3.5) halves into dist\; -Deploy copies them into VaM,
# -Package zips them (VaM-relative paths, READMEs, licences) into dist\DlssNrAmd-<version>.zip.
# The managed half compiles against VaM's and BepInEx's own DLLs, so it needs a VaM install: by default this
# folder sits inside the VaM folder; -VamDir points elsewhere.
param([switch]$Deploy, [switch]$Package, [string]$VamDir, [string]$Cxx)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$vam = if ($VamDir) { $VamDir } else { Split-Path -Parent $root }
# llvm-mingw's clang++ (UCRT, x86_64): -Cxx, else the DLSSNRAMD_CXX environment variable, else clang++ on PATH.
$cxx = if ($Cxx) { $Cxx } elseif ($env:DLSSNRAMD_CXX) { $env:DLSSNRAMD_CXX } else { 'clang++' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$dist = Join-Path $root 'dist'
$tp = Join-Path $root 'native\third_party'
$ffxDlls = @('amd_fidelityfx_loader_dx12.dll', 'amd_fidelityfx_upscaler_dx12.dll')
if (-not (Test-Path (Join-Path $vam 'VaM_Data\Managed\Assembly-CSharp.dll'))) {
    throw "No VaM install at '$vam': put this folder inside the VaM folder or pass -VamDir"
}
if (-not (Get-Command $cxx -ErrorAction SilentlyContinue)) {
    throw "clang++ from llvm-mingw (UCRT, x86_64) not found: pass -Cxx or set DLSSNRAMD_CXX"
}
if (-not (Test-Path $csc)) { throw "C# 3.5 compiler not found ($csc): turn on .NET Framework 3.5 in Windows Features" }
if (($Deploy -or $Package) -and @($ffxDlls | Where-Object { -not (Test-Path (Join-Path $root "redist\ffx\$_")) }).Count -gt 0) {
    throw "redist\ffx needs $($ffxDlls -join ' and ') from the AMD FidelityFX SDK 2.3"
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $cxx -std=c++20 -O2 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers `
    -DWIDL_EXPLICIT_AGGREGATE_RETURNS -DUNICODE -D_UNICODE -DNOMINMAX -static -shared `
    -I "$tp\unity" -I "$tp\ffx\api\include" -I "$tp\ffx\upscalers\include" `
    -o (Join-Path $dist 'DlssNrAmdNative.dll') (Join-Path $root 'native\src\plugin.cpp') (Join-Path $root 'native\src\bridge.cpp') `
    (Join-Path $root 'native\src\dump.cpp') (Join-Path $root 'native\src\motion.cpp')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$m = Join-Path $vam 'VaM_Data\Managed'
& $csc /nologo /target:library /optimize "/out:$(Join-Path $dist 'DlssNrAmd.dll')" `
    "/r:$(Join-Path $vam 'BepInEx\core\BepInEx.dll')" "/r:$m\UnityEngine.dll" "/r:$m\UnityEngine.CoreModule.dll" `
    "/r:$m\Assembly-CSharp.dll" "/r:$m\UnityEngine.ScreenCaptureModule.dll" "/r:$m\UnityEngine.UI.dll" "/r:$m\UnityEngine.UIModule.dll" `
    "/r:$m\UnityEngine.IMGUIModule.dll" "/r:$m\UnityEngine.TextRenderingModule.dll" `
    (Join-Path $root 'managed\*.cs')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$core = Join-Path $vam 'BepInEx\core'
& $csc /nologo /target:library /optimize "/out:$(Join-Path $dist 'DlssNrAmd.Preloader.dll')" `
    "/r:$core\BepInEx.dll" "/r:$core\Mono.Cecil.dll" (Join-Path $root 'managed\preloader\*.cs')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if ($Deploy) {
    if (Get-Process VaM -ErrorAction SilentlyContinue) { throw 'VaM is running; close it before deploying.' }
    Copy-Item (Join-Path $dist 'DlssNrAmdNative.dll') (Join-Path $vam 'VaM_Data\Plugins\DlssNrAmdNative.dll') -Force
    $pd = Join-Path $vam 'BepInEx\plugins\DlssNrAmd'
    New-Item -ItemType Directory -Force -Path $pd | Out-Null
    Copy-Item (Join-Path $dist 'DlssNrAmd.dll') (Join-Path $pd 'DlssNrAmd.dll') -Force
    # Native AMD DLLs must stay out of BepInEx\plugins (BepInEx would try to load them as plugins).
    $ffx = Join-Path $vam 'VaM_Data\Plugins\DlssNrAmd'
    New-Item -ItemType Directory -Force -Path $ffx | Out-Null
    Copy-Item (Join-Path $root 'redist\ffx\*.dll') $ffx -Force
    $old = Join-Path $pd 'ffx'
    if (Test-Path $old) { Remove-Item -Recurse -Force $old }
    Copy-Item (Join-Path $dist 'DlssNrAmd.Preloader.dll') (Join-Path $vam 'BepInEx\patchers\DlssNrAmd.Preloader.dll') -Force
    'deployed'
}

if ($Package) {
    $src = [IO.File]::ReadAllText((Join-Path $root 'managed\DlssNrAmdPlugin.cs'))
    $ver = [regex]::Match($src, 'BepInPlugin\("[^"]+", "[^"]+", "([^"]+)"\)').Groups[1].Value
    $cecil = Join-Path $vam 'VaM_Data\Managed\Mono.Cecil.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($cecil).Version.Major -ne 0 -or [Reflection.AssemblyName]::GetAssemblyName($cecil).Version.Minor -lt 10) {
        throw "VaM_Data\Managed\Mono.Cecil.dll is not 0.10.x; the package needs the 0.10.4 replacement"
    }
    $stage = Join-Path $dist 'package'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $files = [ordered]@{
        'BepInEx\patchers'           = @(Join-Path $dist 'DlssNrAmd.Preloader.dll')
        'BepInEx\plugins\DlssNrAmd'  = @(Join-Path $dist 'DlssNrAmd.dll')
        'VaM_Data\Plugins'           = @(Join-Path $dist 'DlssNrAmdNative.dll')
        'VaM_Data\Plugins\DlssNrAmd' = @(Get-ChildItem (Join-Path $root 'redist\ffx\*.dll') | ForEach-Object { $_.FullName })
        'VaM_Data\Managed'           = @($cecil)
    }
    foreach ($k in $files.Keys) {
        $d = Join-Path $stage $k
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        Copy-Item $files[$k] $d
    }
    Copy-Item (Join-Path $root 'package\README*.txt') $stage  # English and Simplified Chinese
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
    Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.txt') $stage
    $zip = Join-Path $dist "DlssNrAmd-$ver.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Remove-Item -Recurse -Force $stage
    "packaged $zip"
}
