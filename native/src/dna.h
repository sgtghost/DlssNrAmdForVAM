// Shared declarations for the native half of "DLSS-NR on AMD for VaM".
#pragma once
#include <stdint.h>

// One submesh of a GPU-skinned VaM mesh (skin, clothing) or one hair item to draw into the motion vectors.
// Must match the C# MeshDraw struct field for field.
struct DnaMeshDraw
{
    void* verts;    // ID3D11Buffer: VaM's positions for this frame (structured; position = first float3 of each element)
    void* indices;  // ID3D11Buffer: the mesh's index buffer
    uint32_t indexStart;
    uint32_t indexCount;
    uint32_t baseVertex;
    uint32_t index32;  // 1 = 32-bit indices
    int32_t key;       // identifies the skinned object (its history of positions)
    float offsetX;     // VaM's drawOffset: world translation applied when drawing
    float offsetY;
    float offsetZ;
    uint32_t stride;   // element size of verts: 12 (skin, clothing: float3) or 116 (hair physics particles)
    float tolerance;   // metres the redrawn surface may lie off the scene depth and still count as visible
};

// Written by the managed side into a ring slot each frame and handed to the render event.
// Must match the C# FrameData struct field for field.
struct DnaFrameData
{
    void* color;   // ID3D11Texture2D (ARGBHalf, render size): the camera's finished frame
    void* depth;   // ID3D11Texture2D (RFloat, render size): camera depth, reversed Z
    void* motion;  // ID3D11Texture2D (RGHalf, render size): camera motion vectors (UV units, current - previous)
    void* output;  // ID3D11Texture2D (ARGBHalf, display size): receives the FSR (+ NR) result
    int32_t width;   // render size
    int32_t height;
    int32_t displayWidth;  // output size
    int32_t displayHeight;
    float jitterX;  // pixels
    float jitterY;
    float mvScaleX;
    float mvScaleY;
    float frameTimeMs;
    float cameraNear;
    float cameraFar;
    float fovY;  // radians
    int32_t reset;
    int32_t hdr;
    float exposure;  // handed to FSR (and DLSS-NR) as the exposure texture value
    // Object motion of VaM's GPU-skinned meshes, drawn over the camera motion vectors. Matrices are
    // Unity Matrix4x4 memory order (column-major).
    int32_t meshDrawCount;
    DnaMeshDraw* meshDraws;
    float rasterViewProj[16];  // jittered projection (GPU convention, top-down target) * view: where the scene was drawn
    float viewProj[16];        // non-jittered projection (GL convention) * view
    float prevViewProj[16];    // the same, previous frame
};

enum DnaEvent
{
    kDnaEventFrame = 1,  // data: DnaFrameData*
};

void dnaLog(const char* fmt, ...);
