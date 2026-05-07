using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool _materialDumped;

    private static void DumpMaterialOnce(Heightmap hm) {
        if (_materialDumped) return;
        var mat = hm.m_material;
        if (mat == null || mat.shader == null) return;
        _materialDumped = true;

        DebugLog($"[mat] shader={mat.shader.name} renderQueue={mat.renderQueue}");
        int count = mat.shader.GetPropertyCount();
        for (int i = 0; i < count; i++) {
            var pname = mat.shader.GetPropertyName(i);
            var ptype = mat.shader.GetPropertyType(i);
            string value = "?";
            try {
                switch (ptype) {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = mat.GetFloat(pname).ToString("F2");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        value = mat.GetInt(pname).ToString();
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        value = mat.GetColor(pname).ToString();
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        value = mat.GetVector(pname).ToString();
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var tex = mat.GetTexture(pname);
                        value = tex != null ? $"{tex.name} ({tex.GetType().Name})" : "null";
                        break;
                }
            } catch { }
            DebugLog($"[mat]   {ptype} {pname} = {value}");
        }
        foreach (var kw in mat.shaderKeywords)
            DebugLog($"[mat]   keyword {kw}");
    }

    private static void TryApplyTerrainMaterial(ChunkRenderData data) {
        if (data.Renderer == null) return;

        if (Instance != null && Instance._debugMagenta) {
            var debug = Instance.GetOrCreateDebugMaterial();
            if (debug != null) data.Renderer.sharedMaterial = debug;
            return;
        }

        if (data.Heightmap == null) return;

        EnsureHeightmapReflection();
        var liveMat = _materialInstanceField?.GetValue(data.Heightmap) as Material;
        if (liveMat == null) return;

        data.Renderer.sharedMaterial = liveMat;
    }
}
