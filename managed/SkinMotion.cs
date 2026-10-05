// Collects what VaM draws from GPU-computed positions this frame for the native object-motion pass
// (native/src/motion.h): character skin and clothing (mirroring when DAZSkinV2.DrawMeshGPU and
// DAZSkinWrap.DrawMeshGPU draw, which submeshes, with which position buffer and offset), and simulated
// hair. Hair strands are tessellated by VaM's shader between neighbouring guide strands, so they are
// stood in for by thin prisms between the guides' physics particles (the scalp triangles, segment by
// segment); interpolated strands lie inside them and move with them.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

public class SkinMotion
{
    public const int MaxDraws = 512;
    public static readonly int DrawSize = Marshal.SizeOf(typeof(MeshDraw));
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const float SignatureInterval = 0.5f;
    const float SettleDelay = 2f;  // search once things have settled: VaM builds clothing, hair and loaded scenes over a moment

    static readonly FieldInfo SkinWasInit = typeof(DAZSkinV2).GetField("_wasInit", Any);
    static readonly FieldInfo SkinSuspend = typeof(DAZSkinV2).GetField("_renderSuspend", Any);
    static readonly FieldInfo SkinMesh = typeof(DAZSkinV2).GetField("mesh", Any);
    static readonly FieldInfo SkinSmoothing = typeof(DAZSkinV2).GetField("_useSmoothing", Any);
    static readonly FieldInfo RunVerts = typeof(DAZCharacterRun).GetField("mergedMeshMorphedUVVertices", Any);
    static readonly FieldInfo RunSuspend = typeof(DAZCharacterRun).GetField("_renderSuspend", Any);
    static readonly FieldInfo WrapUpdateDrawDisabled = typeof(DAZSkinWrap).GetField("_updateDrawDisabled", Any);
    static readonly FieldInfo WrapIsWrapping = typeof(DAZSkinWrap).GetField("isWrapping", Any);
    static readonly FieldInfo WrapSuspend = typeof(DAZSkinWrap).GetField("_renderSuspend", Any);
    static readonly FieldInfo WrapMesh = typeof(DAZSkinWrap).GetField("mesh", Any);
    static readonly FieldInfo WrapDelayed = typeof(DAZSkinWrap).GetField("_delayedVertsBuffer", Any);
    static readonly FieldInfo WrapDrawVerts = typeof(DAZSkinWrap).GetField("_drawVerticesBuffer", Any);
    static readonly FieldInfo WrapWrapVerts = typeof(DAZSkinWrap).GetField("_wrapVerticesBuffer", Any);

    const float SkinTolerance = 0.006f;  // metres, plus 0.3 % of the distance (native side)
    const float HairTolerance = 0.03f;   // interpolated strands spread up to a few cm around the guides
    static readonly Type HairRenderType = typeof(DAZSkinV2).Assembly.GetType("GPUTools.Hair.Scripts.Runtime.Render.HairRender");
    static readonly FieldInfo HairData = HairRenderType == null ? null : HairRenderType.GetField("data", Any);
    static readonly FieldInfo HairRend = HairRenderType == null ? null : HairRenderType.GetField("rend", Any);

    class MeshInfo
    {
        public IntPtr indices;
        public uint index32;
        public uint[] start, count, baseVertex;
    }

    class HairInfo
    {
        public Mesh mesh;  // index buffer only: the prisms, over the hair's physics particles
        public IntPtr indices;
        public uint count, index32;
        public int particles, scalpIndices;
    }

    public readonly bool Available;
    public bool HairMotion = true;
    public float LastRescanMs;  // diagnostics
    public int LastRescanFrame = -1;
    public string Text { get { return _text; } }

