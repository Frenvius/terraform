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
                if (d != null) DestroyPaintBandMasks(d);
                _chunks.Remove(k);
                _pendingBuildHash.Remove(k);
                _pendingBuildSince.Remove(k);
                _postLoadHeightsHash.Remove(k);
                _postLoadWatchUntil.Remove(k);
            }
        }

        foreach (var hm in Heightmap.GetAllHeightmaps()) {
            int id = hm.GetInstanceID();
            if (_chunks.ContainsKey(id)) continue;

            if (hm.gameObject.name.Contains("lodMesh")) continue;

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
        UpdatePostLoadWatch();

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

        foreach (var data in built)
            PunchAllHistoricalDigsOnHeightmap(data.Heightmap);

        ResetGrassForAllHistoricalDigs();

        foreach (var data in built) {
            RebuildChunkFromHistory(data);
            if (_waters.TryGetValue(data.Heightmap.GetInstanceID(), out var water)) RebuildWaterMeshFromHoles(water);
        }

        foreach (var data in built) {
            if (data?.Heightmap == null) continue;
            int id = data.Heightmap.GetInstanceID();
            _postLoadHeightsHash[id] = ComputeHeightsHash(data.Heightmap);
            _postLoadWatchUntil[id]  = Time.time + PostLoadWatchDuration;
        }
    }

    private void UpdatePostLoadWatch() {
        if (_postLoadWatchUntil.Count == 0) return;

        List<int> changed = null;
        List<int> expired = null;
        foreach (var kv in _postLoadWatchUntil) {
            int id = kv.Key;
            if (!_chunks.TryGetValue(id, out var data) || data?.Heightmap == null) {
                (expired ??= new List<int>()).Add(id);
                continue;
            }
            long current = ComputeHeightsHash(data.Heightmap);
            if (!_postLoadHeightsHash.TryGetValue(id, out var prev) || current != prev) {
                _postLoadHeightsHash[id] = current;
                (changed ??= new List<int>()).Add(id);
            }
            if (Time.time > kv.Value)
                (expired ??= new List<int>()).Add(id);
        }

        if (changed != null) {
            foreach (var id in changed) {
                if (_chunks.TryGetValue(id, out var data) && data?.Heightmap != null) {
                    RebuildMesh(data);
                    DebugLog($"[postload-reclamp] hm={data.Heightmap.gameObject.name} heightmap settled → re-clamped voxel mesh");
                }
            }
        }

        if (expired != null) {
            foreach (var id in expired) {
                _postLoadWatchUntil.Remove(id);
                _postLoadHeightsHash.Remove(id);
            }
        }
    }

    private static long ComputeHeightsHash(Heightmap hm) {
        var heights = GetHeightsArray(hm);
        if (heights == null) return 0;
        long h = unchecked((long)14695981039346656037UL);
        int count = heights.Count;
        int step = count > 512 ? count / 512 : 1;
        for (int i = 0; i < count; i += step) {
            long bits = (long)(heights[i] * 1000000f);
            h = (h ^ bits) * 1099511628211L;
        }
        return h;
    }

    private static void ResetChunkDigState(ChunkRenderData data) {
        data.Chunk = new VoxelChunk();
        data.RenderFootprints.Clear();
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
            if (savedEntry.VoxelDataIsFull)
                data.Chunk.DeserializeFull(savedEntry.VoxelData);
            else
                data.Chunk.DeserializeQuantized(savedEntry.VoxelData);
            foreach (var dig in digs) AddRenderFootprintFromDig(data, dig);
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

        RebuildMesh(data);
        MarkNavDirty(chunkOriginX, chunkOriginZ, chunkOriginX + VoxelChunk.SIZE, chunkOriginZ + VoxelChunk.SIZE);
    }

    private static void AddRenderFootprintFromDig(ChunkRenderData data, DigRecord dig) {
        float apronRadius;
        switch (dig.Mode) {
            case DigMode.Surface:
            case DigMode.Raise:
                apronRadius = dig.Radius * OuterRadiusMultiplier + ExtendedPunchBleed;
                break;
            case DigMode.Smooth:
            case DigMode.Level:
                apronRadius = dig.Radius;
                break;
            default:
                apronRadius = PickaxeRadius + PickaxeNoiseAmplitude;
                break;
        }
        int minX = Mathf.FloorToInt(dig.WorldPos.x - apronRadius);
        int maxX = Mathf.FloorToInt(dig.WorldPos.x + apronRadius);
        int minZ = Mathf.FloorToInt(dig.WorldPos.z - apronRadius);
        int maxZ = Mathf.FloorToInt(dig.WorldPos.z + apronRadius);
        AddRenderFootprint(data, minX, maxX + 1, minZ, maxZ + 1);
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
