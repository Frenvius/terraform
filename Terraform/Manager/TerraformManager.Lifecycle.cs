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
                if (d != null) DestroyUndergroundMasks(d);
                _chunks.Remove(k);
                HolePunchSystem.Clear(k);
                _pendingBuildHash.Remove(k);
                _pendingBuildSince.Remove(k);
            }
        }

        foreach (var hm in Heightmap.GetAllHeightmaps()) {
            int id = hm.GetInstanceID();
            if (_chunks.ContainsKey(id)) continue;

            if (IsLodHeightmap(hm)) {
                if (_lodSeen.Add(id)) ApplyAllCratersToLod(hm);
                continue;
            }

            EnsureHeightmapReflection();
            DumpMaterialOnce(hm);

            var data = new ChunkRenderData {
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
            SetChunkVisible(data, false);
            data.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            data.Renderer.receiveShadows    = true;
            data.Renderer.lightProbeUsage      = UnityEngine.Rendering.LightProbeUsage.Off;
            data.Renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            data.Collider = data.MeshObject.AddComponent<MeshCollider>();
            data.MeshObject.AddComponent<VoxelMeshDestructible>();
            data.MeshObject.AddComponent<VoxelSurface>().Owner = hm;

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
                fill.enabled        = data.Renderer.enabled;
                data.FillLight      = fill;
            }

            int halfA = VoxelChunk.SIZE / 2;
            long lookupKey = ChunkKey(
                Mathf.RoundToInt(hm.transform.position.x - halfA),
                Mathf.RoundToInt(hm.transform.position.z - halfA));
            data.ChunkKey = lookupKey;
            bool fromZone = TryGetZoneTerrainHeight(lookupKey, out float terrainHeight);
            if (!fromZone) {
                if (!Heightmap.GetHeight(hm.transform.position, out terrainHeight))
                    terrainHeight = hm.transform.position.y;
                terrainHeight = Mathf.Round(terrainHeight);
            }
            PlaceChunkWindow(data, terrainHeight);

            _chunks[id] = data;

            DebugLog($"ChunkRenderData -> {hm.gameObject.name} terrainH={data.TerrainHeight:F1} fromZone={fromZone}");

            _pendingBuildHash[id]  = ComputeHeightsHash(hm);
            _pendingBuildSince[id] = Time.time;
        }

        BuildStabilizedChunks();

        ScanWaterVolumes();
        RefreshWaterLeaks();
    }

    private const float BuildStabilizeTimeout = 8f;

    private void BuildStabilizedChunks() {
        if (_pendingBuildHash.Count == 0) return;

        List<int> ready = null;
        foreach (var kv in _pendingBuildHash) {
            int id = kv.Key;
            if (!_chunks.TryGetValue(id, out var data) || data?.Heightmap == null) {
                (ready ??= new List<int>()).Add(id);
                continue;
            }
            long current = ComputeHeightsHash(data.Heightmap);
            float since = _pendingBuildSince.TryGetValue(id, out var t) ? t : Time.time;
            bool timedOut = Time.time - since > BuildStabilizeTimeout;
            if (current == kv.Value || timedOut) {
                (ready ??= new List<int>()).Add(id);
            } else {
                _pendingBuildHash[id] = current;
            }
        }
        if (ready == null) return;

        var built = new List<ChunkRenderData>();
        foreach (var id in ready) {
            _pendingBuildHash.Remove(id);
            _pendingBuildSince.Remove(id);
            if (_chunks.TryGetValue(id, out var data) && data?.Heightmap != null)
                built.Add(data);
        }
        if (built.Count == 0) return;

        foreach (var data in built) BuildChunk(data);
    }

    private static void PlaceChunkWindow(ChunkRenderData data, float terrainHeight) {
        const int half = VoxelChunk.SIZE / 2;
        data.TerrainHeight = terrainHeight;
        data.MeshObject.transform.localPosition = new Vector3(
            -half,
            terrainHeight - data.Heightmap.transform.position.y - half,
            -half);
    }

    private void ResetChunkDigState(ChunkRenderData data) {
        data.Chunk = null;
        EnsureChunkAllocated(data);
    }

    private void ClearChunkMesh(ChunkRenderData data) {
        data.Mesh.Clear();
        SetChunkVisible(data, false);
        if (data.Collider != null) data.Collider.sharedMesh = null;
    }

    private void RefreshChunkAfterStateChange(ChunkRenderData data) {
        RefreshCellOwnership(data, CellRect.Whole);
        RebuildMesh(data);
        if (_waters.TryGetValue(data.Heightmap.GetInstanceID(), out var water)) RebuildWaterMeshFromHoles(water);
        Vector3 origin = data.MeshObject.transform.position;
        int originX = Mathf.RoundToInt(origin.x);
        int originZ = Mathf.RoundToInt(origin.z);
        MarkNavDirty(originX, originZ, originX + VoxelChunk.SIZE, originZ + VoxelChunk.SIZE);
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
