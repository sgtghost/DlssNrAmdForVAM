// Managed half of the VaM "DLSS-NR on AMD" plugin (BepInEx 5, .NET 3.5, Unity 2018.1 / Direct3D 11).
// VaM's main camera renders below display size (CameraHook); FSR with DLSS-NR-on-AMD hooked into it
// upscales the finished frame on the native side.
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

[BepInPlugin("local.dlssnramd", "DLSS-NR on AMD for VaM", "0.3.1")]
public class DlssNrAmdPlugin : BaseUnityPlugin
{
    ConfigEntry<bool> _enabled;
    ConfigEntry<KeyboardShortcut> _toggleKey;
    ConfigEntry<UpscaleQuality> _quality;
    ConfigEntry<KeyboardShortcut> _qualityKey;
    ConfigEntry<KeyboardShortcut> _dumpKey;
    ConfigEntry<string> _fsrVersion;
    ConfigEntry<float> _exposure;
    ConfigEntry<float> _sharpening;
    ConfigEntry<bool> _objectMotion;
    ConfigEntry<KeyboardShortcut> _panelKey;
    Panel _panel;
    float _nextPanelStatus;
    string _lastFps = "";
    ConfigEntry<string> _loadScene;
    ConfigEntry<bool> _testCommands;
    string _dumpDir;
    string _commandFile;
    float _nextCommandCheck;
    float _moveYaw, _moveUp, _moveUntil;  // debug "move" command
    float _spinRate, _spinUntil;          // debug "spin" command
    float _avgDt = 0.016f;                // hitch diagnostics
    int _lastGcCount, _lastReportFrame = -1;
    DAZClothingItem _testClothing;        // debug "clothing" command
    float _fov;                           // debug "fov" command (0 = VaM's own)
    int _sceneStage;  // 0 not requested, 1 requested, 2 loading seen, 3 done
    CameraHook _hook;
    float _reportAt;
    float _reportStart;
    int _reportFrames;
    bool _failureLogged;

