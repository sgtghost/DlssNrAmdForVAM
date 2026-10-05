// In-game panel (IMGUI): status, this plugin's settings, and DLSS-NR-on-AMD's own settings, which live in
// dlssnr_on_amd.ini next to VaM.exe and which the mod re-reads while running.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

public class Panel
{
    public bool Visible;
    public string Status = "";
    public Rect EffectRect, FsrRect, ToneRect, SkinSameRect;  // screen pixels (top left origin), for the test script

    readonly ConfigEntry<bool> _enabled, _objectMotion;
    readonly ConfigEntry<UpscaleQuality> _quality;
    readonly ConfigEntry<string> _fsrVersion;
    readonly ConfigEntry<float> _exposure, _sharpening;
    readonly ConfigEntry<KeyboardShortcut> _toggleKey, _qualityKey, _panelKey;
    readonly NrIni _ini;
    Rect _rect = new Rect(40f, 40f, 470f, 10f);
    float _scale = 1f;
    static Rect s_lastSlider;
    static readonly string[] QualityNames = { "Native", "Quality", "Balanced", "Perform.", "Ultra P." };
    static readonly string[] FsrVersions = { "3.1", "4.1" };
    static readonly string[] FsrNames = { "FSR 3.1 (faster)", "FSR 4.1 (ML, better in motion)" };

    public Panel(ConfigEntry<bool> enabled, ConfigEntry<UpscaleQuality> quality, ConfigEntry<string> fsrVersion,
                 ConfigEntry<float> exposure, ConfigEntry<float> sharpening, ConfigEntry<bool> objectMotion,
                 ConfigEntry<KeyboardShortcut> toggleKey, ConfigEntry<KeyboardShortcut> qualityKey,
                 ConfigEntry<KeyboardShortcut> panelKey, string iniPath)
    {
        _enabled = enabled;
        _quality = quality;
        _fsrVersion = fsrVersion;
        _exposure = exposure;
        _sharpening = sharpening;
        _objectMotion = objectMotion;
        _toggleKey = toggleKey;
        _qualityKey = qualityKey;
        _panelKey = panelKey;
        _ini = new NrIni(iniPath);
    }

    // "Ctrl+Shift+P" rather than BepInEx's "P + LeftShift + LeftControl": Ctrl, Alt, Shift, others, main key.
    static string Keys(KeyboardShortcut k)
    {
        List<string> parts = new List<string>();
        foreach (string mod in new[] { "Control", "Alt", "Shift" })
            foreach (KeyCode m in k.Modifiers)
                if (m.ToString().EndsWith(mod))
                {
                    parts.Add(mod == "Control" ? "Ctrl" : mod);
                    break;
                }
        foreach (KeyCode m in k.Modifiers)
        {
            string n = m.ToString();
            if (!n.EndsWith("Control") && !n.EndsWith("Alt") && !n.EndsWith("Shift")) parts.Add(n);
        }
        parts.Add(k.MainKey.ToString());
        return string.Join("+", parts.ToArray());
    }

    public void OnGUI()
    {
        if (!Visible) return;
        _ini.Flush();
        // Laid out for 1080p and scaled with the window, so it stays readable at 4K.
        float s = Mathf.Max(1f, Screen.height / 1080f);
        _scale = s;
        Matrix4x4 saved = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        _rect = GUILayout.Window(0x444e41, _rect, DrawWindow, "DLSS-NR on AMD");
        GUI.matrix = saved;
    }

