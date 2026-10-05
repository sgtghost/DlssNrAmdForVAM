// FSR bridge: runs Unity's D3D11 camera frames through FSR on a private D3D12 device, where the
// DLSS-NR-on-AMD mod (version.dll) intercepts ffxDispatch and applies the neural rendering pass.
#pragma once
#include <d3d11.h>

#include <string>

#include "dna.h"

namespace bridge
{
enum State
{
    kIdle = 0,
    kStarting = 1,  // background thread: d3d12, hidden swapchain, mod binding, FFX load
    kReady = 2,     // device side up; per-size resources are created on the next frame
    kRunning = 3,
    kFailed = -1,
};

void configure(const std::wstring& ffxDir, const std::string& fsrVersionPrefix);  // before the first frame
void setFsrVersion(const std::string& fsrVersionPrefix);  // any time: the FSR context is rebuilt on the next frame
void onFrame(ID3D11Device* unityDevice, const DnaFrameData& frame);                 // Unity render thread
void shutdown();                                                                     // Unity render thread
void requestDump(const std::wstring& dir);  // writes the next frame's FSR inputs/output as BMP files
int state();
std::string status();
}  // namespace bridge
