// Interop with DlssNrAmdNative.dll and the shared quality presets.
using System;
using System.Runtime.InteropServices;

public enum UpscaleQuality
{
    NativeAA,
    Quality,
    Balanced,
    Performance,
    UltraPerformance,
}

public static class Presets
{
    // Display size divided by render size, per axis (FSR's standard ratios).
    public static float Ratio(UpscaleQuality q)
    {
        switch (q)
        {
            case UpscaleQuality.Quality: return 1.5f;
            case UpscaleQuality.Balanced: return 1.7f;
            case UpscaleQuality.Performance: return 2f;
            case UpscaleQuality.UltraPerformance: return 3f;
            default: return 1f;
        }
    }
}

// Must match DnaMeshDraw in native/src/dna.h.
[StructLayout(LayoutKind.Sequential)]
struct MeshDraw
{
    public IntPtr verts, indices;
    public uint indexStart, indexCount, baseVertex, index32;
    public int key;
    public float offsetX, offsetY, offsetZ;
    public uint stride;
    public float tolerance;
}

// Must match DnaFrameData in native/src/dna.h.
[StructLayout(LayoutKind.Sequential)]
struct FrameData
{
    public IntPtr color, depth, motion, output;
    public int width, height, displayWidth, displayHeight;
    public float jitterX, jitterY, mvScaleX, mvScaleY, frameTimeMs, cameraNear, cameraFar, fovY;
    public int reset, hdr;
    public float exposure;
    public int meshDrawCount;
    public IntPtr meshDraws;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] rasterViewProj;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] viewProj;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] prevViewProj;
}

static class Native
{
    const string Dll = "DlssNrAmdNative";
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern void DlssNrAmd_SetLogPath(string path);
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern void DlssNrAmd_Configure(string ffxDir, [MarshalAs(UnmanagedType.LPStr)] string fsrVersionPrefix);
    [DllImport(Dll)] public static extern void DlssNrAmd_SetFsrVersion([MarshalAs(UnmanagedType.LPStr)] string fsrVersionPrefix);
    [DllImport(Dll)] public static extern IntPtr DlssNrAmd_GetRenderEventFunc();
    [DllImport(Dll)] public static extern int DlssNrAmd_GetState();
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern void DlssNrAmd_RequestDump(string dir);
    [DllImport(Dll)] public static extern IntPtr DlssNrAmd_GetStatus();
    [DllImport(Dll)] public static extern IntPtr DlssNrAmd_DrainLog();
}
