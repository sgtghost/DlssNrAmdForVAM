# DLSS-NR on AMD for VaM

A BepInEx plugin for Virt-A-Mate 1.22 (desktop mode) that runs VaM's main camera through AMD FSR 3.1 on
a private Direct3D 12 device, so that the [DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD)
mod can apply its neural rendering to VaM's frames. The scene renders below screen resolution and FSR
upscales it; VaM's own menus are drawn on top at full resolution.

A personal project, not affiliated with AMD, NVIDIA or Meshed VR. The DLSS-NR-on-AMD mod is not included.

中文说明: [package/README_zh-CN.txt](package/README_zh-CN.txt)

## Requirements

- VaM 1.22 on Direct3D 11 (the default), desktop mode. VR is not supported.
- BepInEx 5 for VaM.
- DLSS-NR-on-AMD 0.6.0 (`version.dll` and `dlssnr_on_amd.ini` next to `VaM.exe`). Without it the plugin
  is a plain FSR 3.1 upscaler.
- MSAA off in VaM's settings: FSR does the anti-aliasing.

## Install

Download the zip from [Releases](https://github.com/sgtghost/DlssNrAmdForVAM/releases) and copy its
contents into the VaM folder:

```
BepInEx\patchers\DlssNrAmd.Preloader.dll
BepInEx\plugins\DlssNrAmd\DlssNrAmd.dll
VaM_Data\Plugins\DlssNrAmdNative.dll
VaM_Data\Plugins\DlssNrAmd\amd_fidelityfx_loader_dx12.dll
VaM_Data\Plugins\DlssNrAmd\amd_fidelityfx_upscaler_dx12.dll
VaM_Data\Managed\Mono.Cecil.dll
```

The package replaces VaM's Mono.Cecil 0.9.6 with 0.10.4 so that BepInEx can run the preloader patcher;
back up the original first.

## Use

| Hotkey | |
|---|---|
| Ctrl+Shift+N | Effect on / off. Off after install, then remembered between launches; the first switch-on in a VaM session takes about 10 seconds. |
| Ctrl+Shift+Q | Render resolution: Native, Quality (1/1.5), Balanced (1/1.7), Performance (1/2), Ultra Performance (1/3). |
| Ctrl+Shift+P | Panel: the above, upscaler (FSR 3.1 or 4.1), exposure, texture sharpening, motion vectors, and DLSS-NR's own settings (neural rendering, tone, structure, skin structure, fast / reference), which are written to `dlssnr_on_amd.ini` and picked up by the mod while it runs. |

All settings, hotkeys included, are in `BepInEx\config\local.dlssnramd.cfg`. AMD Software takes
Ctrl+Shift+O (performance overlay) and Ctrl+Shift with S, I, J, U, E, C, M, G and L for itself.

FSR 4.1 (machine learning) is better in motion. On an RX 7800 XT at 4K it costs 3-4 ms more per frame:
about 5 % fewer fps with neural rendering on, 185 -> 114 fps with FSR alone (Performance, one character).

On pure black backgrounds DLSS-NR's Structure lifts the black around characters into a faint halo
(obvious with Windows Auto HDR). Structure 0 with Skin structure 1 removes it and keeps the skin detail;
clothes and surroundings then get no structure enhancement.

Measured on an RX 7800 XT at 3840x2160 with one character: effect off 184 fps; Native 5.3, Quality 11.5,
Balanced 13.9, Performance 18.8, Ultra Performance 30.1 fps. The network's cost follows the render
resolution.

## How it works

- The DLSS-NR mod hooks FSR 3.1 only on a Direct3D 12 device that presents a swapchain; VaM is a
  Direct3D 11 Unity 2018 game without FSR. The native plugin (`native/`) creates a D3D12 device on VaM's
  GPU with a hidden swapchain it presents every frame, shares textures and a fence with Unity's D3D11
  device, and dispatches FSR 3.1.5 there; the mod intercepts that dispatch and runs its network.
- The managed plugin (`managed/`) renders VaM's main camera into a render-size target with a Halton
  jitter, captures colour, depth and motion vectors after the camera's image effects, and hands them to
  the native side in a render event. A presenter camera draws the upscaled result and then VaM's
  world-space interface at display size.
- VaM skins characters and simulates hair in compute shaders, so Unity has no motion vectors for them.
  The plugin redraws skin and clothing from VaM's own position buffers, and hair as prisms between its
  guide strands, against a copy of the previous frame's positions; only fragments whose depth matches
  the scene are kept, and their motion replaces the camera-only motion.
- Texture mip bias is lowered while upscaling; FSR's history resets on resizes, preset changes and scene
  loads.

## Build

- llvm-mingw (UCRT, x86_64) for the native DLL: pass `-Cxx path\to\clang++.exe`, set `DLSSNRAMD_CXX`,
  or have `clang++` on PATH.
- The .NET Framework 3.5 C# compiler (Windows feature ".NET Framework 3.5").
- A VaM install with BepInEx: the managed code compiles against VaM's and BepInEx's DLLs, which are not
  in this repository. Clone this repository into the VaM folder, or pass `-VamDir`.
- For `-Deploy` and `-Package`: `amd_fidelityfx_loader_dx12.dll` and `amd_fidelityfx_upscaler_dx12.dll`
  from the [AMD FSR SDK 2.3.0](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v2.3.0)
  package, in `redist\ffx`.

```
.\build.ps1             # build into dist\
.\build.ps1 -Deploy     # build and install into VaM (VaM must be closed)
.\build.ps1 -Package    # build and zip dist\DlssNrAmd-<version>.zip
```

`tools\` holds the scripts used for testing: they start VaM and drive it through the plugin's
`command.txt`, which is only read with `[Debug] TestCommands = true`.

## License

MIT, see [LICENSE](LICENSE). Third-party components: [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
