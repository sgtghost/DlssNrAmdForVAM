// FSR bridge: see bridge.h.
//
// Per frame (Unity render thread): the camera's colour/depth/motion are copied into textures shared
// between Unity's D3D11 device and our D3D12 device, a shared fence hands them to D3D12, FSR runs
// there (the DLSS-NR mod hooks that ffxDispatch and runs its network inline), and D3D11 waits on the
// fence before copying the result back. The mod only attaches to a D3D12 device that presents a
// swapchain, so we keep a hidden one and present it every frame.
#include "bridge.h"

#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>

#include <atomic>
#include <cstring>
#include <mutex>
#include <vector>

#include "dump.h"
#include "motion.h"
#include "ffx_api.h"
#include "ffx_upscale.h"
#include "dx12/ffx_api_dx12.h"

namespace bridge
{
namespace
{
template <class T> void release(T*& p)
{
    if (p)
    {
        p->Release();
        p = nullptr;
    }
}

std::atomic<int> g_state{kIdle};
std::wstring g_ffxDir;
std::mutex g_versionMx;  // guards g_fsrPrefix: set from the main thread, read on the startup and render threads
std::string g_fsrPrefix;
std::atomic<int> g_versionRequest{0};  // bumped by setFsrVersion
int g_versionApplied = 0;              // render thread
std::mutex g_failMx;
std::string g_failure;

// D3D11: Unity's device (not owned). The immediate context is only touched on the render thread.
ID3D11Device* g_dev11 = nullptr;
ID3D11Device5* g_dev11v5 = nullptr;
ID3D11DeviceContext* g_ctx = nullptr;
ID3D11DeviceContext4* g_ctx4 = nullptr;
ID3D11Fence* g_fence11 = nullptr;
HANDLE g_fenceHandle = nullptr;
LUID g_luid{};
int g_startW = 0, g_startH = 0;

// D3D12 (owned)
IDXGIFactory6* g_factory = nullptr;
ID3D12Device* g_dev12 = nullptr;
ID3D12CommandQueue* g_queue = nullptr;
ID3D12Fence* g_fence12 = nullptr;  // g_fence11 opened on D3D12
UINT64 g_fenceValue = 0;           // shared counter, only advanced on one thread at a time
HANDLE g_cpuEvent = nullptr;
const int kSlots = 3;
ID3D12CommandAllocator* g_alloc[kSlots] = {};
UINT64 g_allocFence[kSlots] = {};
ID3D12GraphicsCommandList* g_list = nullptr;
HWND g_hwnd = nullptr;
DWORD g_windowThreadId = 0;
IDXGISwapChain3* g_swapchain = nullptr;

// FFX
HMODULE g_ffxLoader = nullptr, g_ffxUpscaler = nullptr;
PfnFfxCreateContext g_ffxCreate = nullptr;
PfnFfxDestroyContext g_ffxDestroy = nullptr;
PfnFfxDispatch g_ffxDispatch = nullptr;
PfnFfxQuery g_ffxQuery = nullptr;
uint64_t g_versionId = 0;
std::string g_versionName = "default";
// Providers the FFX DLLs offer: filled by the startup thread before kReady, read-only afterwards.
std::vector<uint64_t> g_versionIds;
std::vector<std::string> g_versionNames;
ffxContext g_ffx = nullptr;
// FFX keeps pointers into the create descriptors for the context's lifetime.
ffxCreateBackendDX12Desc g_beDesc{};
ffxOverrideVersion g_ovDesc{};
ffxCreateContextDescUpscale g_ctxDesc{};
std::string g_provider = "?";

// Per-size shared textures
struct SharedTex
{
    ID3D11Texture2D* t11 = nullptr;
    ID3D12Resource* t12 = nullptr;
};
SharedTex g_color, g_depth, g_motion, g_out;  // color/depth/motion at render size, out at display size
int g_w = 0, g_h = 0;    // render size
int g_dw = 0, g_dh = 0;  // display size
bool g_hdr = false;

// 1x1 exposure texture, refreshed every frame from a persistently mapped upload ring. Without it
// DLSS-NR-on-AMD estimates exposure itself, and VaM's near-black backgrounds drive that estimate to
// its cap: the network then sees a white frame and changes nothing.
const UINT kExposureStride = D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT;
ID3D12Resource* g_exposure = nullptr;
ID3D12Resource* g_exposureUpload = nullptr;
uint8_t* g_exposureMapped = nullptr;

std::atomic<int> g_dumpFrames{0};  // consecutive frames still to dump
int g_dumpIndex = 0;
std::mutex g_dumpMx;
std::wstring g_dumpDir;

// Stats (render thread)
uint64_t g_frames = 0, g_errors = 0;
uint32_t g_lastRc = 0;
double g_cpuMs = 0;
LARGE_INTEGER g_qpf{};

void setFailed(const char* why)
{
    {
        std::lock_guard<std::mutex> lk(g_failMx);
        g_failure = why;
    }
    dnaLog("FAILED: %s -- the camera image is shown without FSR/NR", why);
    g_state = kFailed;
}

bool failHr(const char* what, HRESULT hr)
{
    char buf[256];
    snprintf(buf, sizeof buf, "%s (hr=0x%08lX)", what, (unsigned long)hr);
    setFailed(buf);
    return false;
}

void ffxMessage(uint32_t type, const wchar_t* message)
{
    dnaLog("FFX %s: %ls", type == FFX_API_MESSAGE_TYPE_ERROR ? "error" : "warning", message);
}

void cpuWait(UINT64 value)
{
    if (g_fence12->GetCompletedValue() >= value) return;
    g_fence12->SetEventOnCompletion(value, g_cpuEvent);
    if (WaitForSingleObject(g_cpuEvent, 10000) != WAIT_OBJECT_0) dnaLog("GPU wait for fence %llu timed out", value);
}

void waitIdle()
{
    if (!g_queue || !g_fence12) return;
    const UINT64 v = ++g_fenceValue;
    g_queue->Signal(g_fence12, v);
    cpuWait(v);
}

// ---------------------------------------------------------------- startup (background thread)
LRESULT CALLBACK wndProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

DWORD WINAPI windowThread(void* ready)
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)&wndProc, &self);
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof wc;
    wc.lpfnWndProc = wndProc;
    wc.hInstance = self;
    wc.lpszClassName = L"DlssNrAmdHiddenSwapchain";
    RegisterClassExW(&wc);
    g_hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, L"DLSS-NR on AMD (hidden)",
                             WS_POPUP, 0, 0, g_startW, g_startH, nullptr, nullptr, self, nullptr);
    g_windowThreadId = GetCurrentThreadId();
    SetEvent(static_cast<HANDLE>(ready));
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (g_hwnd) DestroyWindow(g_hwnd);
    return 0;
}

