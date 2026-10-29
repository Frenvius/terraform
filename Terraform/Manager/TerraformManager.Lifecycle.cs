using System.Collections.Generic;
using UnityEngine;
using Terraform.Persistence;
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
                if (d != null) {
                    DestroyUndergroundMasks(d);
                    if (!_pendingBuildHash.ContainsKey(k)) RetainUnloadedChunkSdf(d);
                }
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
            bool usedSavedTerrainHeight = false;
            if (_pendingPersistence != null && _pendingPersistence.TryGetValue(lookupKey, out var savedEntry)
                && savedEntry != null && savedEntry.TerrainHeight != 0f) {
                data.TerrainHeight = savedEntry.TerrainHeight;
                usedSavedTerrainHeight = true;
            } else {
                if (!Heightmap.GetHeight(hm.transform.position, out data.TerrainHeight))
                    data.TerrainHeight = hm.transform.position.y;
                data.TerrainHeight = Mathf.Round(data.TerrainHeight);
            }

            data.MeshObject.transform.localPosition = new Vector3(
                -halfA,
                data.TerrainHeight - hm.transform.position.y - halfA,
                -halfA);

            _chunks[id] = data;

            DebugLog($"ChunkRenderData -> {hm.gameObject.name} terrainH={data.TerrainHeight:F1} fromSave={usedSavedTerrainHeight}");

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

        foreach (var data in built) {
            RebuildChunkFromHistory(data);
            if (_waters.TryGetValue(data.Heightmap.GetInstanceID(), out var water)) RebuildWaterMeshFromHoles(water);
        }
    }

    private void RetainUnloadedChunkSdf(ChunkRenderData data) {
        if (data.Chunk == null || !data.Chunk.HasAnyAltered()) return;
        _pendingPersistence[data.ChunkKey] = new ChunkPersistence {
            TerrainHeight   = data.TerrainHeight,
            VoxelData       = data.Chunk.SerializeFull(),
            VoxelDataIsFull = true,
            AlteredBits     = data.Chunk.SerializeAltered(),
        };
    }

    private void ResetChunkDigState(ChunkRenderData data) {
        data.Chunk = null;
        EnsureChunkAllocated(data);
    }

    private static void RestoreSavedSdf(ChunkRenderData data, ChunkPersistence saved) {
        float[] values = saved.VoxelDataIsFull
            ? VoxelChunk.DecodeFull(saved.VoxelData)
            : VoxelChunk.DecodeQuantized(saved.VoxelData);
        if (saved.AlteredBits != null) data.Chunk.RestoreAltered(values, saved.AlteredBits);
        else data.Chunk.RestoreLegacy(values);
    }

    private void RebuildChunkFromHistory(ChunkRenderData data) {
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);
        long chunkKey = ChunkKey(chunkOriginX, chunkOriginZ);
        if (!_chunkDigHistory.TryGetValue(chunkKey, out var digs) || digs.Count == 0) {
            data.Mesh.Clear();
            SetChunkVisible(data, false);
            if (data.Collider != null) data.Collider.sharedMesh = null;
            return;
        }

        ResetChunkDigState(data);

        if (_pendingPersistence != null
            && _pendingPersistence.TryGetValue(chunkKey, out var savedEntry)
            && savedEntry?.VoxelData != null
            && savedEntry.VoxelData.Length > 0) {
            RestoreSavedSdf(data, savedEntry);
            _pendingPersistence.Remove(chunkKey);
            DebugLog($"  restored SDF for chunk ({digs.Count} digs, full={savedEntry.VoxelDataIsFull})");
        } else {
            int replayed = 0;
            foreach (var dig in digs) {
                CarveCrater(data, dig);
                replayed++;
            }
            DebugLog($"  replayed {replayed} digs onto new chunk");
        }

        RefreshCellOwnership(data, CellRect.Whole);
        RebuildMesh(data);
        MarkNavDirty(chunkOriginX, chunkOriginZ, chunkOriginX + VoxelChunk.SIZE, chunkOriginZ + VoxelChunk.SIZE);
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
