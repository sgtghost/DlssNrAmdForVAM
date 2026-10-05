// Object motion for VaM's GPU-skinned meshes: see motion.h.
#include "motion.h"

#include <windows.h>
#include <d3d11_1.h>

#include <cstdio>
#include <cstring>
#include <unordered_map>

namespace motion
{
namespace
{
// Positions come from VaM's buffers by vertex index, so no vertex layout is involved. A fragment is
// kept only where its depth matches the scene's: that resolves occlusion between and within meshes, and
// skips anything VaM drew invisibly (hidden heads, teeth behind closed lips). Output is Unity's motion
// vector convention: (current - previous) in UV units, v up.
const char kShader[] = R"(
cbuffer Params : register(b0)
{
    float4x4 rasterVP;
    float4x4 curVP;
    float4x4 prevVP;
    float4 curOffset;    // xyz; w = base vertex
    float4 prevOffset;   // xyz
    float4 depthParams;  // x, y: eye depth from reversed Z; z: relative tolerance; w: absolute tolerance (m)
};
#ifdef HAIR
struct Particle { float3 p; float rest[26]; };  // GPUTools hair GPParticle, 116 bytes, position first
StructuredBuffer<Particle> curPos : register(t0);
StructuredBuffer<Particle> prevPos : register(t1);
#define POS(b, i) b[i].p
#else
StructuredBuffer<float3> curPos : register(t0);
StructuredBuffer<float3> prevPos : register(t1);
#define POS(b, i) b[i]
#endif
Texture2D<float> sceneDepth : register(t2);

struct V
{
    float4 pos : SV_Position;
    float4 cur : TEXCOORD0;
    float4 prev : TEXCOORD1;
};

V vsMain(uint id : SV_VertexID)
{
    uint i = id + (uint)curOffset.w;
    float4 p = float4(POS(curPos, i) + curOffset.xyz, 1.0);
    float4 q = float4(POS(prevPos, i) + prevOffset.xyz, 1.0);
    V o;
    o.pos = mul(rasterVP, p);
    o.cur = mul(curVP, p);
    o.prev = mul(prevVP, q);
    return o;
}

float eyeDepth(float d) { return 1.0 / (depthParams.x * d + depthParams.y); }

float2 psMain(V i) : SV_Target
{
    float scene = eyeDepth(sceneDepth.Load(int3(i.pos.xy, 0)));
    float frag = eyeDepth(i.pos.z);
    if (abs(frag - scene) > depthParams.z * scene + depthParams.w || i.cur.w < 1e-4 || i.prev.w < 1e-4) discard;
    return (i.cur.xy / i.cur.w - i.prev.xy / i.prev.w) * 0.5;
}
)";

// Depth match: an absolute part per draw (VaM tessellates skin, so the redrawn base mesh is near, not on, the
// surface; hair strands spread around their guides) plus a small share of the distance. A large share lets
// surfaces a few cm behind or in front pass as visible at a distance.
const float kRelTolerance = 0.003f;
const uint64_t kEvictFrames = 300;

typedef HRESULT(WINAPI* PfnD3DCompile)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*, ID3DInclude*, LPCSTR, LPCSTR, UINT,
                                       UINT, ID3DBlob**, ID3DBlob**);

struct Params
{
    float rasterVP[16], curVP[16], prevVP[16];
    float curOffset[4], prevOffset[4], depthParams[4];
};

template <class T> void release(T*& p)
{
    if (p)
    {
        p->Release();
        p = nullptr;
    }
}

// History of one skinned object.
struct Item
{
    ID3D11Buffer* src = nullptr;  // VaM buffer the history follows (kept alive by its cached view)
    ID3D11Buffer* prev = nullptr;
    ID3D11ShaderResourceView* prevSrv = nullptr;
    float offset[3] = {}, prevOffset[3] = {};
    uint64_t lastFrame = 0;
};

struct View
{
    ID3D11ShaderResourceView* srv = nullptr;
    uint64_t lastFrame = 0;
};