    void Awake()
    {
        _enabled = Config.Bind("General", "Enabled", false,
            "Run VaM's main camera through FSR and DLSS-NR-on-AMD. Off by default; Ctrl+Shift+N turns it on in game.");
        _toggleKey = Config.Bind("General", "ToggleKey",
            new KeyboardShortcut(KeyCode.N, KeyCode.LeftControl, KeyCode.LeftShift), "Turns the effect on and off in game.");
        _quality = Config.Bind("General", "Quality", UpscaleQuality.Performance,
            "Render resolution: the scene renders at the display size divided by 1 (NativeAA), 1.5 (Quality), 1.7 (Balanced), " +
            "2 (Performance) or 3 (UltraPerformance), and FSR upscales it. Lower resolutions are much faster: the network cost follows the pixel count.");
        _qualityKey = Config.Bind("General", "QualityKey",
            new KeyboardShortcut(KeyCode.Q, KeyCode.LeftControl, KeyCode.LeftShift), "Cycles the quality preset in game.");
        _fsrVersion = Config.Bind("Advanced", "FsrVersion", "3.1",
            "Upscaler, matched by version prefix: 3.1 = FSR 3.1.x (faster), 4.1 = FSR 4.1.x (machine learning, better in motion; " +
            "3-4 ms more per frame on an RX 7800 XT at 4K). Also in the panel; switches while running.");
        _exposure = Config.Bind("Advanced", "Exposure", 2f,
            "Exposure handed to FSR and DLSS-NR (1 = the frame as VaM renders it). Higher values brighten what the network sees; 2 looked most natural in testing.");
        _sharpening = Config.Bind("Advanced", "TextureSharpening", 1f,
            "Texture mip bias while upscaling, as a fraction of FSR's recommendation (log2(render/display) - 1). Lower it if fine textures shimmer; 0 turns it off.");
        _objectMotion = Config.Bind("Advanced", "ObjectMotion", true,
            "Motion vectors for VaM's GPU-skinned skin, clothing and simulated hair, so moving characters do not smear. Unity only describes the camera's movement over them.");
        CameraHook.Log = Logger;
        _dumpKey = Config.Bind("Advanced", "DumpKey",
            new KeyboardShortcut(KeyCode.D, KeyCode.LeftControl, KeyCode.LeftShift), "Writes the next frames' FSR inputs and output as BMP files (debugging).");
        _panelKey = Config.Bind("General", "PanelKey",
            new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl, KeyCode.LeftShift), "Opens the in-game panel (settings, DLSS-NR options, status).");
        // The first default, Ctrl+Shift+O, is AMD Software's performance overlay hotkey: the driver takes it first.
        if (_panelKey.Value.ToString() == new KeyboardShortcut(KeyCode.O, KeyCode.LeftControl, KeyCode.LeftShift).ToString())
            _panelKey.Value = (KeyboardShortcut)_panelKey.DefaultValue;
        _panel = new Panel(_enabled, _quality, _fsrVersion, _exposure, _sharpening, _objectMotion, _toggleKey, _qualityKey, _panelKey,
                           Path.Combine(Path.GetDirectoryName(Application.dataPath), "dlssnr_on_amd.ini"));
        _testCommands = Config.Bind("Debug", "TestCommands", false,
            "Testing aid: executes commands written to command.txt next to the plugin (used by the test scripts).");
        _loadScene = Config.Bind("Debug", "LoadSceneOnStart", "",
            "Testing aid: scene to load once VaM has started (e.g. Saves/scene/x.json); the main menu is hidden afterwards. Empty = off.");
        string dir = Path.GetDirectoryName(Info.Location);
        _dumpDir = Path.Combine(dir, "dump");
        _commandFile = Path.Combine(dir, "command.txt");
        Native.DlssNrAmd_SetLogPath(Path.Combine(dir, "DlssNrAmd_native.log"));
        // The AMD FidelityFX DLLs live outside BepInEx\plugins so BepInEx does not try to load them as plugins.
        Native.DlssNrAmd_Configure(Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "DlssNrAmd"), _fsrVersion.Value);
        _fsrVersion.SettingChanged += delegate
        {
            Native.DlssNrAmd_SetFsrVersion(_fsrVersion.Value);
            Logger.LogInfo("upscaler: FSR " + _fsrVersion.Value);
        };
        DrainNative();
    }

    // Debug "clothing": switches the first active clothing item of the first character off, and back on the next time.
    void ToggleTestClothing()
    {
        if (_testClothing == null)
            foreach (UnityEngine.Object o in FindObjectsOfType(typeof(DAZCharacterSelector)))
            {
                foreach (DAZClothingItem c in ((DAZCharacterSelector)o).clothingItems)
                    if (c != null && c.active)
                    {
                        _testClothing = c;
                        break;
                    }
                if (_testClothing != null) break;
            }
        if (_testClothing == null)
        {
            Logger.LogInfo("clothing: no active clothing item");
            return;
        }
        _testClothing.characterSelector.ToggleClothingItem(_testClothing);
        Logger.LogInfo("clothing: " + _testClothing.name + (_testClothing.active ? " on" : " off"));
    }

    // Diagnostics: logs every frame that took far longer than the recent average, with what ran in it.
    // The scans run during rendering, so their cost shows in the duration of the frame they ran in.
    void TrackHitches()
    {
        float dt = Time.unscaledDeltaTime;
        int gc = GC.CollectionCount(0);
        int prev = Time.frameCount - 1;
        if (dt > 0.03f && dt > 2.5f * _avgDt && Time.realtimeSinceStartup > 20f)
        {
            string why = gc != _lastGcCount ? " GC" : "";
            _lastGcCount = gc;
            if (!_testCommands.Value) return;  // logged for testing only
            if (_hook != null && _hook.MipBias.LastScanFrame == prev)
                why += " mip-scan " + _hook.MipBias.LastScanMs.ToString("F1") + " ms (find " + _hook.MipBias.LastFindMs.ToString("F1") +
                       " ms, " + _hook.MipBias.LastExamined + " textures)";
            if (_hook != null && _hook.SkinMotion.LastRescanFrame == prev)
                why += " scene-scan " + _hook.SkinMotion.LastRescanMs.ToString("F1") + " ms";
            if (_lastReportFrame == prev) why += " report";
            Logger.LogInfo("hitch " + (dt * 1000f).ToString("F0") + " ms at " + Time.realtimeSinceStartup.ToString("F1") + " s (average " +
                           (_avgDt * 1000f).ToString("F0") + " ms):" + (why.Length > 0 ? why : " other"));
        }
        else
            _avgDt = Mathf.Lerp(_avgDt, dt, 0.05f);
        _lastGcCount = gc;
    }

    void Update()
    {
        TrackHitches();
        if (_toggleKey.Value.IsDown())
        {
            _enabled.Value = !_enabled.Value;
            Logger.LogInfo("effect " + (_enabled.Value ? "on" : "off"));
        }
        if (_qualityKey.Value.IsDown())
        {
            _quality.Value = (UpscaleQuality)(((int)_quality.Value + 1) % Enum.GetValues(typeof(UpscaleQuality)).Length);
            Logger.LogInfo("quality: " + _quality.Value);
        }
        if (_panelKey.Value.IsDown())
        {
            _panel.Visible = !_panel.Visible;
            Logger.LogInfo("panel " + (_panel.Visible ? "opened" : "closed"));
        }
        if (_panel.Visible && Time.realtimeSinceStartup >= _nextPanelStatus)
        {
            _nextPanelStatus = Time.realtimeSinceStartup + 0.5f;
            _panel.Status = PanelStatus();
        }
        if (_dumpKey.Value.IsDown())
        {
            Native.DlssNrAmd_RequestDump(_dumpDir);
            Logger.LogInfo("dump requested: " + _dumpDir);
        }
        if (_hook == null) AttachToMainCamera();
        bool failed = Native.DlssNrAmd_GetState() < 0;
        if (failed && !_failureLogged)
        {
            _failureLogged = true;
            DrainNative();
            Logger.LogError("native side failed, effect disabled: " + Marshal.PtrToStringAnsi(Native.DlssNrAmd_GetStatus()));
        }
        if (_hook != null)
        {
            _hook.enabled = _enabled.Value && !failed;
            _hook.Exposure = _exposure.Value;
            _hook.Ratio = Presets.Ratio(_quality.Value);
            _hook.Sharpening = _sharpening.Value;
            _hook.ObjectMotion = _objectMotion.Value;
        }
        LoadSceneOnStart();
        PollCommands();
        ApplyMove();
        ApplySpin();
        if (_fov > 0f && _hook != null) _hook.GetComponent<Camera>().fieldOfView = _fov;
        Report();
    }

    // Debug "pkgdiag": reports VaM's package registration state (read-only).
    void PackageDiagnostics()
    {
        try
        {
            const System.Reflection.BindingFlags sf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                                                      System.Reflection.BindingFlags.NonPublic;
            Type fm = typeof(MVR.FileManagement.FileManager);
            object packagesEnabled = fm.GetField("packagesEnabled", sf).GetValue(null);
            System.Collections.IDictionary byUid = (System.Collections.IDictionary)fm.GetField("packagesByUid", sf).GetValue(null);
            System.Collections.IDictionary byPath = (System.Collections.IDictionary)fm.GetField("packagesByPath", sf).GetValue(null);
            string folder = (string)fm.GetField("packageFolder", sf).GetValue(null);
            string[] prefixes = (string[])fm.GetField("demoPackagePrefixes", sf).GetValue(null);
            object disablePackages = typeof(SuperController).GetField("disablePackages",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .GetValue(SuperController.singleton);
            Logger.LogInfo("pkgdiag: packagesEnabled=" + packagesEnabled + " SuperController.disablePackages=" + disablePackages +
                           " packageFolder=" + folder + " registered uids=" + (byUid == null ? -1 : byUid.Count) +
                           " demoPrefixes=" + (prefixes == null ? "null" : string.Join("|", prefixes)));
            string[] files = Directory.GetFiles(folder, "*.var", SearchOption.AllDirectories);
            int registered = 0, firstMissing = -1, lastRegisteredIndex = -1;
            for (int i = 0; i < files.Length; i++)
            {
                if (byPath != null && byPath.Contains(files[i])) { registered++; lastRegisteredIndex = i; }
                else if (firstMissing < 0) firstMissing = i;
            }
            Logger.LogInfo("pkgdiag: var files found " + files.Length + ", registered " + registered +
                           (lastRegisteredIndex >= 0 ? ", last registered #" + lastRegisteredIndex + " " + files[lastRegisteredIndex] : "") +
                           (firstMissing >= 0 ? ", first unregistered #" + firstMissing + " " + files[firstMissing] : ""));
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int n = 0;
            if (byUid != null)
                foreach (object k in byUid.Keys) { if (n++ >= 40) break; sb.Append(k).Append("  "); }
            Logger.LogInfo("pkgdiag: registered uids (first 40, registration order): " + sb);
        }
        catch (Exception e)
        {
            Logger.LogError("pkgdiag failed: " + e);
        }
    }

    // Debug "move <yaw deg/s> <up m/s> <seconds>": turns and lifts VaM's navigation rig, for motion-vector tests.
    void ApplyMove()
    {
        if (_hook == null || Time.realtimeSinceStartup >= _moveUntil) return;
        Transform rig = _hook.transform;
        for (int i = 0; i < 3 && rig.parent != null; i++) rig = rig.parent;  // MonitorRig -> HeightOffset -> [CameraRig] -> NavRig
        float dt = Time.unscaledDeltaTime;
        rig.RotateAround(_hook.transform.position, Vector3.up, _moveYaw * dt);
        rig.position += Vector3.up * (_moveUp * dt);
    }

    void OnGUI()
    {
        _panel.OnGUI();
    }

    // Three short lines for the panel.
    string PanelStatus()
    {
        string native = Marshal.PtrToStringAnsi(Native.DlssNrAmd_GetStatus());
        if (Native.DlssNrAmd_GetState() < 0) return "Failed, effect disabled:\n" + native;
        if (_hook == null || !_hook.enabled) return "Effect off\n" + _lastFps;
        string[] p = native.Split(new string[] { " | " }, StringSplitOptions.None);
        if (p.Length < 4 || p[0] != "running") return "Starting (the first time takes about 10 seconds)\n" + _hook.SizeText;
        return p[1] + ", " + _lastFps + "\n" + _hook.MotionText + (p[3].StartsWith("dispatch errors 0") ? "" : "\n" + p[3]);
    }

    // Debug "spin <deg/s> <seconds>": turns every Person atom about its main controller, for object-motion tests.
    void ApplySpin()
    {
        if (Time.realtimeSinceStartup >= _spinUntil || SuperController.singleton == null) return;
        foreach (Atom a in SuperController.singleton.GetAtoms())
        {
            if (a.type != "Person" || a.mainController == null) continue;
            Transform t = a.mainController.transform;
            t.RotateAround(t.position, Vector3.up, _spinRate * Time.unscaledDeltaTime);
        }
    }

    // Debug "uipos <text>": screen position (pixels, origin bottom left) of the first visible UI text that
    // contains <text>, so the test script can click it with the real mouse.
    void LogUiPosition(string query)
    {
        foreach (UnityEngine.Object o in FindObjectsOfType(typeof(UnityEngine.UI.Text)))
        {
            UnityEngine.UI.Text t = (UnityEngine.UI.Text)o;
            string s = t.text == null ? "" : t.text.Replace('\n', ' ');
            Canvas c = t.canvas;
            if (c == null || s.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Camera cam = null;
            if (c.renderMode != RenderMode.ScreenSpaceOverlay)
                cam = c.worldCamera ?? Camera.main ?? (_hook != null ? _hook.GetComponent<Camera>() : null);
            RectTransform rt = t.rectTransform;
            Vector3 world = rt.TransformPoint(rt.rect.center);
            if (cam != null && cam.WorldToViewportPoint(world).z <= 0f) continue;
            Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, world);
            if (p.x < 0f || p.y < 0f || p.x >= Screen.width || p.y >= Screen.height) continue;
            Logger.LogInfo("ui position of '" + query + "': " + Mathf.RoundToInt(p.x) + " " + Mathf.RoundToInt(p.y) + " of " +
                           Screen.width + "x" + Screen.height + " (text '" + s + "', canvas " + c.name + ", " + c.renderMode +
                           ", camera " + (cam == null ? "none" : cam.name) + ")");
            return;
        }
        Logger.LogInfo("ui position of '" + query + "': not found");
    }

    // Debug "state": what VaM registered, for the click tests.
    void LogState()
    {
        SuperController sc = SuperController.singleton;
        System.Reflection.FieldInfo f = typeof(SuperController).GetField("_monitorUIVisible",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Logger.LogInfo("state: game mode " + sc.gameMode + ", main HUD visible " + sc.MainHUDVisible +
                       ", monitor UI visible " + (f == null ? "?" : f.GetValue(sc).ToString()));
    }

    // Testing aid ([Debug] TestCommands): lines in command.txt next to the plugin are executed once and the
    // file is deleted, so test scripts do not depend on window focus. Commands: dump, enable, disable,
    // exposure <x>, quality <preset>, hud [on|off], ui on|off, shot <png>, uipos <text>, state, panel [on|off],
    // panelrects, load <scene>, fsr <version>, clothing, objmotion on|off, hairmotion on|off, glow on|off, spin <deg/s> <s>, fov <deg>,
    // move <yaw deg/s> <up m/s> <s>, pkgdiag.
    void PollCommands()
    {
        if (!_testCommands.Value) return;
        float now = Time.realtimeSinceStartup;
        if (now < _nextCommandCheck) return;
        _nextCommandCheck = now + 0.5f;
        if (!File.Exists(_commandFile)) return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(_commandFile);
            File.Delete(_commandFile);
        }
        catch (IOException)
        {
            return;
        }
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (string raw in lines)
        {
            string[] parts = raw.Trim().Split(' ');
            if (parts[0].Length == 0) continue;
            Logger.LogInfo("command: " + raw.Trim());
            switch (parts[0].ToLowerInvariant())
            {
                case "dump": Native.DlssNrAmd_RequestDump(_dumpDir); break;
                case "enable": _enabled.Value = true; break;
                case "disable": _enabled.Value = false; break;
                case "exposure":
                    if (parts.Length > 1) _exposure.Value = float.Parse(parts[1], inv);
                    break;
                case "quality":
                    if (parts.Length > 1) _quality.Value = (UpscaleQuality)Enum.Parse(typeof(UpscaleQuality), parts[1], true);
                    break;
                case "hud":  // "hud" toggles VaM's main menu; "hud on" / "hud off" set it
                    if (parts.Length < 2 || SuperController.singleton.MainHUDVisible != (parts[1] == "on"))
                        SuperController.singleton.ToggleMainHUD();
                    break;
                case "shot":  // the frame as presented, captured inside VaM (independent of window focus)
                    if (parts.Length > 1) ScreenCapture.CaptureScreenshot(raw.Trim().Substring(5).Trim());
                    break;
                case "ui":  // "ui on" / "ui off": VaM's monitor UI (what the "Toggle UI (U)" button switches)
                    if (parts.Length > 1 && parts[1] == "on") SuperController.singleton.ShowMonitorUI();
                    else SuperController.singleton.HideMonitorUI();
                    break;
                case "uipos":
                    if (parts.Length > 1) LogUiPosition(raw.Trim().Substring(5).Trim());
                    break;
                case "state": LogState(); break;
                case "load":  // VaM scene load (path relative to the VaM folder)
                    if (parts.Length > 1) SuperController.singleton.Load(raw.Trim().Substring(5).Trim());
                    break;
                case "objmotion":
                    if (parts.Length > 1) _objectMotion.Value = parts[1] == "on";
                    break;
                case "fov":  // debug zoom: "fov 14"; "fov 0" gives the camera back to VaM
                    if (parts.Length > 1) _fov = float.Parse(parts[1], inv);
                    break;
                case "glow":  // VaM's bloom (MKGlow on the main camera) on / off
                    if (parts.Length > 1 && _hook != null)
                        foreach (MonoBehaviour b in _hook.GetComponents<MonoBehaviour>())
                            if (b.GetType().Name.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                b.enabled = parts[1] == "on";
                                Logger.LogInfo("glow: " + b.GetType().Name + (b.enabled ? " on" : " off"));
                            }
                    break;
                case "hairmotion":
                    if (parts.Length > 1 && _hook != null) _hook.HairMotion = parts[1] == "on";
                    break;
                case "spin":
                    if (parts.Length > 2)
                    {
                        _spinRate = float.Parse(parts[1], inv);
                        _spinUntil = now + float.Parse(parts[2], inv);
                    }
                    break;
                case "panel":
                    _panel.Visible = parts.Length < 2 || parts[1] == "on";
                    break;
                case "clothing": ToggleTestClothing(); break;
                case "fsr":  // upscaler: "fsr 4.1" / "fsr 3.1"
                    if (parts.Length > 1) _fsrVersion.Value = parts[1];
                    break;
                case "panelrects":  // centres in client pixels (top left origin), for the test script
                    Logger.LogInfo("panel rects: effect " + (int)_panel.EffectRect.center.x + " " + (int)_panel.EffectRect.center.y +
                                   " fsr " + (int)_panel.FsrRect.x + " " + (int)_panel.FsrRect.center.y + " " + (int)_panel.FsrRect.width +
                                   " tone " + (int)_panel.ToneRect.x + " " + (int)_panel.ToneRect.center.y + " " + (int)_panel.ToneRect.width +
                                   " skinsame " + (int)(_panel.SkinSameRect.x + 30f) + " " + (int)_panel.SkinSameRect.center.y);
                    break;
                case "pkgdiag": PackageDiagnostics(); break;
                case "move":
                    if (parts.Length > 3)
                    {
                        _moveYaw = float.Parse(parts[1], inv);
                        _moveUp = float.Parse(parts[2], inv);
                        _moveUntil = now + float.Parse(parts[3], inv);
                    }
                    break;
                default: Logger.LogWarning("unknown command: " + raw.Trim()); break;
            }
        }
    }

    void LoadSceneOnStart()
    {
        if (_sceneStage == 3 || string.IsNullOrEmpty(_loadScene.Value)) return;
        SuperController sc = SuperController.singleton;
        if (sc == null) return;
        if (_sceneStage == 0)
        {
            if (sc.isLoading || Time.realtimeSinceStartup < 10f) return;
            Logger.LogInfo("loading scene " + _loadScene.Value);
            sc.Load(_loadScene.Value);
            _sceneStage = 1;
        }
        else if (_sceneStage == 1 && sc.isLoading)
            _sceneStage = 2;
        else if (_sceneStage == 2 && !sc.isLoading)
        {
            sc.HideMainHUD();
            _sceneStage = 3;
            Logger.LogInfo("scene loaded");
        }
    }

    void AttachToMainCamera()
    {
        Camera cam = CameraTarget.centerTarget != null ? CameraTarget.centerTarget.targetCamera : null;
        if (cam == null) return;
        _hook = cam.GetComponent<CameraHook>();
        if (_hook == null) _hook = cam.gameObject.AddComponent<CameraHook>();
        Logger.LogInfo("attached to camera \"" + cam.name + "\" (" + cam.pixelWidth + "x" + cam.pixelHeight + ")");
    }

    void Report()
    {
        _reportFrames++;
        float now = Time.realtimeSinceStartup;
        if (now < _reportAt) return;
        DrainNative();
        if (now > _reportStart)
        {
            string fps = (_reportFrames / (now - _reportStart)).ToString("F1") + " fps";
            _lastFps = fps;
            _lastReportFrame = Time.frameCount;
            if (_hook != null && _hook.enabled)
            {
                // Outside rendering the camera must be back at full size, or VaM's mouse picking would be off.
                Camera cam = _hook.GetComponent<Camera>();
                Logger.LogInfo(Marshal.PtrToStringAnsi(Native.DlssNrAmd_GetStatus()) + " | " + _quality.Value + ", " + _hook.SizeText +
                               " | camera between frames " + cam.pixelWidth + "x" + cam.pixelHeight +
                               (cam.targetTexture != null ? " (target still attached)" : "") +
                               " | mip bias " + _hook.MipBias.Bias.ToString("F2") + " on " + _hook.MipBias.Count + " textures | " +
                               _hook.MotionText + " | " + fps);
            }
            else
                Logger.LogInfo("effect off | " + fps);
        }
        _reportStart = now;
        _reportAt = now + 5f;
        _reportFrames = 0;
    }

    void DrainNative()
    {
        string text = Marshal.PtrToStringAnsi(Native.DlssNrAmd_DrainLog());
        if (string.IsNullOrEmpty(text)) return;
        foreach (string line in text.Split('\n'))
            if (line.Length > 0) Logger.LogInfo("[native] " + line);
    }
}