void presentFor(int ms)
{
    for (int t = 0; t < ms; t += 16)
    {
        g_swapchain->Present(0, 0);
        Sleep(16);
    }
}

// The first offered provider whose name starts with the requested prefix ("3.1", "4.1"), else FFX's default.
void selectVersion()
{
    std::string prefix;
    {
        std::lock_guard<std::mutex> lk(g_versionMx);
        prefix = g_fsrPrefix;
    }
    g_versionId = 0;
    g_versionName = "default";
    for (size_t i = 0; i < g_versionNames.size(); ++i)
        if (!prefix.empty() && g_versionNames[i].compare(0, prefix.size(), prefix) == 0)
        {
            g_versionId = g_versionIds[i];
            g_versionName = g_versionNames[i];
            return;
        }
}

void pickVersion()
{
    uint64_t count = 0;
    ffxQueryDescGetVersions q{};
    q.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    q.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    q.device = g_dev12;
    q.outputCount = &count;
    if (g_ffxQuery(nullptr, &q.header) != FFX_API_RETURN_OK || count == 0)
    {
        dnaLog("FSR version query failed; using the default provider");
        return;
    }
    std::vector<uint64_t> ids(count);
    std::vector<const char*> names(count);
    q.versionIds = ids.data();
    q.versionNames = names.data();
    if (g_ffxQuery(nullptr, &q.header) != FFX_API_RETURN_OK) return;
    for (uint64_t i = 0; i < count; ++i)
    {
        g_versionIds.push_back(ids[i]);
        g_versionNames.push_back(names[i] ? names[i] : "?");
    }
    selectVersion();
    for (size_t i = 0; i < g_versionNames.size(); ++i)
        dnaLog("  FSR provider available: %s%s", g_versionNames[i].c_str(),
               g_versionId && g_versionIds[i] == g_versionId ? "  <- selected" : "");
}

