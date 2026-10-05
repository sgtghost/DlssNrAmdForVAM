// Debug dump: writes the shared FSR inputs and output as BMP files (top row of the file = row 0 of
// the texture), to check orientation and the content of colour, depth and motion vectors.
#include "dump.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

#include "dna.h"

namespace
{
float halfToFloat(uint16_t h)
{
    uint32_t s = uint32_t(h & 0x8000u) << 16, e = (h >> 10) & 0x1Fu, m = h & 0x3FFu, f;
    if (e == 0)
    {
        if (m == 0)
            f = s;
        else
        {
            int ee = 1;
            while (!(m & 0x400u))
            {
                m <<= 1;
                --ee;
            }
            m &= 0x3FFu;
            f = s | (uint32_t(ee + 112) << 23) | (m << 13);
        }
    }
    else if (e == 31)
        f = s | 0x7F800000u | (m << 13);
    else
        f = s | ((e + 112) << 23) | (m << 13);
    float r;
    memcpy(&r, &f, 4);
    return r;
}

uint8_t toByte(float v)
{
    if (!(v > 0.0f)) return 0;
    if (v >= 1.0f) return 255;
    return uint8_t(v * 255.0f + 0.5f);
}

bool writeBmp(const std::wstring& path, int w, int h, const std::vector<uint8_t>& bgr)
{
    const int row = (w * 3 + 3) & ~3;
    BITMAPFILEHEADER fh{};
    BITMAPINFOHEADER ih{};
    ih.biSize = sizeof ih;
    ih.biWidth = w;
    ih.biHeight = -h;  // top-down
    ih.biPlanes = 1;
    ih.biBitCount = 24;
    ih.biCompression = BI_RGB;
    ih.biSizeImage = DWORD(row * h);
    fh.bfType = 0x4D42;
    fh.bfOffBits = sizeof fh + sizeof ih;
    fh.bfSize = fh.bfOffBits + ih.biSizeImage;
    FILE* f = _wfopen(path.c_str(), L"wb");
    if (!f) return false;
    fwrite(&fh, sizeof fh, 1, f);
    fwrite(&ih, sizeof ih, 1, f);
    const uint8_t pad[3] = {};
    for (int y = 0; y < h; ++y)
    {
        fwrite(&bgr[size_t(y) * w * 3], 1, size_t(w) * 3, f);
        fwrite(pad, 1, size_t(row - w * 3), f);
    }
    fclose(f);
    return true;
}

enum Kind { kColour, kDepth, kMotion };

void dumpOne(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* src, Kind kind, const std::wstring& path)
{
    D3D11_TEXTURE2D_DESC d{};
    src->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING;
    d.BindFlags = 0;
    d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    d.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    if (FAILED(dev->CreateTexture2D(&d, nullptr, &staging))) return;
    ctx->CopyResource(staging, src);
    D3D11_MAPPED_SUBRESOURCE m{};
    if (FAILED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
    {
        staging->Release();
        return;
    }
    const int w = int(d.Width), h = int(d.Height);
    std::vector<uint8_t> bgr(size_t(w) * h * 3);
    std::vector<float> raw(kind == kMotion ? size_t(w) * h * 2 : 0);
    double sum[3] = {}, maxMv = 0, minD = 1e9, maxD = -1e9;
    for (int y = 0; y < h; ++y)
    {
        const uint8_t* row = static_cast<const uint8_t*>(m.pData) + size_t(y) * m.RowPitch;
        for (int x = 0; x < w; ++x)
        {
            uint8_t* o = &bgr[(size_t(y) * w + x) * 3];
            if (kind == kColour)
            {
                const uint16_t* p = reinterpret_cast<const uint16_t*>(row) + x * 4;
                for (int c = 0; c < 3; ++c)
                {
                    const float v = halfToFloat(p[c]);
                    sum[c] += v;
                    o[2 - c] = toByte(std::pow(v / (1.0f + v), 1.0f / 2.2f));
                }
            }
            else if (kind == kDepth)
            {
                const float v = reinterpret_cast<const float*>(row)[x];
                if (v < minD) minD = v;
                if (v > maxD) maxD = v;
                o[0] = o[1] = o[2] = toByte(std::pow(v > 0 ? v : 0.0f, 0.25f));
            }
            else
            {
                const uint16_t* p = reinterpret_cast<const uint16_t*>(row) + x * 2;
                const float mx = halfToFloat(p[0]), my = halfToFloat(p[1]);
                raw[(size_t(y) * w + x) * 2] = mx;
                raw[(size_t(y) * w + x) * 2 + 1] = my;
                const double mag = std::sqrt(double(mx) * mx + double(my) * my);
                if (mag > maxMv) maxMv = mag;
                o[2] = toByte(0.5f + mx * 50.0f);
                o[1] = toByte(0.5f + my * 50.0f);
                o[0] = 128;
            }
        }
    }
    ctx->Unmap(staging, 0);
    staging->Release();
    writeBmp(path, w, h, bgr);
    if (kind == kMotion)
    {
        // float32 (x, y) per pixel, rows top to bottom
        const std::wstring rawPath = path.substr(0, path.size() - 4) + L".raw";
        if (FILE* f = _wfopen(rawPath.c_str(), L"wb"))
        {
            fwrite(raw.data(), sizeof(float), raw.size(), f);
            fclose(f);
        }
    }
    const double n = double(w) * h;
    if (kind == kColour)
        dnaLog("dump %ls: %dx%d, mean rgb %.3f %.3f %.3f", path.c_str(), w, h, sum[0] / n, sum[1] / n, sum[2] / n);
    else if (kind == kDepth)
        dnaLog("dump %ls: %dx%d, depth %.5f .. %.5f", path.c_str(), w, h, minD, maxD);
    else
        dnaLog("dump %ls: %dx%d, max |mv| %.5f (uv)", path.c_str(), w, h, maxMv);
}
}  // namespace

namespace dump
{
void write(ID3D11Device* dev, ID3D11DeviceContext* ctx, const std::wstring& dir, ID3D11Texture2D* color,
           ID3D11Texture2D* depth, ID3D11Texture2D* motion, ID3D11Texture2D* output)
{
    CreateDirectoryW(dir.c_str(), nullptr);
    dumpOne(dev, ctx, color, kColour, dir + L"\\input_colour.bmp");
    dumpOne(dev, ctx, depth, kDepth, dir + L"\\input_depth.bmp");
    dumpOne(dev, ctx, motion, kMotion, dir + L"\\input_motion.bmp");
    dumpOne(dev, ctx, output, kColour, dir + L"\\output.bmp");
}
}  // namespace dump
