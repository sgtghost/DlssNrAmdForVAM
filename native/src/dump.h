// Debug dump of the shared FSR inputs/output (see dump.cpp).
#pragma once
#include <windows.h>
#include <d3d11.h>

#include <string>

namespace dump
{
// Render thread only (uses Unity's immediate context and stalls on Map).
void write(ID3D11Device* dev, ID3D11DeviceContext* ctx, const std::wstring& dir, ID3D11Texture2D* color,
           ID3D11Texture2D* depth, ID3D11Texture2D* motion, ID3D11Texture2D* output);
}