bool startDevice()
{
    HMODULE d3d12 = LoadLibraryW(L"d3d12.dll");
    HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
    if (!d3d12 || !dxgi) return failHr("d3d12.dll/dxgi.dll not loadable", HRESULT_FROM_WIN32(GetLastError()));
    // The DLSS-NR mod waits for d3d12 + dxgi to appear and hooks them from its own thread.
    Sleep(3000);
    auto createDevice = reinterpret_cast<PFN_D3D12_CREATE_DEVICE>(reinterpret_cast<void*>(GetProcAddress(d3d12, "D3D12CreateDevice")));
    auto createFactory2 = reinterpret_cast<HRESULT(WINAPI*)(UINT, REFIID, void**)>(
        reinterpret_cast<void*>(GetProcAddress(dxgi, "CreateDXGIFactory2")));
    if (!createDevice || !createFactory2) return failHr("D3D12/DXGI exports missing", E_NOINTERFACE);
    HRESULT hr = createFactory2(0, IID_PPV_ARGS(&g_factory));
    if (FAILED(hr)) return failHr("CreateDXGIFactory2", hr);
    IDXGIAdapter1* adapter = nullptr;
    hr = g_factory->EnumAdapterByLuid(g_luid, IID_PPV_ARGS(&adapter));
    if (FAILED(hr)) return failHr("Unity's adapter not found for D3D12", hr);
    DXGI_ADAPTER_DESC1 ad{};
    adapter->GetDesc1(&ad);
    hr = createDevice(adapter, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&g_dev12));
    adapter->Release();
    if (FAILED(hr)) return failHr("D3D12CreateDevice", hr);
    dnaLog("D3D12 device %p on %ls (Unity's adapter)", (void*)g_dev12, ad.Description);

    D3D12_COMMAND_QUEUE_DESC qd{};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    if (FAILED(hr = g_dev12->CreateCommandQueue(&qd, IID_PPV_ARGS(&g_queue)))) return failHr("CreateCommandQueue", hr);
    g_queue->SetName(L"DlssNrAmd queue");
    hr = g_dev12->OpenSharedHandle(g_fenceHandle, IID_PPV_ARGS(&g_fence12));
    CloseHandle(g_fenceHandle);
    g_fenceHandle = nullptr;
    if (FAILED(hr)) return failHr("OpenSharedHandle(fence)", hr);
    g_cpuEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    for (auto& a : g_alloc)
        if (FAILED(hr = g_dev12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a))))
            return failHr("CreateCommandAllocator", hr);
    hr = g_dev12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_alloc[0], nullptr, IID_PPV_ARGS(&g_list));
    if (FAILED(hr)) return failHr("CreateCommandList", hr);
    g_list->Close();

    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC rd{};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    rd.Width = 1;
    rd.Height = 1;
    rd.DepthOrArraySize = 1;
    rd.MipLevels = 1;
    rd.Format = DXGI_FORMAT_R32_FLOAT;
    rd.SampleDesc.Count = 1;
    hr = g_dev12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr,
                                          IID_PPV_ARGS(&g_exposure));
    if (FAILED(hr)) return failHr("exposure texture", hr);
    heap.Type = D3D12_HEAP_TYPE_UPLOAD;
    rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    rd.Width = UINT64(kExposureStride) * kSlots;
    rd.Height = 1;
    rd.DepthOrArraySize = 1;
    rd.MipLevels = 1;
    rd.SampleDesc.Count = 1;
    rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    hr = g_dev12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                          IID_PPV_ARGS(&g_exposureUpload));
    if (SUCCEEDED(hr)) hr = g_exposureUpload->Map(0, nullptr, reinterpret_cast<void**>(&g_exposureMapped));
    if (FAILED(hr)) return failHr("exposure upload buffer", hr);

    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE wt = CreateThread(nullptr, 0, windowThread, ready, 0, nullptr);
    WaitForSingleObject(ready, 5000);
    CloseHandle(ready);
    if (wt) CloseHandle(wt);
    if (!g_hwnd) return failHr("hidden window", HRESULT_FROM_WIN32(GetLastError()));
    DXGI_SWAP_CHAIN_DESC1 sd{};
    sd.Width = UINT(g_startW);
    sd.Height = UINT(g_startH);
    sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.SampleDesc.Count = 1;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.BufferCount = 2;
    sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain1* sc1 = nullptr;
    if (FAILED(hr = g_factory->CreateSwapChainForHwnd(g_queue, g_hwnd, &sd, nullptr, nullptr, &sc1)))
        return failHr("CreateSwapChainForHwnd", hr);
    g_factory->MakeWindowAssociation(g_hwnd, DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_NO_WINDOW_CHANGES);
    hr = sc1->QueryInterface(IID_PPV_ARGS(&g_swapchain));
    sc1->Release();
    if (FAILED(hr)) return failHr("IDXGISwapChain3", hr);

    // The mod binds to the device of the first presented swapchain and starts its engine.
    presentFor(4000);

    const std::wstring up = g_ffxDir + L"\\amd_fidelityfx_upscaler_dx12.dll";
    const std::wstring ld = g_ffxDir + L"\\amd_fidelityfx_loader_dx12.dll";
    g_ffxUpscaler = LoadLibraryExW(up.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    g_ffxLoader = LoadLibraryExW(ld.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_ffxUpscaler || !g_ffxLoader) return failHr("FidelityFX DLLs not loadable (ffx folder)", HRESULT_FROM_WIN32(GetLastError()));
    g_ffxCreate = reinterpret_cast<PfnFfxCreateContext>(reinterpret_cast<void*>(GetProcAddress(g_ffxLoader, "ffxCreateContext")));
    g_ffxDestroy = reinterpret_cast<PfnFfxDestroyContext>(reinterpret_cast<void*>(GetProcAddress(g_ffxLoader, "ffxDestroyContext")));
    g_ffxDispatch = reinterpret_cast<PfnFfxDispatch>(reinterpret_cast<void*>(GetProcAddress(g_ffxLoader, "ffxDispatch")));
    g_ffxQuery = reinterpret_cast<PfnFfxQuery>(reinterpret_cast<void*>(GetProcAddress(g_ffxLoader, "ffxQuery")));
    if (!g_ffxCreate || !g_ffxDestroy || !g_ffxDispatch || !g_ffxQuery) return failHr("FidelityFX exports missing", E_NOINTERFACE);
    // Once bound, the mod hooks the FFX exports shortly after they are loaded.
    presentFor(3000);
    pickVersion();
    dnaLog("device side ready (FSR provider request: %s)", g_versionName.c_str());
    return true;
}