    void DrawWindow(int id)
    {
        _enabled.Value = GUILayout.Toggle(_enabled.Value, " Effect on  (" + Keys(_toggleKey.Value) + ")");
        if (Event.current.type == EventType.Repaint) EffectRect = ToScreen(GUILayoutUtility.GetLastRect());
        GUILayout.Label("Render resolution  (" + Keys(_qualityKey.Value) + " cycles)");
        _quality.Value = (UpscaleQuality)GUILayout.Toolbar((int)_quality.Value, QualityNames);
        GUILayout.Label("Upscaler");
        int fsr = GUILayout.Toolbar(Array.IndexOf(FsrVersions, _fsrVersion.Value), FsrNames);
        if (Event.current.type == EventType.Repaint) FsrRect = ToScreen(GUILayoutUtility.GetLastRect());
        if (fsr >= 0 && FsrVersions[fsr] != _fsrVersion.Value) _fsrVersion.Value = FsrVersions[fsr];
        _exposure.Value = Slider("Exposure", _exposure.Value, 0.5f, 4f);
        _sharpening.Value = Slider("Texture sharpening", _sharpening.Value, 0f, 1f);
        _objectMotion.Value = GUILayout.Toggle(_objectMotion.Value, " Motion vectors for skin, clothing and hair");

        GUILayout.Space(8f);
        GUILayout.Label("DLSS-NR  (" + (_ini.Found ? "dlssnr_on_amd.ini" : "dlssnr_on_amd.ini not found") + ")");
        if (_ini.Found)
        {
            bool nr = _ini.Get("Enabled", "1") != "0";
            bool nr2 = GUILayout.Toggle(nr, " Neural rendering");
            if (nr2 != nr) _ini.Set("Enabled", nr2 ? "1" : "0");
            IniSlider("Tone", "LocalTone", 0f, 2f);
            IniSlider("Structure", "LocalStructure", 0f, 2f);
            // SkinStructure -1 (the mod's default) means "as Structure". Structure 0 with skin structure 1 keeps
            // the skin detail without the faint halo the network lifts into black backgrounds around characters.
            bool same = _ini.Get("SkinStructure", "-1").StartsWith("-");
            bool same2 = GUILayout.Toggle(same, " Skin structure same as Structure");
            if (Event.current.type == EventType.Repaint) SkinSameRect = ToScreen(GUILayoutUtility.GetLastRect());
            if (same2 != same) _ini.Set("SkinStructure", same2 ? "-1" : _ini.Get("LocalStructure", "1"));
            GUI.enabled = !same2;
            if (same2) Slider("Skin structure", ParseIni("LocalStructure", 1f), 0f, 2f);
            else IniSlider("Skin structure", "SkinStructure", 0f, 2f);
            GUI.enabled = true;
            bool fast = _ini.Get("Quality", "fast") != "reference";
            int q = GUILayout.Toolbar(fast ? 0 : 1, new string[] { "Fast", "Reference" });
            if ((q == 0) != fast) _ini.Set("Quality", q == 0 ? "fast" : "reference");
        }
        // Fixed height at the bottom: a changing status must not move the controls above it.
        GUILayout.Space(8f);
        GUILayout.Label(Status, GUILayout.Height(66f));
        if (GUILayout.Button("Close  (" + Keys(_panelKey.Value) + ")")) Visible = false;
        GUI.DragWindow();
    }

    Rect ToScreen(Rect r)
    {
        return new Rect((_rect.x + r.x) * _scale, (_rect.y + r.y) * _scale, r.width * _scale, r.height * _scale);
    }

    static float Slider(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140f));
        float v = GUILayout.HorizontalSlider(value, min, max);
        if (Event.current.type == EventType.Repaint) s_lastSlider = GUILayoutUtility.GetLastRect();
        GUILayout.Label(v.ToString("F2"), GUILayout.Width(40f));
        GUILayout.EndHorizontal();
        return Mathf.Round(v * 100f) / 100f;
    }

    float ParseIni(string key, float def)
    {
        float v;
        return float.TryParse(_ini.Get(key, ""), System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
    }

    void IniSlider(string label, string key, float min, float max)
    {
        float cur = ParseIni(key, 0f);
        float v = Slider(label, cur, min, max);
        if (key == "LocalTone" && Event.current.type == EventType.Repaint) ToneRect = ToScreen(s_lastSlider);
        if (Mathf.Abs(v - cur) > 0.001f) _ini.Set(key, v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }
}

// Minimal key=value editor for dlssnr_on_amd.ini: keeps every other line as it is, writes changed values
// at most a few times a second (sliders change every frame while dragged).
public class NrIni
{
    readonly string _path;
    List<string> _lines = new List<string>();
    DateTime _stamp;
    bool _dirty;
    float _nextWrite;

    public bool Found { get { return File.Exists(_path); } }

    public NrIni(string path)
    {
        _path = path;
    }

    public string Get(string key, string def)
    {
        Load();
        foreach (string l in _lines)
        {
            int eq = l.IndexOf('=');
            if (eq > 0 && l.Substring(0, eq).Trim() == key) return l.Substring(eq + 1).Trim();
        }
        return def;
    }

    public void Set(string key, string value)
    {
        Load();
        for (int i = 0; i < _lines.Count; i++)
        {
            int eq = _lines[i].IndexOf('=');
            if (eq > 0 && _lines[i].Substring(0, eq).Trim() == key)
            {
                _lines[i] = key + "=" + value;
                _dirty = true;
                return;
            }
        }
        _lines.Add(key + "=" + value);
        _dirty = true;
    }

    public void Flush()
    {
        if (!_dirty || Time.realtimeSinceStartup < _nextWrite) return;
        File.WriteAllLines(_path, _lines.ToArray());
        _stamp = File.GetLastWriteTimeUtc(_path);
        _dirty = false;
        _nextWrite = Time.realtimeSinceStartup + 0.3f;
    }

    // Picks up edits made elsewhere (the mod's setup tool, a text editor) unless ours are pending.
    void Load()
    {
        if (_dirty || !File.Exists(_path)) return;
        DateTime t = File.GetLastWriteTimeUtc(_path);
        if (t == _stamp && _lines.Count > 0) return;
        _lines = new List<string>(File.ReadAllLines(_path));
        _stamp = t;
    }
}