bool g_init = false, g_broken = false;
ID3D11Device1* g_dev = nullptr;
ID3D11DeviceContext1* g_ctx = nullptr;
ID3DDeviceContextState* g_state = nullptr;
ID3D11VertexShader* g_vs = nullptr;      // positions as float3
ID3D11VertexShader* g_vsHair = nullptr;  // positions in hair particles
ID3D11PixelShader* g_ps = nullptr;
ID3D11Buffer* g_cb = nullptr;
ID3D11RasterizerState* g_rs = nullptr;
std::unordered_map<int32_t, Item> g_items;
std::unordered_map<ID3D11Buffer*, View> g_views;  // VaM position buffers
void* g_rtvTex = nullptr;
ID3D11RenderTargetView* g_rtv = nullptr;
void* g_depthTex = nullptr;
ID3D11ShaderResourceView* g_depthSrv = nullptr;
uint64_t g_frame = 0;
int g_lastDraws = 0, g_lastObjects = 0, g_skipped = 0;

bool fail(const char* what, HRESULT hr)
{
    dnaLog("object motion disabled: %s (hr=0x%08lX)", what, (unsigned long)hr);
    g_broken = true;
    return false;
}

bool init(ID3D11Device* dev, ID3D11DeviceContext* ctx)
{
    g_init = true;
    HRESULT hr = dev->QueryInterface(IID_PPV_ARGS(&g_dev));
    if (SUCCEEDED(hr)) hr = ctx->QueryInterface(IID_PPV_ARGS(&g_ctx));
    if (FAILED(hr)) return fail("Direct3D 11.1 unavailable", hr);
    const D3D_FEATURE_LEVEL fl = dev->GetFeatureLevel();
    hr = g_dev->CreateDeviceContextState(0, &fl, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device1), nullptr, &g_state);
    if (FAILED(hr))
        hr = g_dev->CreateDeviceContextState(D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED, &fl, 1, D3D11_SDK_VERSION,
                                             __uuidof(ID3D11Device1), nullptr, &g_state);
    if (FAILED(hr)) return fail("CreateDeviceContextState", hr);

    HMODULE dc = LoadLibraryW(L"d3dcompiler_47.dll");
    auto compile = dc ? reinterpret_cast<PfnD3DCompile>(reinterpret_cast<void*>(GetProcAddress(dc, "D3DCompile"))) : nullptr;
    if (!compile) return fail("d3dcompiler_47.dll not available", HRESULT_FROM_WIN32(GetLastError()));
    ID3DBlob *vsb = nullptr, *vhb = nullptr, *psb = nullptr, *err = nullptr;
    const D3D_SHADER_MACRO hair[] = {{"HAIR", "1"}, {nullptr, nullptr}};
    hr = compile(kShader, sizeof kShader - 1, "dlssnramd_motion", nullptr, nullptr, "vsMain", "vs_5_0", 0, 0, &vsb, &err);
    if (SUCCEEDED(hr)) hr = compile(kShader, sizeof kShader - 1, "dlssnramd_motion", hair, nullptr, "vsMain", "vs_5_0", 0, 0, &vhb, &err);
    if (SUCCEEDED(hr)) hr = compile(kShader, sizeof kShader - 1, "dlssnramd_motion", nullptr, nullptr, "psMain", "ps_5_0", 0, 0, &psb, &err);
    if (FAILED(hr))
    {
        if (err) dnaLog("motion shader: %.*s", int(err->GetBufferSize()), static_cast<const char*>(err->GetBufferPointer()));
        release(err);
        release(vsb);
        release(vhb);
        return fail("shader compilation", hr);
    }
    release(err);
    hr = dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &g_vs);
    if (SUCCEEDED(hr)) hr = dev->CreateVertexShader(vhb->GetBufferPointer(), vhb->GetBufferSize(), nullptr, &g_vsHair);
    if (SUCCEEDED(hr)) hr = dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &g_ps);
    release(vsb);
    release(vhb);
    release(psb);
    if (FAILED(hr)) return fail("CreateVertexShader/CreatePixelShader", hr);

    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = (sizeof(Params) + 15) & ~15u;
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(hr = dev->CreateBuffer(&bd, nullptr, &g_cb))) return fail("constant buffer", hr);
    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;  // the depth test decides visibility
    rd.DepthClipEnable = TRUE;
    if (FAILED(hr = dev->CreateRasterizerState(&rd, &g_rs))) return fail("rasterizer state", hr);
    dnaLog("object motion ready");
    return true;
}