    DAZSkinV2[] _skins = new DAZSkinV2[0];
    DAZSkinWrap[] _wraps = new DAZSkinWrap[0];
    DAZCharacterRun[] _runs = new DAZCharacterRun[0];
    Component[] _hairs = new Component[0];
    readonly Dictionary<Component, HairInfo> _hairInfo = new Dictionary<Component, HairInfo>();
    readonly HashSet<DAZSkinV2> _toDraw = new HashSet<DAZSkinV2>();
    readonly Dictionary<ComputeBuffer, IntPtr> _bufferPtrs = new Dictionary<ComputeBuffer, IntPtr>();
    readonly Dictionary<Mesh, MeshInfo> _meshes = new Dictionary<Mesh, MeshInfo>();
    readonly HashSet<object> _seen = new HashSet<object>();
    // Searching the scene for these objects costs ~100 ms in VaM (FindObjectsOfType walks every object), so it
    // only happens when something that holds them can have changed (CheckForChanges), never on a timer.
    DAZCharacterSelector[] _selectors = new DAZCharacterSelector[0];
    bool _dirty = true, _lost, _wasLoading;  // _dirty: search right away (after switching on); _lost: a tracked object was destroyed
    float _rescanAt = -1f, _nextSignature;
    int _signature;
    string _text = "object motion: not scanned";

    public SkinMotion(ManualLogSource log)
    {
        foreach (FieldInfo f in new FieldInfo[] { SkinWasInit, SkinSuspend, SkinMesh, SkinSmoothing, RunVerts, RunSuspend,
                                                   WrapUpdateDrawDisabled, WrapIsWrapping, WrapSuspend, WrapMesh, WrapDelayed,
                                                   WrapDrawVerts, WrapWrapVerts })
            if (f == null)
            {
                if (log != null) log.LogWarning("object motion unavailable: a VaM field it reads is missing (different VaM version?)");
                return;
            }
        Available = true;
    }

    // Writes up to max MeshDraw entries to dst; returns how many.
    public int Collect(IntPtr dst, int max)
    {
        if (!Available) return 0;
        CheckForChanges();
        int n = 0, objects = 0, hairs = 0;
        // Hair first: where it lies on skin or clothing, their tighter depth match decides.
        if (HairMotion && HairData != null && HairRend != null)
            foreach (Component h in _hairs)
            {
                if (n >= max) break;
                if (h == null)
                {
                    _lost = true;  // destroyed: search again once things settle
                    continue;
                }
                if (!h.gameObject.activeInHierarchy) continue;
                MeshRenderer r = (MeshRenderer)HairRend.GetValue(h);
                object data = HairData.GetValue(h);
                if (r == null || !r.enabled || data == null) continue;
                ComputeBuffer particles = GpuBufferOf(data, "Particles");
                if (particles == null || particles.stride != 116) continue;
                HairInfo hi = HairGeometry(h, data, particles.count);
                IntPtr vp = BufferPtr(particles);
                if (hi == null || vp == IntPtr.Zero) continue;
                MeshDraw d = new MeshDraw();
                d.verts = vp;
                d.indices = hi.indices;
                d.indexCount = hi.count;
                d.index32 = hi.index32;
                d.key = h.GetInstanceID();
                d.stride = 116;
                d.tolerance = HairTolerance;
                Marshal.StructureToPtr(d, new IntPtr(dst.ToInt64() + (long)n * DrawSize), false);
                n++;
                hairs++;
            }
        _toDraw.Clear();
        foreach (DAZSkinV2 s in _skins)
        {
            if (s == null) _lost = true;
            else if (s.isActiveAndEnabled && s.skin && s.draw && s.skinMethod != DAZSkinV2.SkinMethod.CPU &&
                     (bool)SkinWasInit.GetValue(s))
                _toDraw.Add(s);
        }
        foreach (DAZCharacterRun r in _runs)
        {
            if (r == null) _lost = true;
            else if (r.isActiveAndEnabled && r.skin != null && r.doDraw && RunVerts.GetValue(r) != null &&
                     !(bool)RunSuspend.GetValue(r))
                _toDraw.Add(r.skin);
        }
        if (!DAZSkinV2.staticDraw)
            foreach (DAZSkinV2 s in _toDraw)
            {
                if ((bool)SkinSuspend.GetValue(s)) continue;
                ComputeBuffer verts = s.delayDisplayOneFrame ? s.delayedVertsBuffer
                                    : (bool)SkinSmoothing.GetValue(s) ? s.smoothedVertsBuffer : s.rawVertsBuffer;
                int added = Add(dst, n, max, (Mesh)SkinMesh.GetValue(s), verts, s.GetInstanceID(), s.drawOffset,
                                s.materialsEnabled, s.GPUuseSimpleMaterial && s.GPUsimpleMaterial != null, s.GPUmaterials);
                n += added;
                if (added > 0) objects++;
            }
        if (!DAZSkinWrap.staticDraw)
            foreach (DAZSkinWrap w in _wraps)
            {
                if (w == null)
                {
                    _lost = true;
                    continue;
                }
                if (w.skin == null || (bool)WrapSuspend.GetValue(w)) continue;
                bool postProcess = (bool)WrapUpdateDrawDisabled.GetValue(w);
                ComputeBuffer verts;
                if (postProcess)
                {
                    // Drawn from the cloth simulation's dispatch (PostProcessDispatch), never delayed.
                    if (!w.drawInPostProcess || WrapWrapVerts.GetValue(w) == null) continue;
                    verts = (ComputeBuffer)WrapDrawVerts.GetValue(w);
                }
                else
                {
                    if (!w.isActiveAndEnabled || !w.draw || w.dazMesh == null || (bool)WrapIsWrapping.GetValue(w) ||
                        w.skin.skinMethod == DAZSkinV2.SkinMethod.CPU || w.forceCPU)
                        continue;
                    verts = (ComputeBuffer)(w.skin.delayDisplayOneFrame ? WrapDelayed.GetValue(w) : WrapDrawVerts.GetValue(w));
                }
                int added = Add(dst, n, max, (Mesh)WrapMesh.GetValue(w), verts, w.GetInstanceID(), Vector3.zero,
                                w.materialsEnabled, w.GPUuseSimpleMaterial && w.GPUsimpleMaterial != null, w.GPUmaterials);
                n += added;
                if (added > 0) objects++;
            }
        _text = "object motion: " + objects + " meshes, " + hairs + " hair, " + n + " draws (" + _skins.Length + " skins, " +
                _wraps.Length + " wraps, " + _runs.Length + " characters, " + _hairs.Length + " hair)";
        return n;
    }

