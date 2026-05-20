using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private void ScanHeightmaps() {
        List<int> dead = null;
        foreach (var kv in _chunks) {
            var d = kv.Value;
            if (d == null || d.Heightmap == null || d.MeshObject == null) {
                (dead ??= new List<int>()).Add(kv.Key);
            }
        }
        if (dead != null) {
            foreach (var k in dead) {
                var d = _chunks[k];
                if (d?.MeshObject != null) Destroy(d.MeshObject);
                if (d?.Mesh       != null) Destroy(d.Mesh);
                _chunks.Remove(k);
            }
        }

        foreach (var hm in FindObjectsByType<Heightmap>(FindObjectsSortMode.None)) {
            int id = hm.GetInstanceID();
            if (_chunks.ContainsKey(id)) continue;

            if (hm.gameObject.name.Contains("lodMesh")) continue;

            EnsureHeightmapReflection();
            DumpMaterialOnce(hm);

            var data = new ChunkRenderData {
                Chunk      = new VoxelChunk(),
                Heightmap  = hm,
                MeshObject = new GameObject("TerraformVoxels"),
                Mesh       = new Mesh { name = "TerraformVoxels" }
            };

            data.MeshObject.transform.SetParent(hm.transform, false);
            int voxelLayer = LayerMask.NameToLayer("piece");
            if (voxelLayer < 0) voxelLayer = 0;
            data.MeshObject.layer = voxelLayer;
            data.Filter            = data.MeshObject.AddComponent<MeshFilter>();
            data.Filter.mesh       = data.Mesh;
            data.Renderer = data.MeshObject.AddComponent<MeshRenderer>();
            var placeholderShader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
            data.Renderer.sharedMaterial = placeholderShader != null
                ? new Material(placeholderShader) { color = Color.magenta }
                : new Material(Shader.Find("Hidden/InternalErrorShader"));
            data.Renderer.enabled = false;
            data.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            data.Renderer.receiveShadows    = false;
            data.Renderer.lightProbeUsage      = UnityEngine.Rendering.LightProbeUsage.Off;
            data.Renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            data.Collider = data.MeshObject.AddComponent<MeshCollider>();
            data.MeshObject.AddComponent<VoxelMeshDestructible>();

            int pieceLayer = LayerMask.NameToLayer("piece");
            if (pieceLayer >= 0) {
                var fillGo = new GameObject("TerraformFillLight");
                fillGo.transform.SetParent(data.MeshObject.transform, false);
                fillGo.transform.localPosition = new Vector3(VoxelChunk.SIZE * 0.5f, VoxelChunk.SIZE * 0.5f, VoxelChunk.SIZE * 0.5f);
                var fill = fillGo.AddComponent<Light>();
                fill.type           = LightType.Point;
                fill.range          = VoxelChunk.SIZE;
                fill.intensity      = 0.6f;
                fill.color          = new Color(0.55f, 0.6f, 0.7f);
                fill.shadows        = LightShadows.None;
                fill.cullingMask    = 1 << pieceLayer;
                fill.renderMode     = LightRenderMode.ForcePixel;
            }

            if (!Heightmap.GetHeight(hm.transform.position, out data.TerrainHeight))
                data.TerrainHeight = hm.transform.position.y;
            data.TerrainHeight = Mathf.Round(data.TerrainHeight);

            int half = VoxelChunk.SIZE / 2;
            data.MeshObject.transform.localPosition = new Vector3(
                -half,
                data.TerrainHeight - hm.transform.position.y - half,
                -half);

            _chunks[id] = data;

            DebugLog($"ChunkRenderData -> {hm.gameObject.name} terrainH={data.TerrainHeight:F1}");

            RebuildChunkFromHistory(data);
        }

        ScanWaterVolumes();
        RefreshWaterLeaks();
    }

    private static void ResetChunkDigState(ChunkRenderData data) {
        data.Chunk = new VoxelChunk();
        data.RenderFootprints.Clear();
    }

    private void RebuildChunkFromHistory(ChunkRenderData data) {
        ResetChunkDigState(data);

        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);
        long chunkKey = ChunkKey(chunkOriginX, chunkOriginZ);
        if (!_chunkDigHistory.TryGetValue(chunkKey, out var digs) || digs.Count == 0) {
            data.Mesh.Clear();
            data.Renderer.enabled = false;
            if (data.Collider != null) data.Collider.sharedMesh = null;
            return;
        }

        int replayed = 0;
        foreach (var dig in digs) {
            CarveCrater(data, dig.WorldPos, dig.Radius, dig.Mode, dig.Normal.sqrMagnitude > 0.001f ? dig.Normal : Vector3.up);
            replayed++;
        }
        if (replayed > 0) {
            ReplayDigPunchesOnHeightmap(data.Heightmap, digs);
            RebuildMesh(data);
            DebugLog($"  replayed {replayed} digs onto new chunk");
        } else {
            data.Mesh.Clear();
            data.Renderer.enabled = false;
            if (data.Collider != null) data.Collider.sharedMesh = null;
        }
    }

    private static bool ChunkOverlapsDig(int chunkOriginX, int chunkOriginZ, Vector3 worldPos, float radius) {
        float footprint = radius * OuterRadiusMultiplier;
        int worldCellMinX = Mathf.FloorToInt(worldPos.x - footprint);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + footprint);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - footprint);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + footprint);

        if (worldCellMaxX + 1 < chunkOriginX || worldCellMinX > chunkOriginX + VoxelChunk.SIZE) return false;
        if (worldCellMaxZ + 1 < chunkOriginZ || worldCellMinZ > chunkOriginZ + VoxelChunk.SIZE) return false;
        return true;
    }
}