bool views(const DnaFrameData& f)
{
    HRESULT hr = S_OK;
    if (g_rtvTex != f.motion)
    {
        release(g_rtv);
        g_rtvTex = nullptr;
        D3D11_RENDER_TARGET_VIEW_DESC d{};
        d.Format = DXGI_FORMAT_R16G16_FLOAT;
        d.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        if (FAILED(hr = g_dev->CreateRenderTargetView(static_cast<ID3D11Resource*>(f.motion), &d, &g_rtv)))
            return fail("motion texture view", hr);
        g_rtvTex = f.motion;
    }
    if (g_depthTex != f.depth)
    {
        release(g_depthSrv);
        g_depthTex = nullptr;
        D3D11_SHADER_RESOURCE_VIEW_DESC d{};
        d.Format = DXGI_FORMAT_R32_FLOAT;
        d.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        d.Texture2D.MipLevels = 1;
        if (FAILED(hr = g_dev->CreateShaderResourceView(static_cast<ID3D11Resource*>(f.depth), &d, &g_depthSrv)))
            return fail("depth texture view", hr);
        g_depthTex = f.depth;
    }
    return true;
}

ID3D11ShaderResourceView* bufferView(ID3D11Buffer* b, UINT elements)
{
    View& v = g_views[b];
    v.lastFrame = g_frame;
    if (v.srv) return v.srv;
    D3D11_SHADER_RESOURCE_VIEW_DESC d{};
    d.Format = DXGI_FORMAT_UNKNOWN;
    d.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    d.Buffer.FirstElement = 0;
    d.Buffer.NumElements = elements;
    if (FAILED(g_dev->CreateShaderResourceView(b, &d, &v.srv))) v.srv = nullptr;
    return v.srv;
}

// Starts (or restarts) an object's history at the current positions: zero motion of its own this frame.
bool resetItem(Item& it, ID3D11Buffer* src, const D3D11_BUFFER_DESC& desc, const float* offset)
{
    release(it.prevSrv);
    release(it.prev);
    it.src = nullptr;
    if (FAILED(g_dev->CreateBuffer(&desc, nullptr, &it.prev))) return false;
    D3D11_SHADER_RESOURCE_VIEW_DESC d{};
    d.Format = DXGI_FORMAT_UNKNOWN;
    d.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    d.Buffer.NumElements = desc.ByteWidth / desc.StructureByteStride;
    if (FAILED(g_dev->CreateShaderResourceView(it.prev, &d, &it.prevSrv)))
    {
        release(it.prev);
        return false;
    }
    g_ctx->CopyResource(it.prev, src);
    memcpy(it.prevOffset, offset, sizeof it.prevOffset);
    it.src = src;
    return true;
}

void evict()
{
    for (auto i = g_items.begin(); i != g_items.end();)
        if (g_frame - i->second.lastFrame > kEvictFrames)
        {
            release(i->second.prevSrv);
            release(i->second.prev);
            i = g_items.erase(i);
        }
        else
            ++i;
    for (auto i = g_views.begin(); i != g_views.end();)
        if (g_frame - i->second.lastFrame > kEvictFrames)
        {
            release(i->second.srv);
            i = g_views.erase(i);
        }
        else
            ++i;
}
}  // namespace

