// Lives on VaM's main camera ("MonitorRig").
//
// While drawing, the camera renders into a target of render size (display size / Ratio); VaM's input
// handling and everything else between frames see the camera at full size, because the target is
// attached in OnPreCull and taken off again at the end of the frame. After the camera's image effects
// the finished frame, its depth and motion vectors go to the native bridge, where FSR (with
// DLSS-NR-on-AMD hooked into it) upscales them to display size. A presenter camera that renders right
// after this one, and before VaM's UI camera, draws the result to the screen and then VaM's world-space
// interface on top at display size, so the menus are neither upscaled nor altered by the network.
using System;
using System.Collections;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

public class CameraHook : MonoBehaviour
{
    public static ManualLogSource Log;
    public float Exposure = 2f;
    public float Ratio = 2f;       // display size / render size, per axis
    public float Sharpening = 1f;  // fraction of FSR's recommended texture mip bias
    public bool ObjectMotion = true;  // motion vectors for VaM's GPU-skinned skin and clothing

    public string SizeText { get { return "render " + _renderW + "x" + _renderH + " -> display " + _displayW + "x" + _displayH; } }
    public MipBias MipBias { get { return _mipBias; } }
    public string MotionText { get { return ObjectMotion ? _skinMotion.Text : "object motion off"; } }
    public bool HairMotion { set { _skinMotion.HairMotion = value; } }  // debug: hair part of the object motion

    const DepthTextureMode Needed = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
    // VaM's own interface: world-space canvases on "UI" (overlay shader, always on top) and "LoadUI".
    const int UiLayers = (1 << 5) | (1 << 22);
    const int RingSize = 8;  // frame-data slots; the render thread may still read a slot of an earlier frame
    static readonly Vector2 FlipScale = new Vector2(1f, -1f);
    static readonly Vector2 FlipOffset = new Vector2(0f, 1f);
    static IntPtr s_renderEvent;

    readonly MipBias _mipBias = new MipBias();
    readonly SkinMotion _skinMotion = new SkinMotion(Log);
    readonly float[] _rasterVP = new float[16], _viewProj = new float[16], _prevViewProj = new float[16];
    Matrix4x4 _prevVP;
    IntPtr _drawRing;
    Camera _cam;
    Camera _presenter;
    CommandBuffer _cb, _presentCb;
    Coroutine _endOfFrame;
    IntPtr _ring;
    int _slotSize, _frame, _savedMask;
    bool _reset, _active, _depthModeResetLogged;
    float _jx, _jy, _nextMipScan;
    DepthTextureMode _savedDepthMode;
    int _displayW, _displayH, _renderW, _renderH;
    RenderTexture _target, _color, _depth, _motion, _output;
    IntPtr _colorPtr, _depthPtr, _motionPtr, _outputPtr;

    void OnEnable()
    {
        _cam = GetComponent<Camera>();
        if (s_renderEvent == IntPtr.Zero) s_renderEvent = Native.DlssNrAmd_GetRenderEventFunc();
        if (_ring == IntPtr.Zero)
        {
            _slotSize = Marshal.SizeOf(typeof(FrameData));
            _ring = Marshal.AllocHGlobal(_slotSize * RingSize);
            _drawRing = Marshal.AllocHGlobal(SkinMotion.DrawSize * SkinMotion.MaxDraws * RingSize);
        }
        _savedDepthMode = _cam.depthTextureMode;
        _cam.depthTextureMode |= Needed;
        _cb = new CommandBuffer();
        _cb.name = "DLSS-NR on AMD";
        _cam.AddCommandBuffer(CameraEvent.AfterImageEffects, _cb);
        CreatePresenter();
        _endOfFrame = StartCoroutine(EndOfFrame());
        _reset = true;
        _nextMipScan = 0f;
        if (Log == null) return;
        Log.LogInfo("camera hook enabled: colour space " + QualitySettings.activeColorSpace + ", motion vectors supported " +
                    SystemInfo.supportsMotionVectors + ", depth texture mode " + _savedDepthMode + " -> " + _cam.depthTextureMode);
        if (_cam.allowMSAA && QualitySettings.antiAliasing > 1)
            Log.LogWarning("MSAA is on (" + QualitySettings.antiAliasing + "x): turn it off in VaM's settings, FSR does the anti-aliasing");
    }

