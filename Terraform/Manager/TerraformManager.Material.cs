using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool _materialDumped;
    private static MaterialPropertyBlock _terrainRendererProperties;
    private static readonly int ClearedMaskTexId = Shader.PropertyToID("_ClearedMaskTex");

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
            if (debug != null) SetVoxelMaterial(data, debug);
            return;
        }

        if (data.Heightmap == null) return;

        EnsureHeightmapReflection();
        var liveMat = _materialInstanceField?.GetValue(data.Heightmap) as Material;
        if (liveMat == null) return;

        SetVoxelMaterial(data, liveMat);
        MirrorTerrainRendererProperties(data);
    }

    private static void SetVoxelMaterial(ChunkRenderData data, Material material) {
        var materials = new Material[data.Mesh.subMeshCount];
        for (int i = 0; i < materials.Length; i++) materials[i] = material;
        data.Renderer.sharedMaterials = materials;
    }

    private static void MirrorTerrainRendererProperties(ChunkRenderData data) {
        var terrainRenderer = data.Heightmap.GetComponent<MeshRenderer>();
        if (terrainRenderer == null) return;

        _terrainRendererProperties ??= new MaterialPropertyBlock();
        terrainRenderer.GetPropertyBlock(_terrainRendererProperties);
        data.Renderer.SetPropertyBlock(_terrainRendererProperties, SurfaceSubmesh);
        _terrainRendererProperties.SetTexture(ClearedMaskTexId, UndergroundDirtMask);
        data.Renderer.SetPropertyBlock(_terrainRendererProperties, UndergroundSubmesh);
        for (int i = 0; i < data.SubmeshPaintBands.Count; i++) {
            if (!data.PaintBandMasks.TryGetValue(data.SubmeshPaintBands[i], out var mask)) continue;
            _terrainRendererProperties.SetTexture(ClearedMaskTexId, mask);
            data.Renderer.SetPropertyBlock(_terrainRendererProperties, FirstPaintBandSubmesh + i);
        }
    }
}
