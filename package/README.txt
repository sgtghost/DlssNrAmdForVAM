DLSS-NR on AMD for VaM 0.3.1 (personal build)
=============================================

Runs Virt-A-Mate's main camera (desktop mode) through AMD FSR 3.1 on a private Direct3D 12 device, so
the DLSS-NR-on-AMD mod can run its neural rendering on VaM's frames. The scene renders below screen
resolution and FSR upscales it; VaM's own menus are drawn on top at full resolution.

Requirements
  - VaM 1.22 on Direct3D 11 (the default), desktop mode. VR is not supported.
  - BepInEx 5 for VaM.
  - DLSS-NR-on-AMD 0.6.0 (version.dll and dlssnr_on_amd.ini next to VaM.exe).
    Requirements and download: https://github.com/danielblnc/DLSS-NR-on-AMD
  - MSAA off in VaM's settings: FSR does the anti-aliasing.

Install: copy into the VaM folder
  BepInEx\patchers\DlssNrAmd.Preloader.dll
  BepInEx\plugins\DlssNrAmd\DlssNrAmd.dll
  VaM_Data\Plugins\DlssNrAmdNative.dll
  VaM_Data\Plugins\DlssNrAmd\amd_fidelityfx_loader_dx12.dll
  VaM_Data\Plugins\DlssNrAmd\amd_fidelityfx_upscaler_dx12.dll
  VaM_Data\Managed\Mono.Cecil.dll   0.10.4, replacing VaM's 0.9.6 so BepInEx can run the preloader
                                    patcher. Back up the original first.

Use
  Ctrl+Shift+N  effect on / off (off after install, then remembered between launches; the first
                switch-on in a VaM session takes about 10 seconds)
  Ctrl+Shift+Q  render resolution: Native, Quality (1/1.5), Balanced (1/1.7), Performance (1/2),
                Ultra Performance (1/3)
  Ctrl+Shift+P  panel: the above, upscaler (FSR 3.1 or 4.1), exposure, texture sharpening, motion
                vectors, and DLSS-NR's own settings (neural rendering, tone, structure, skin structure,
                fast / reference), which are written to dlssnr_on_amd.ini and picked up by the mod while
                it runs
  All settings, hotkeys included (ToggleKey, QualityKey, PanelKey): BepInEx\config\local.dlssnramd.cfg.
  Pick hotkeys that other software does not take first: AMD Software, for example, uses Ctrl+Shift+O
  (performance overlay) and Ctrl+Shift with S, I, J, U, E, C, M, G and L.

Measured on an RX 7800 XT at 3840x2160, one character: effect off 184 fps; Native 5.3, Quality 11.5,
Balanced 13.9, Performance 18.8, Ultra Performance 30.1 fps. The network's cost follows the render
resolution.

Notes
  - VaM produces no motion vectors for skin, clothing or simulated hair; the plugin draws them, so
    moving characters do not smear. Hair is an approximation built from the guide strands.
  - FSR 4.1 (machine learning) is better in motion. On an RX 7800 XT at 4K it costs 3-4 ms more per
    frame: about 5 % fewer fps with neural rendering on, 185 -> 114 fps with FSR alone (Performance,
    one character).
  - On pure black backgrounds DLSS-NR's Structure lifts the black around characters into a faint halo
    (obvious with Windows Auto HDR). Structure 0 with Skin structure 1 (panel) removes it and keeps the
    skin detail; clothes and surroundings then get no structure enhancement.
  - VaM's screenshot and save-thumbnail cameras are left as they are.
  - Scene-authored UI on layer 0 goes through the upscaler with the scene.

Licences
  This plugin: MIT (LICENSE.txt). Third-party components, all MIT (AMD FidelityFX SDK DLLs and
  headers, Unity plugin headers, Mono.Cecil 0.10.4): THIRD_PARTY_NOTICES.txt.
  Source: https://github.com/sgtghost/DlssNrAmdForVAM