    static ComputeBuffer GpuBufferOf(object data, string name)
    {
        PropertyInfo p = data.GetType().GetProperty(name);
        object gb = p == null ? null : p.GetValue(data, null);
        PropertyInfo cb = gb == null ? null : gb.GetType().GetProperty("ComputeBuffer");
        return cb == null ? null : (ComputeBuffer)cb.GetValue(gb, null);
    }

    // The prisms of one hair item: for every scalp triangle of guide strands and every segment, the
    // triangle through the three guides' particles plus the three quads up to the next segment.
    // Physics particles are strand-major: guide g, segment s is particle g * segments + s.
    HairInfo HairGeometry(Component h, object data, int particleCount)
    {
        int[] scalp = (int[])data.GetType().GetProperty("Indices").GetValue(data, null);
        Vector4 size = (Vector4)data.GetType().GetProperty("Size").GetValue(data, null);
        int strands = (int)size.x, segs = (int)size.y;
        if (scalp == null || strands <= 0 || segs < 2 || strands * segs != particleCount) return null;
        HairInfo hi;
        if (_hairInfo.TryGetValue(h, out hi) && hi.mesh != null && hi.particles == particleCount && hi.scalpIndices == scalp.Length)
            return hi;
        if (hi != null && hi.mesh != null) UnityEngine.Object.Destroy(hi.mesh);
        List<int> idx = new List<int>(scalp.Length / 3 * (segs * 3 + (segs - 1) * 18));
        for (int t = 0; t + 2 < scalp.Length; t += 3)
        {
            if (scalp[t] >= strands || scalp[t + 1] >= strands || scalp[t + 2] >= strands) continue;
            int a = scalp[t] * segs, b = scalp[t + 1] * segs, c = scalp[t + 2] * segs;
            for (int s = 0; s < segs; s++)
            {
                idx.Add(a + s); idx.Add(b + s); idx.Add(c + s);
                if (s + 1 == segs) continue;
                Quad(idx, a + s, b + s);
                Quad(idx, b + s, c + s);
                Quad(idx, c + s, a + s);
            }
        }
        hi = new HairInfo();
        hi.mesh = new Mesh();
        hi.mesh.name = "DlssNrAmd hair motion";
        hi.mesh.indexFormat = particleCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        hi.mesh.vertices = new Vector3[particleCount];
        hi.mesh.SetIndices(idx.ToArray(), MeshTopology.Triangles, 0);
        hi.mesh.UploadMeshData(true);
        hi.indices = hi.mesh.GetNativeIndexBufferPtr();
        hi.count = (uint)idx.Count;
        hi.index32 = particleCount > 65535 ? 1u : 0u;
        hi.particles = particleCount;
        hi.scalpIndices = scalp.Length;
        _hairInfo[h] = hi;
        return hi;
    }

