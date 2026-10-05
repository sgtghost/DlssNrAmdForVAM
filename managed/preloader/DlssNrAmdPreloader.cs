// BepInEx preloader patcher for "DLSS-NR on AMD for VaM". It patches nothing: it loads the DXGI and
// D3D12 runtimes while VaM is still starting up, so DLSS-NR-on-AMD (version.dll) installs its DXGI
// hooks before anything renders. Hooking later, while VaM's render thread is presenting, can deadlock.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

public static class DlssNrAmdPreloader
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string name);

    public static IEnumerable<string> TargetDLLs { get { return new string[0]; } }

    public static void Patch(AssemblyDefinition assembly) { }

    public static void Initialize()
    {
        ManualLogSource log = Logger.CreateLogSource("DLSS-NR on AMD preloader");
        string modLog = Path.Combine(Paths.GameRootPath, "dlssnr_on_amd.log");
        if (!File.Exists(Path.Combine(Paths.GameRootPath, "version.dll")) || !File.Exists(modLog))
        {
            log.LogInfo("DLSS-NR-on-AMD not installed next to VaM.exe; nothing to do");
            return;
        }
        LoadLibraryW("dxgi.dll");
        LoadLibraryW("d3d12.dll");
        DateTime start = DateTime.Now;
        while ((DateTime.Now - start).TotalSeconds < 6)
        {
            if (PresentHooked(modLog))
            {
                log.LogInfo("DLSS-NR-on-AMD hooks installed after " + (int)(DateTime.Now - start).TotalMilliseconds + " ms");
                return;
            }
            Thread.Sleep(50);
        }
        log.LogWarning("DLSS-NR-on-AMD did not report its Present hook within 6 s; continuing");
    }

    // True once the mod's log, since its last load into VaM.exe, reports the Present1 hook.
    static bool PresentHooked(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 65536);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[fs.Length - start];
                int n = fs.Read(buf, 0, buf.Length);
                string tail = Encoding.UTF8.GetString(buf, 0, n);
                int session = tail.LastIndexOf("loaded into VaM.exe", StringComparison.Ordinal);
                return session >= 0 && tail.IndexOf("hooked IDXGISwapChain1::Present1", session, StringComparison.Ordinal) >= 0;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }
}