DWORD WINAPI startupThread(void*)
{
    if (startDevice()) g_state = kReady;
    return 0;
}

// ---------------------------------------------------------------- per size (render thread)
bool makeShared(SharedTex& st, UINT w, UINT h, DXGI_FORMAT fmt, bool uav)
{
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = fmt;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0);
    td.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    HRESULT hr = g_dev11->CreateTexture2D(&td, nullptr, &st.t11);
    if (FAILED(hr)) return failHr("CreateTexture2D (shared)", hr);
    IDXGIResource1* r1 = nullptr;
    HANDLE handle = nullptr;
    hr = st.t11->QueryInterface(IID_PPV_ARGS(&r1));
    if (SUCCEEDED(hr)) hr = r1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle);
    release(r1);
    if (FAILED(hr)) return failHr("CreateSharedHandle (texture)", hr);
    hr = g_dev12->OpenSharedHandle(handle, IID_PPV_ARGS(&st.t12));
    CloseHandle(handle);
    if (FAILED(hr)) return failHr("OpenSharedHandle (texture)", hr);
    return true;
}

void destroySized()
{
    if (g_ffx && g_ffxDestroy) g_ffxDestroy(&g_ffx, nullptr);
    g_ffx = nullptr;
    for (SharedTex* t : {&g_color, &g_depth, &g_motion, &g_out})
    {
        release(t->t12);
        release(t->t11);
    }
    g_w = g_h = g_dw = g_dh = 0;
}

