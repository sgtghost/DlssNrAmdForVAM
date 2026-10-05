// DlssNrAmdNative: native half of the VaM "DLSS-NR on AMD" plugin (Unity 2018.1, D3D11).
// Loaded by Unity from VaM_Data\Plugins on the first P/Invoke, which hands us IUnityInterfaces.
#include <windows.h>
#include <d3d11.h>

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "bridge.h"
#include "dna.h"

#define DNA_EXPORT extern "C" UNITY_INTERFACE_EXPORT

namespace
{
// Lines are kept for the managed side (drained into BepInEx's log) and appended to a file once a
// path is known, so a crash still leaves a trace.
std::mutex g_logMx;
std::string g_pending;
std::string g_drained;
std::string g_history;  // everything logged before the file was opened
FILE* g_file = nullptr;

IUnityInterfaces* g_unity = nullptr;
IUnityGraphics* g_graphics = nullptr;
IUnityGraphicsD3D11* g_d3d11 = nullptr;

std::string g_statusCopy;

void UNITY_INTERFACE_API onDeviceEvent(UnityGfxDeviceEventType type)
{
    if (type == kUnityGfxDeviceEventInitialize)
    {
        const UnityGfxRenderer r = g_graphics->GetRenderer();
        g_d3d11 = r == kUnityGfxRendererD3D11 ? static_cast<IUnityGraphicsD3D11*>(
                                                    g_unity->GetInterface(GetUnityInterfaceGUID<IUnityGraphicsD3D11>()))
                                              : nullptr;
        dnaLog("graphics device: renderer %d%s, IUnityGraphicsD3D11 %p", int(r),
               r == kUnityGfxRendererD3D11 ? " (Direct3D 11)" : " (not Direct3D 11: the plugin stays idle)",
               (void*)g_d3d11);
    }
    else if (type == kUnityGfxDeviceEventShutdown)
    {
        bridge::shutdown();
        g_d3d11 = nullptr;
    }
}

void UNITY_INTERFACE_API onRenderEvent(int eventId, void* data)
{
    if (eventId == kDnaEventFrame && data && g_d3d11)
        bridge::onFrame(g_d3d11->GetDevice(), *static_cast<const DnaFrameData*>(data));
}
}  // namespace

void dnaLog(const char* fmt, ...)
{
    char msg[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof msg, fmt, ap);
    va_end(ap);
    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[1100];
    snprintf(line, sizeof line, "%02d:%02d:%02d.%03d %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, msg);
    std::lock_guard<std::mutex> lk(g_logMx);
    g_pending += msg;
    g_pending += '\n';
    if (g_file)
    {
        fputs(line, g_file);
        fflush(g_file);
    }
    else
        g_history += line;
}

DNA_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unity)
{
    g_unity = unity;
    g_graphics = static_cast<IUnityGraphics*>(unity->GetInterface(GetUnityInterfaceGUID<IUnityGraphics>()));
    dnaLog("UnityPluginLoad (IUnityGraphics %p)", (void*)g_graphics);
    if (!g_graphics) return;
    g_graphics->RegisterDeviceEventCallback(onDeviceEvent);
    onDeviceEvent(kUnityGfxDeviceEventInitialize);  // the device already exists when we are loaded
}

DNA_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_graphics) g_graphics->UnregisterDeviceEventCallback(onDeviceEvent);
}

DNA_EXPORT void UNITY_INTERFACE_API DlssNrAmd_SetLogPath(const wchar_t* path)
{
    std::lock_guard<std::mutex> lk(g_logMx);
    if (g_file) fclose(g_file);
    g_file = _wfopen(path, L"w");
    if (g_file)
    {
        fputs(g_history.c_str(), g_file);
        fflush(g_file);
        g_history.clear();
    }
}

DNA_EXPORT void UNITY_INTERFACE_API DlssNrAmd_Configure(const wchar_t* ffxDir, const char* fsrVersionPrefix)
{
    bridge::configure(ffxDir ? ffxDir : L"", fsrVersionPrefix ? fsrVersionPrefix : "");
}

DNA_EXPORT void UNITY_INTERFACE_API DlssNrAmd_SetFsrVersion(const char* fsrVersionPrefix)
{
    bridge::setFsrVersion(fsrVersionPrefix ? fsrVersionPrefix : "");
}

DNA_EXPORT UnityRenderingEventAndData UNITY_INTERFACE_API DlssNrAmd_GetRenderEventFunc() { return onRenderEvent; }

DNA_EXPORT int UNITY_INTERFACE_API DlssNrAmd_GetState() { return bridge::state(); }

DNA_EXPORT void UNITY_INTERFACE_API DlssNrAmd_RequestDump(const wchar_t* dir) { bridge::requestDump(dir ? dir : L"."); }

// Valid until the next call; the managed side copies it immediately.
DNA_EXPORT const char* UNITY_INTERFACE_API DlssNrAmd_GetStatus()
{
    g_statusCopy = bridge::status();
    return g_statusCopy.c_str();
}

// Everything logged since the previous call. Valid until the next call.
DNA_EXPORT const char* UNITY_INTERFACE_API DlssNrAmd_DrainLog()
{
    std::lock_guard<std::mutex> lk(g_logMx);
    g_drained.swap(g_pending);
    g_pending.clear();
    return g_drained.c_str();
}