    void OnDisable()
    {
        if (_endOfFrame != null)
        {
            StopCoroutine(_endOfFrame);
            _endOfFrame = null;
        }
        RestoreCamera();
        if (_cb != null)
        {
            _cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, _cb);
            _cb.Release();
            _cb = null;
        }
        DestroyPresenter();
        _cam.depthTextureMode = _savedDepthMode;
        _mipBias.Restore();
        _skinMotion.Clear();
        ReleaseTextures();
        // _ring and _drawRing stay allocated: frames already queued on the render thread may still read them.
    }

    void CreatePresenter()
    {
        // Follows this camera; field of view, clip planes and the interface layers are copied every frame.
        GameObject go = new GameObject("DLSS-NR on AMD presenter");
        go.transform.SetParent(transform, false);
        _presenter = go.AddComponent<Camera>();
        _presenter.clearFlags = CameraClearFlags.Depth;
        _presenter.cullingMask = 0;
        _presenter.depth = _cam.depth + 0.01f;  // after this camera, before VaM's UI camera
        _presenter.allowHDR = false;
        _presenter.allowMSAA = false;
        _presenter.useOcclusionCulling = false;
        // The upscaled frame becomes the background (filled in by EnsureTextures), the interface is drawn
        // over it, and the image effect hands the result on. Only the image-effect destination reaches the
        // screen: blits to the screen from a command buffer or OnPostRender were gone once VaM's UI camera
        // (HDR, clears nothing) had drawn (tested).
        _presentCb = new CommandBuffer();
        _presentCb.name = "DLSS-NR on AMD present";
        _presenter.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _presentCb);
        go.AddComponent<Presenter>();
    }

    void DestroyPresenter()
    {
        if (_presenter == null) return;
        _presenter.enabled = false;  // Destroy happens at the end of the frame; do not draw in this one
        Destroy(_presenter.gameObject);
        _presenter = null;
        _presentCb.Release();
        _presentCb = null;
    }

    IEnumerator EndOfFrame()
    {
        WaitForEndOfFrame wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            RestoreCamera();
        }
    }

    // Puts the camera back to full size between frames.
    void RestoreCamera()
    {
        if (!_active) return;
        _active = false;
        if (_target != null && _cam.targetTexture == _target) _cam.targetTexture = null;
        if (_cam.cullingMask == (_savedMask & ~UiLayers)) _cam.cullingMask = _savedMask;
        _cam.ResetProjectionMatrix();
    }

    void OnPreCull()
    {
        RenderTexture current = _cam.targetTexture;
        if (current != null && current != _target) return;  // VaM renders this camera into its own texture (thumbnails, captures)
        if (current == null)
        {
            _displayW = _cam.pixelWidth;
            _displayH = _cam.pixelHeight;
        }
        int rw = Mathf.Max(8, Mathf.RoundToInt(_displayW / Ratio));
        int rh = Mathf.Max(8, Mathf.RoundToInt(_displayH / Ratio));
        EnsureTextures(rw, rh, _displayW, _displayH);
        _cam.targetTexture = _target;
        _active = true;
        // VaM's interface is left to the presenter (display size, after upscaling).
        _savedMask = _cam.cullingMask;
        _cam.cullingMask = _savedMask & ~UiLayers;
        _presenter.cullingMask = _savedMask & UiLayers;
        _presenter.fieldOfView = _cam.fieldOfView;
        _presenter.nearClipPlane = _cam.nearClipPlane;
        _presenter.farClipPlane = _cam.farClipPlane;
        // Other components assign depthTextureMode instead of OR-ing into it; re-arm every frame.
        if ((_cam.depthTextureMode & Needed) != Needed)
        {
            if (!_depthModeResetLogged && Log != null)
                Log.LogInfo("depth texture mode was reset to " + _cam.depthTextureMode + " by another component; re-arming each frame");
            _depthModeResetLogged = true;
            _cam.depthTextureMode |= Needed;
        }
        // FSR's jitter sequence length grows with the square of the upscale factor.
        float scale = _displayW / (float)rw;
        int phases = Mathf.Max(8, Mathf.CeilToInt(8f * scale * scale));
        int i = (_frame % phases) + 1;
        _jx = Halton(i, 2) - 0.5f;
        _jy = Halton(i, 3) - 0.5f;
        _cam.ResetProjectionMatrix();
        Matrix4x4 p = _cam.projectionMatrix;
        _cam.nonJitteredProjectionMatrix = p;
        Vector3 offset = new Vector3(2f * _jx / rw, 2f * _jy / rh, 0f);
        _cam.projectionMatrix = Matrix4x4.TRS(offset, Quaternion.identity, Vector3.one) * p;
        _cam.useJitteredProjectionMatrixForTransparentRendering = true;
        if (Time.realtimeSinceStartup >= _nextMipScan)
        {
            _mipBias.Apply(rw < _displayW ? (Mathf.Log(rw / (float)_displayW, 2f) - 1f) * Sharpening : 0f);
            _nextMipScan = Time.realtimeSinceStartup + 5f;
        }
    }

    void OnPreRender()
    {
        _cb.Clear();
        if (!_active) return;
        FrameData d = new FrameData();
        d.color = _colorPtr;
        d.depth = _depthPtr;
        d.motion = _motionPtr;
        d.output = _outputPtr;
        d.width = _renderW;
        d.height = _renderH;
        d.displayWidth = _displayW;
        d.displayHeight = _displayH;
        // Unity keeps render textures bottom-up on D3D; the inputs are flipped into top-down (D3D) order
        // for FSR and the network, which turns the vertical jitter and motion directions around.
        d.jitterX = _jx;
        d.jitterY = -_jy;
        d.mvScaleX = -_renderW;  // Unity motion vectors are (current - previous) in UV units, v pointing up
        d.mvScaleY = _renderH;
        d.frameTimeMs = Time.unscaledDeltaTime * 1000f;
        d.cameraNear = _cam.nearClipPlane;
        d.cameraFar = _cam.farClipPlane;
        d.fovY = _cam.fieldOfView * Mathf.Deg2Rad;
        // A scene load moves the camera anywhere: no history (FSR's and the motion vectors') carries over.
        if (SuperController.singleton != null && SuperController.singleton.isLoading) _reset = true;
        d.reset = _reset ? 1 : 0;
        d.hdr = _cam.allowHDR ? 1 : 0;
        d.exposure = Exposure;
        // Object motion: where the scene was drawn (jittered, GPU convention, top-down like our copies),
        // and the non-jittered view-projection of this and the last frame for the motion itself.
        Matrix4x4 view = _cam.worldToCameraMatrix;
        Matrix4x4 vp = _cam.nonJitteredProjectionMatrix * view;
        if (_reset) _prevVP = vp;
        Store(GL.GetGPUProjectionMatrix(_cam.projectionMatrix, false) * view, _rasterVP);
        Store(vp, _viewProj);
        Store(_prevVP, _prevViewProj);
        _prevVP = vp;
        d.rasterViewProj = _rasterVP;
        d.viewProj = _viewProj;
        d.prevViewProj = _prevViewProj;
        d.meshDraws = new IntPtr(_drawRing.ToInt64() + (long)(_frame % RingSize) * SkinMotion.DrawSize * SkinMotion.MaxDraws);
        d.meshDrawCount = ObjectMotion ? _skinMotion.Collect(d.meshDraws, SkinMotion.MaxDraws) : 0;
        IntPtr slot = new IntPtr(_ring.ToInt64() + (long)(_frame % RingSize) * _slotSize);
        Marshal.StructureToPtr(d, slot, false);
        _cb.Blit(_target, _color, FlipScale, FlipOffset);
        _cb.Blit(BuiltinRenderTextureType.Depth, _depth, FlipScale, FlipOffset);
        _cb.Blit(BuiltinRenderTextureType.MotionVectors, _motion, FlipScale, FlipOffset);
        _cb.Blit(_target, _output, FlipScale, FlipOffset);  // plain upscale; replaced by FSR's result whenever it runs
        _cb.IssuePluginEventAndData(s_renderEvent, 1, slot);
        _reset = false;
        _frame++;
    }

    void EnsureTextures(int rw, int rh, int dw, int dh)
    {
        if (_target != null && _renderW == rw && _renderH == rh && _output.width == dw && _output.height == dh) return;
        if (_target != null && _cam.targetTexture == _target) _cam.targetTexture = null;
        ReleaseTextures();
        _target = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        _target.name = "DlssNrAmd camera target";
        _target.Create();
        _color = MakeTexture(rw, rh, RenderTextureFormat.ARGBHalf, "colour");
        _depth = MakeTexture(rw, rh, RenderTextureFormat.RFloat, "depth");
        _motion = MakeTexture(rw, rh, RenderTextureFormat.RGHalf, "motion");
        _output = MakeTexture(dw, dh, RenderTextureFormat.ARGBHalf, "output");
        _colorPtr = _color.GetNativeTexturePtr();
        _depthPtr = _depth.GetNativeTexturePtr();
        _motionPtr = _motion.GetNativeTexturePtr();
        _outputPtr = _output.GetNativeTexturePtr();
        _presentCb.Clear();
        _presentCb.Blit(_output, BuiltinRenderTextureType.CameraTarget, FlipScale, FlipOffset);
        _renderW = rw;
        _renderH = rh;
        _reset = true;
        _nextMipScan = 0f;
        if (Log != null) Log.LogInfo(SizeText);
    }

    static RenderTexture MakeTexture(int w, int h, RenderTextureFormat format, string name)
    {
        RenderTexture rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear);
        rt.name = "DlssNrAmd " + name;
        rt.Create();
        return rt;
    }

    void ReleaseTextures()
    {
        if (_presentCb != null) _presentCb.Clear();
        foreach (RenderTexture rt in new RenderTexture[] { _target, _color, _depth, _motion, _output })
        {
            if (rt == null) continue;
            rt.Release();
            Destroy(rt);
        }
        _target = _color = _depth = _motion = _output = null;
        _renderW = _renderH = 0;
    }

    // Unity's Matrix4x4 memory order (column-major), as HLSL's default constant-buffer packing expects.
    static void Store(Matrix4x4 m, float[] a)
    {
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                a[c * 4 + r] = m[r, c];
    }

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }
        return r;
    }
}

// The presenter camera's image effect: hands its picture (upscaled frame + interface) to the screen
// (see CameraHook.CreatePresenter).
public class Presenter : MonoBehaviour
{
    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        Graphics.Blit(src, dst);
    }
}