    // Two triangles between the strand segments starting at particles x and y.
    static void Quad(List<int> idx, int x, int y)
    {
        idx.Add(x); idx.Add(y); idx.Add(y + 1);
        idx.Add(x); idx.Add(y + 1); idx.Add(x + 1);
    }

    int Add(IntPtr dst, int n, int max, Mesh mesh, ComputeBuffer verts, int key, Vector3 offset, bool[] enabled, bool simple,
            Material[] materials)
    {
        if (mesh == null || verts == null) return 0;
        MeshInfo mi = Info(mesh);
        IntPtr vp = BufferPtr(verts);
        if (mi.indices == IntPtr.Zero || vp == IntPtr.Zero) return 0;
        int added = 0;
        MeshDraw d = new MeshDraw();
        d.verts = vp;
        d.indices = mi.indices;
        d.index32 = mi.index32;
        d.key = key;
        d.offsetX = offset.x;
        d.offsetY = offset.y;
        d.offsetZ = offset.z;
        d.stride = 12;
        d.tolerance = SkinTolerance;
        for (int i = 0; i < mi.start.Length && n + added < max; i++)
        {
            if (!simple && (enabled == null || i >= enabled.Length || !enabled[i] || materials == null || i >= materials.Length ||
                            materials[i] == null))
                continue;
            d.indexStart = mi.start[i];
            d.indexCount = mi.count[i];
            d.baseVertex = mi.baseVertex[i];
            Marshal.StructureToPtr(d, new IntPtr(dst.ToInt64() + (long)(n + added) * DrawSize), false);
            added++;
        }
        return added;
    }

    // Native pointers are looked up once per object: asking Unity for them waits for the render thread.
    IntPtr BufferPtr(ComputeBuffer b)
    {
        IntPtr p;
        if (!_bufferPtrs.TryGetValue(b, out p))
        {
            p = b.stride == 12 || b.stride == 116 ? b.GetNativeBufferPtr() : IntPtr.Zero;
            _bufferPtrs[b] = p;
        }
        _seen.Add(b);
        return p;
    }

    MeshInfo Info(Mesh m)
    {
        MeshInfo mi;
        if (!_meshes.TryGetValue(m, out mi))
        {
            mi = new MeshInfo();
            int subs = m.subMeshCount;
            mi.start = new uint[subs];
            mi.count = new uint[subs];
            mi.baseVertex = new uint[subs];
            for (int i = 0; i < subs; i++)
            {
                mi.start[i] = m.GetIndexStart(i);
                mi.count[i] = m.GetIndexCount(i);
                mi.baseVertex[i] = m.GetBaseVertex(i);
            }
            mi.index32 = m.indexFormat == IndexFormat.UInt32 ? 1u : 0u;
            mi.indices = m.GetNativeIndexBufferPtr();
            _meshes[m] = mi;
        }
        _seen.Add(m);
        return mi;
    }

    // One search, SettleDelay after the last change, when a scene load has ended, atoms came or went, a tracked
    // object was destroyed, or a character's look, clothing or hair changed; right away only after switching
    // on. Never while VaM loads.
    void CheckForChanges()
    {
        float now = Time.realtimeSinceStartup;
        SuperController sc = SuperController.singleton;
        if (sc != null && sc.isLoading)
        {
            _wasLoading = true;
            return;
        }
        if (_wasLoading)
        {
            _wasLoading = false;
            _rescanAt = now + SettleDelay;
        }
        if (_lost && _rescanAt < 0f) _rescanAt = now + SettleDelay;  // not pushed back while the dead entry stays listed
        _lost = false;
        if (now >= _nextSignature)
        {
            _nextSignature = now + SignatureInterval;
            int sig = Signature(sc);
            if (sig != _signature)
            {
                _signature = sig;
                _rescanAt = now + SettleDelay;  // each further change pushes the search back until things settle
            }
        }
        if (_dirty || (_rescanAt >= 0f && now >= _rescanAt))
        {
            _dirty = false;
            _rescanAt = -1f;
            Rescan();
        }
    }

