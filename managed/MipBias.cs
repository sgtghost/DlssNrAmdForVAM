// Sharpens texture sampling while the scene renders below display size (FSR's recommended mip bias,
// log2(render / display) - 1). Only textures with mipmaps and no bias of their own are touched, and
// only those are put back.
using System.Collections.Generic;
using UnityEngine;

public class MipBias
{
    readonly List<Texture2D> _textures = new List<Texture2D>();
    readonly HashSet<int> _ids = new HashSet<int>();
    float _bias;

    public int Count { get { return _textures.Count; } }
    public float Bias { get { return _bias; } }

    // Applies the bias to every loaded texture not yet handled; call again to pick up new textures.
    public void Apply(float bias)
    {
        if (bias == 0f)
        {
            Restore();
            return;
        }
        if (bias != _bias)
            foreach (Texture2D t in _textures)
                if (t != null) t.mipMapBias = bias;
        _bias = bias;
        foreach (Texture2D t in Resources.FindObjectsOfTypeAll<Texture2D>())
        {
            if (t.mipmapCount <= 1 || t.mipMapBias != 0f) continue;
            if (!_ids.Add(t.GetInstanceID())) continue;
            t.mipMapBias = bias;
            _textures.Add(t);
        }
        _textures.RemoveAll(t => t == null);
    }

    public void Restore()
    {
        foreach (Texture2D t in _textures)
            if (t != null && t.mipMapBias == _bias) t.mipMapBias = 0f;
        _textures.Clear();
        _ids.Clear();
        _bias = 0f;
    }
}