bool createSized(const DnaFrameData& f)
{
    waitIdle();
    destroySized();
    const UINT w = UINT(f.width), h = UINT(f.height), dw = UINT(f.displayWidth), dh = UINT(f.displayHeight);
    if (!makeShared(g_color, w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, true) ||
        !makeShared(g_depth, w, h, DXGI_FORMAT_R32_FLOAT, false) ||
        !makeShared(g_motion, w, h, DXGI_FORMAT_R16G16_FLOAT, false) ||
        !makeShared(g_out, dw, dh, DXGI_FORMAT_R16G16B16A16_FLOAT, true))
        return false;

    g_beDesc = {};
    g_beDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    g_beDesc.device = g_dev12;
    g_ovDesc = {};
    g_ovDesc.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
    g_ovDesc.versionId = g_versionId;
    if (g_versionId) g_beDesc.header.pNext = &g_ovDesc.header;
    g_ctxDesc = {};
    g_ctxDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    g_ctxDesc.header.pNext = &g_beDesc.header;
    g_ctxDesc.flags = FFX_UPSCALE_ENABLE_DEPTH_INVERTED | (f.hdr ? FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE : 0);
    g_ctxDesc.maxRenderSize = {w, h};
    g_ctxDesc.maxUpscaleSize = {dw, dh};
    g_ctxDesc.fpMessage = &ffxMessage;
    const ffxReturnCode_t rc = g_ffxCreate(&g_ffx, &g_ctxDesc.header, nullptr);
    if (rc != FFX_API_RETURN_OK)
    {
        g_ffx = nullptr;
        char buf[96];
        snprintf(buf, sizeof buf, "ffxCreateContext rc=%u", rc);
        setFailed(buf);
        return false;
    }
    ffxQueryGetProviderVersion pv{};
    pv.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    g_ffxQuery(&g_ffx, &pv.header);
    g_provider = pv.versionName ? pv.versionName : "?";
    g_w = f.width;
    g_h = f.height;
    g_dw = f.displayWidth;
    g_dh = f.displayHeight;
    g_hdr = f.hdr != 0;
    dnaLog("FSR %s context: render %dx%d -> display %dx%d (%s input)", g_provider.c_str(), g_w, g_h, g_dw, g_dh,
           g_hdr ? "HDR" : "SDR");
    return true;
}

// ---------------------------------------------------------------- per frame (render thread)
// Whenever FSR does not run, the output keeps the plain bilinear upscale the managed side drew into it.

D3D12_RESOURCE_BARRIER transition(ID3D12Resource* r, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER b{};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = r;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = before;
    b.Transition.StateAfter = after;
    return b;
}