    // Cheap fingerprint of what holds skinned meshes: the atom count and, per character, its look (and whether
    // its skin is built) and its active clothing and hair items with the wraps and hair renderers built under
    // them so far (VaM finishes those a while after a load or a switch). VaM's own lists, no scene search.
    int Signature(SuperController sc)
    {
        int sig = sc != null ? sc.GetAtoms().Count : 0;
        foreach (DAZCharacterSelector cs in _selectors)
        {
            if (cs == null)
            {
                sig = sig * 31 + 1;
                continue;
            }
            DAZCharacter ch = cs.selectedCharacter;
            sig = sig * 31 + (ch == null ? 0 : ch.GetInstanceID() * 4 + (ch.ready ? 1 : 0) + (ch.skin != null ? 2 : 0));
            DAZClothingItem[] clothes = cs.clothingItems;
            if (clothes != null)
                foreach (DAZClothingItem c in clothes)
                    if (c != null && c.active)
                    {
                        c.GetComponentsInChildren(s_wraps);
                        sig = sig * 31 + c.GetInstanceID() + s_wraps.Count * 7919;
                    }
            DAZHairGroup[] hair = cs.hairItems;
            if (hair != null)
                foreach (DAZHairGroup h in hair)
                    if (h != null && h.active)
                    {
                        h.GetComponentsInChildren(s_wraps);
                        int built = s_wraps.Count;
                        if (HairRenderType != null) built += h.GetComponentsInChildren(HairRenderType).Length * 101;
                        sig = sig * 31 + h.GetInstanceID() + built * 7919;
                    }
        }
        return sig;
    }

    static readonly List<DAZSkinWrap> s_wraps = new List<DAZSkinWrap>();

    void Rescan()
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        RescanObjects();
        LastRescanMs = (float)sw.Elapsed.TotalMilliseconds;
        LastRescanFrame = Time.frameCount;
    }

    void RescanObjects()
    {
        _selectors = (DAZCharacterSelector[])UnityEngine.Object.FindObjectsOfType(typeof(DAZCharacterSelector));
        _signature = Signature(SuperController.singleton);
        _skins = (DAZSkinV2[])UnityEngine.Object.FindObjectsOfType(typeof(DAZSkinV2));
        _wraps = (DAZSkinWrap[])UnityEngine.Object.FindObjectsOfType(typeof(DAZSkinWrap));
        _runs = (DAZCharacterRun[])UnityEngine.Object.FindObjectsOfType(typeof(DAZCharacterRun));
        if (HairRenderType != null)
        {
            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(HairRenderType);
            _hairs = new Component[found.Length];
            for (int i = 0; i < found.Length; i++) _hairs[i] = (Component)found[i];
        }
        List<Component> gone = new List<Component>();
        foreach (KeyValuePair<Component, HairInfo> kv in _hairInfo)
            if (kv.Key == null || Array.IndexOf(_hairs, kv.Key) < 0) gone.Add(kv.Key);
        foreach (Component h in gone)
        {
            if (_hairInfo[h].mesh != null) UnityEngine.Object.Destroy(_hairInfo[h].mesh);
            _hairInfo.Remove(h);
        }
        // Forget pointers of buffers and meshes not used since the last scan (released or replaced).
        List<ComputeBuffer> oldBuffers = new List<ComputeBuffer>();
        foreach (ComputeBuffer b in _bufferPtrs.Keys) if (!_seen.Contains(b)) oldBuffers.Add(b);
        foreach (ComputeBuffer b in oldBuffers) _bufferPtrs.Remove(b);
        List<Mesh> oldMeshes = new List<Mesh>();
        foreach (Mesh m in _meshes.Keys) if (m == null || !_seen.Contains(m)) oldMeshes.Add(m);
        foreach (Mesh m in oldMeshes) _meshes.Remove(m);
        _seen.Clear();
    }

    // Drops cached pointers, e.g. when the effect is switched off.
    public void Clear()
    {
        foreach (HairInfo hi in _hairInfo.Values)
            if (hi.mesh != null) UnityEngine.Object.Destroy(hi.mesh);
        _hairInfo.Clear();
        _bufferPtrs.Clear();
        _meshes.Clear();
        _seen.Clear();
        _dirty = true;
        _rescanAt = -1f;
    }
}