void draw(ID3D11Device* dev, ID3D11DeviceContext* ctx, const DnaFrameData& f)
{
    ++g_frame;
    if (g_frame % 60 == 0) evict();
    if (f.meshDrawCount <= 0 || !f.meshDraws || g_broken)
    {
        g_lastDraws = g_lastObjects = 0;
        return;
    }
    if (!g_init && !init(dev, ctx)) return;
    if (!views(f)) return;

    ID3DDeviceContextState* unityState = nullptr;
    g_ctx->SwapDeviceContextState(g_state, &unityState);
    g_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g_ctx->VSSetShader(g_vs, nullptr, 0);
    g_ctx->PSSetShader(g_ps, nullptr, 0);
    g_ctx->VSSetConstantBuffers(0, 1, &g_cb);
    g_ctx->PSSetConstantBuffers(0, 1, &g_cb);
    g_ctx->PSSetShaderResources(2, 1, &g_depthSrv);
    g_ctx->RSSetState(g_rs);
    const D3D11_VIEWPORT vp{0.0f, 0.0f, float(f.width), float(f.height), 0.0f, 1.0f};
    g_ctx->RSSetViewports(1, &vp);
    g_ctx->OMSetRenderTargets(1, &g_rtv, nullptr);

    Params p{};
    memcpy(p.rasterVP, f.rasterViewProj, sizeof p.rasterVP);
    memcpy(p.curVP, f.viewProj, sizeof p.curVP);
    memcpy(p.prevVP, f.prevViewProj, sizeof p.prevVP);
    // Unity's _ZBufferParams for reversed Z: eye depth = 1 / (z * d + w).
    const float x = -1.0f + f.cameraFar / f.cameraNear;
    p.depthParams[0] = x / f.cameraFar;
    p.depthParams[1] = 1.0f / f.cameraFar;
    p.depthParams[2] = kRelTolerance;

    int draws = 0, skipped = 0;
    std::unordered_map<int32_t, Item*> used;
    for (int i = 0; i < f.meshDrawCount; ++i)
    {
        const DnaMeshDraw& m = f.meshDraws[i];
        ID3D11Buffer* src = static_cast<ID3D11Buffer*>(m.verts);
        if (!src || !m.indices || m.indexCount == 0)
        {
            ++skipped;
            continue;
        }
        D3D11_BUFFER_DESC desc{};
        src->GetDesc(&desc);
        if ((m.stride != 12 && m.stride != 116) || desc.StructureByteStride != m.stride ||
            !(desc.MiscFlags & D3D11_RESOURCE_MISC_BUFFER_STRUCTURED))
        {
            ++skipped;
            continue;
        }
        ID3D11ShaderResourceView* curSrv = bufferView(src, desc.ByteWidth / m.stride);
        Item& it = g_items[m.key];
        const float offset[3] = {m.offsetX, m.offsetY, m.offsetZ};
        if (!used.count(m.key))
        {
            // A new object, a different VaM buffer, or an object that was not drawn last frame starts over.
            D3D11_BUFFER_DESC pd{};
            if (it.prev) it.prev->GetDesc(&pd);
            if (it.src != src || !it.prev || pd.ByteWidth != desc.ByteWidth || it.lastFrame + 1 != g_frame)
                if (!resetItem(it, src, desc, offset))
                {
                    ++skipped;
                    continue;
                }
            memcpy(it.offset, offset, sizeof it.offset);
            it.lastFrame = g_frame;
            used[m.key] = &it;
        }
        if (!curSrv || !it.prevSrv)
        {
            ++skipped;
            continue;
        }
        memcpy(p.curOffset, offset, sizeof offset);
        p.curOffset[3] = float(m.baseVertex);
        memcpy(p.prevOffset, it.prevOffset, sizeof it.prevOffset);
        p.depthParams[3] = m.tolerance;
        g_ctx->VSSetShader(m.stride == 116 ? g_vsHair : g_vs, nullptr, 0);
        D3D11_MAPPED_SUBRESOURCE ms{};
        if (FAILED(g_ctx->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) break;
        memcpy(ms.pData, &p, sizeof p);
        g_ctx->Unmap(g_cb, 0);
        ID3D11ShaderResourceView* srvs[2] = {curSrv, it.prevSrv};
        g_ctx->VSSetShaderResources(0, 2, srvs);
        g_ctx->IASetIndexBuffer(static_cast<ID3D11Buffer*>(m.indices), m.index32 ? DXGI_FORMAT_R32_UINT : DXGI_FORMAT_R16_UINT, 0);
        g_ctx->DrawIndexed(m.indexCount, m.indexStart, 0);
        ++draws;
    }
    g_ctx->ClearState();
    // This frame's positions become next frame's history.
    for (auto& u : used)
    {
        g_ctx->CopyResource(u.second->prev, u.second->src);
        memcpy(u.second->prevOffset, u.second->offset, sizeof u.second->prevOffset);
    }
    g_ctx->SwapDeviceContextState(unityState, nullptr);
    release(unityState);
    if (g_lastDraws == 0 && draws > 0) dnaLog("object motion: %d draws for %d objects", draws, int(used.size()));
    g_lastDraws = draws;
    g_lastObjects = int(used.size());
    g_skipped = skipped;
}

void shutdown()
{
    for (auto& i : g_items)
    {
        release(i.second.prevSrv);
        release(i.second.prev);
    }
    g_items.clear();
    for (auto& v : g_views) release(v.second.srv);
    g_views.clear();
    release(g_rtv);
    release(g_depthSrv);
    g_rtvTex = g_depthTex = nullptr;
    release(g_rs);
    release(g_cb);
    release(g_ps);
    release(g_vsHair);
    release(g_vs);
    release(g_state);
    release(g_ctx);
    release(g_dev);
    g_init = g_broken = false;
}

std::string status()
{
    char buf[96];
    snprintf(buf, sizeof buf, "object motion %d draws / %d objects%s", g_lastDraws, g_lastObjects,
             g_broken ? " (disabled)" : g_skipped ? " (some skipped)" : "");
    return buf;
}
}  // namespace motion