void runFrame(const DnaFrameData& f)
{
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    motion::draw(g_dev11, g_ctx, f);
    g_ctx->CopyResource(g_color.t11, static_cast<ID3D11Resource*>(f.color));
    g_ctx->CopyResource(g_depth.t11, static_cast<ID3D11Resource*>(f.depth));
    g_ctx->CopyResource(g_motion.t11, static_cast<ID3D11Resource*>(f.motion));
    const UINT64 inputsReady = ++g_fenceValue;
    g_ctx4->Signal(g_fence11, inputsReady);
    g_ctx->Flush();

    const int slot = int(g_frames % kSlots);
    cpuWait(g_allocFence[slot]);
    g_alloc[slot]->Reset();
    g_list->Reset(g_alloc[slot], nullptr);
    const D3D12_RESOURCE_STATES srv = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;

    const float exposure = f.exposure > 0.0f ? f.exposure : 1.0f;
    memcpy(g_exposureMapped + size_t(slot) * kExposureStride, &exposure, sizeof exposure);
    D3D12_TEXTURE_COPY_LOCATION dst{}, src{};
    dst.pResource = g_exposure;
    dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    src.pResource = g_exposureUpload;
    src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    src.PlacedFootprint.Offset = UINT64(slot) * kExposureStride;
    src.PlacedFootprint.Footprint = {DXGI_FORMAT_R32_FLOAT, 1, 1, 1, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT};
    D3D12_RESOURCE_BARRIER toCopy = transition(g_exposure, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
    g_list->ResourceBarrier(1, &toCopy);
    g_list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);

    D3D12_RESOURCE_BARRIER in[5] = {transition(g_color.t12, D3D12_RESOURCE_STATE_COMMON, srv),
                                    transition(g_depth.t12, D3D12_RESOURCE_STATE_COMMON, srv),
                                    transition(g_motion.t12, D3D12_RESOURCE_STATE_COMMON, srv),
                                    transition(g_out.t12, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
                                    transition(g_exposure, D3D12_RESOURCE_STATE_COPY_DEST, srv)};
    g_list->ResourceBarrier(5, in);

    ffxDispatchDescUpscale d{};
    d.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    d.commandList = g_list;
    d.color = ffxApiGetResourceDX12(g_color.t12, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.depth = ffxApiGetResourceDX12(g_depth.t12, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.motionVectors = ffxApiGetResourceDX12(g_motion.t12, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.exposure = ffxApiGetResourceDX12(g_exposure, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.reactive = ffxApiGetResourceDX12(nullptr);
    d.transparencyAndComposition = ffxApiGetResourceDX12(nullptr);
    d.output = ffxApiGetResourceDX12(g_out.t12, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    d.jitterOffset = {f.jitterX, f.jitterY};
    d.motionVectorScale = {f.mvScaleX, f.mvScaleY};
    d.renderSize = {uint32_t(f.width), uint32_t(f.height)};
    d.upscaleSize = {uint32_t(f.displayWidth), uint32_t(f.displayHeight)};
    d.enableSharpening = false;
    d.sharpness = 0.0f;
    d.frameTimeDelta = f.frameTimeMs;
    d.preExposure = 1.0f;
    d.reset = f.reset != 0 || g_frames == 0;
    d.cameraNear = f.cameraNear;
    d.cameraFar = f.cameraFar;
    d.cameraFovAngleVertical = f.fovY;
    d.viewSpaceToMetersFactor = 1.0f;
    const ffxReturnCode_t rc = g_ffxDispatch(&g_ffx, &d.header);
    if (rc != FFX_API_RETURN_OK && g_errors++ < 10) dnaLog("ffxDispatch rc=%u", rc);
    g_lastRc = rc;

    D3D12_RESOURCE_BARRIER out[5] = {transition(g_color.t12, srv, D3D12_RESOURCE_STATE_COMMON),
                                     transition(g_depth.t12, srv, D3D12_RESOURCE_STATE_COMMON),
                                     transition(g_motion.t12, srv, D3D12_RESOURCE_STATE_COMMON),
                                     transition(g_out.t12, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON),
                                     transition(g_exposure, srv, D3D12_RESOURCE_STATE_COMMON)};
    g_list->ResourceBarrier(5, out);
    g_list->Close();
    g_queue->Wait(g_fence12, inputsReady);
    ID3D12CommandList* lists[] = {g_list};
    g_queue->ExecuteCommandLists(1, lists);
    g_swapchain->Present(0, 0);
    const UINT64 outputReady = ++g_fenceValue;
    g_queue->Signal(g_fence12, outputReady);
    g_allocFence[slot] = outputReady;

    g_ctx4->Wait(g_fence11, outputReady);
    if (rc == FFX_API_RETURN_OK) g_ctx->CopyResource(static_cast<ID3D11Resource*>(f.output), g_out.t11);
    if (g_frames == 0) dnaLog("first frame through FSR: rc=%u", rc);
    if (g_dumpFrames.load() > 0)
    {
        std::wstring dir;
        {
            std::lock_guard<std::mutex> lk(g_dumpMx);
            dir = g_dumpDir;
        }
        CreateDirectoryW(dir.c_str(), nullptr);
        dump::write(g_dev11, g_ctx, dir + L"\\frame" + std::to_wstring(g_dumpIndex++), g_color.t11, g_depth.t11,
                    g_motion.t11, g_out.t11);
        --g_dumpFrames;
    }
    ++g_frames;
    QueryPerformanceCounter(&t1);
    const double ms = double(t1.QuadPart - t0.QuadPart) * 1000.0 / double(g_qpf.QuadPart);
    g_cpuMs = g_frames == 1 ? ms : g_cpuMs * 0.95 + ms * 0.05;
}

bool beginStart(ID3D11Device* dev, const DnaFrameData& f)
{
    QueryPerformanceFrequency(&g_qpf);
    g_dev11 = dev;
    dev->GetImmediateContext(&g_ctx);
    HRESULT hr = dev->QueryInterface(IID_PPV_ARGS(&g_dev11v5));
    if (SUCCEEDED(hr)) hr = g_ctx->QueryInterface(IID_PPV_ARGS(&g_ctx4));
    if (FAILED(hr)) return failHr("Direct3D 11.4 (shared fences) unavailable", hr);
    if (FAILED(hr = g_dev11v5->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g_fence11))))
        return failHr("ID3D11Device5::CreateFence", hr);
    if (FAILED(hr = g_fence11->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &g_fenceHandle)))
        return failHr("CreateSharedHandle (fence)", hr);
    IDXGIDevice* dxgiDev = nullptr;
    IDXGIAdapter* adapter = nullptr;
    hr = dev->QueryInterface(IID_PPV_ARGS(&dxgiDev));
    if (SUCCEEDED(hr)) hr = dxgiDev->GetAdapter(&adapter);
    if (FAILED(hr))
    {
        release(dxgiDev);
        return failHr("Unity's DXGI adapter", hr);
    }
    DXGI_ADAPTER_DESC ad{};
    adapter->GetDesc(&ad);
    g_luid = ad.AdapterLuid;
    release(adapter);
    release(dxgiDev);
    g_startW = f.width;
    g_startH = f.height;
    dnaLog("starting: Unity D3D11 device %p on %ls, camera %dx%d", (void*)dev, ad.Description, f.width, f.height);
    g_state = kStarting;
    HANDLE t = CreateThread(nullptr, 0, startupThread, nullptr, 0, nullptr);
    if (!t) return failHr("startup thread", HRESULT_FROM_WIN32(GetLastError()));
    CloseHandle(t);
    return true;
}
}  // namespace

void configure(const std::wstring& ffxDir, const std::string& fsrVersionPrefix)
{
    g_ffxDir = ffxDir;
    std::lock_guard<std::mutex> lk(g_versionMx);
    g_fsrPrefix = fsrVersionPrefix;
}

void setFsrVersion(const std::string& fsrVersionPrefix)
{
    {
        std::lock_guard<std::mutex> lk(g_versionMx);
        g_fsrPrefix = fsrVersionPrefix;
    }
    ++g_versionRequest;
}

void onFrame(ID3D11Device* unityDevice, const DnaFrameData& f)
{
    if (!unityDevice || !f.color || !f.depth || !f.motion || !f.output || f.width <= 0 || f.height <= 0 ||
        f.displayWidth <= 0 || f.displayHeight <= 0)
        return;
    int st = g_state.load();
    if (st == kIdle)
    {
        if (!beginStart(unityDevice, f)) return;
        st = kStarting;
    }
    if (st == kStarting || st == kFailed) return;
    const HRESULT removed = g_dev12->GetDeviceRemovedReason();
    if (FAILED(removed))
    {
        failHr("the private D3D12 device was removed", removed);  // never queue a D3D11 wait it cannot satisfy
        return;
    }
    // A different FSR provider was asked for: the provider is fixed per context, so the context is rebuilt.
    bool versionChanged = false;
    const int request = g_versionRequest.load();
    if (request != g_versionApplied)
    {
        g_versionApplied = request;
        const uint64_t before = g_versionId;
        selectVersion();
        versionChanged = g_versionId != before;
        if (versionChanged) dnaLog("FSR provider request: %s", g_versionName.c_str());
    }
    if (st == kReady || versionChanged || f.width != g_w || f.height != g_h || f.displayWidth != g_dw ||
        f.displayHeight != g_dh || (f.hdr != 0) != g_hdr)
    {
        if (!createSized(f)) return;
        g_state = kRunning;
    }
    runFrame(f);
}

void shutdown()
{
    if (g_state.load() == kStarting) return;  // the startup thread still owns the device side
    waitIdle();
    destroySized();
    release(g_swapchain);
    if (g_exposureUpload && g_exposureMapped) g_exposureUpload->Unmap(0, nullptr);
    g_exposureMapped = nullptr;
    release(g_exposureUpload);
    release(g_exposure);
    release(g_list);
    for (auto& a : g_alloc) release(a);
    release(g_fence12);
    release(g_queue);
    release(g_dev12);
    release(g_factory);
    release(g_fence11);
    motion::shutdown();
    release(g_ctx4);
    release(g_ctx);
    release(g_dev11v5);
    if (g_windowThreadId) PostThreadMessageW(g_windowThreadId, WM_QUIT, 0, 0);
    g_state = kIdle;
}

void requestDump(const std::wstring& dir)
{
    {
        std::lock_guard<std::mutex> lk(g_dumpMx);
        g_dumpDir = dir;
    }
    g_dumpIndex = 0;
    g_dumpFrames = 2;  // two consecutive frames: enough to check motion vectors by reprojection
}

int state() { return g_state.load(); }

std::string status()
{
    static const char* names[] = {"idle", "starting", "ready", "running"};
    const int st = g_state.load();
    char buf[512];
    if (st == kFailed)
    {
        std::lock_guard<std::mutex> lk(g_failMx);
        snprintf(buf, sizeof buf, "failed: %s", g_failure.c_str());
    }
    else
        snprintf(buf, sizeof buf, "%s | FSR %s %dx%d -> %dx%d | frames %llu | dispatch errors %llu (last rc %u) | render-thread %.2f ms/frame | %s",
                 st >= 0 && st <= 3 ? names[st] : "?", g_provider.c_str(), g_w, g_h, g_dw, g_dh, (unsigned long long)g_frames,
                 (unsigned long long)g_errors, g_lastRc, g_cpuMs, motion::status().c_str());
    return buf;
}
}  // namespace bridge
