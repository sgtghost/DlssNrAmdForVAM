// Object motion for VaM's GPU-skinned meshes (skin and clothing).
//
// VaM skins characters in compute shaders and draws them with Graphics.DrawMesh, so Unity's motion
// vectors only describe the camera's movement over them, and FSR / DLSS-NR smear anything that moves on
// its own. This pass redraws those meshes from VaM's own position buffers, against a copy of last
// frame's positions, and writes the screen motion into the camera's motion texture wherever the
// redrawn surface is the one the camera saw (its depth matches the scene depth).
#pragma once
#include <d3d11.h>

#include <string>

#include "dna.h"

namespace motion
{
// Render thread, before the motion texture is handed on. Leaves Unity's D3D11 state untouched.
void draw(ID3D11Device* dev, ID3D11DeviceContext* ctx, const DnaFrameData& f);
void shutdown();
std::string status();
}  // namespace motion
