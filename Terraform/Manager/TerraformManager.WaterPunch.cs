using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float WaterDigReachMargin = 0.5f;

    private Dictionary<int, HashSet<int>> _cachedGlobalMasks;

    public void InvalidateWaterMasks() {
        _cachedGlobalMasks = null;
    }

    public void RebuildAllWaterMeshes() {
        InvalidateWaterMasks();
        foreach (var kv in _waters) {
            RebuildWaterMeshFromHistory(kv.Value);
        }
    }

    private void RebuildWaterMeshFromHistory(WaterRenderData data) {
        if (data == null) return;
        var original = data.OriginalSharedMesh;
        var cloned = data.ClonedMesh;
        var filter = data.SurfaceFilter;
        if (original == null || cloned == null || filter == null) return;

        var originalIndices = original.GetIndices(0);

        if (!_chunkDigHistory.TryGetValue(data.ChunkKey, out var digs) || digs.Count == 0) {
            cloned.SetIndices(originalIndices, MeshTopology.Triangles, 0);
            data.ReachableMask = null;
            return;
        }

        Transform waterT = filter.transform;
        float waterY = waterT.position.y;

        List<DigRecord> applicable = null;
        foreach (var dig in digs) {
            if (dig.WorldPos.y - dig.Radius < waterY + WaterDigReachMargin) {
                (applicable ??= new List<DigRecord>()).Add(dig);
            }
        }

        if (applicable == null) {
            cloned.SetIndices(originalIndices, MeshTopology.Triangles, 0);
            data.ReachableMask = null;
            return;
        }

        var globalMasks = EnsureGlobalMasks(waterY);
        globalMasks.TryGetValue(data.Heightmap.GetInstanceID(), out HashSet<int> reachableMask);

        int chunkOriginX = 0, chunkOriginZ = 0;
        if (_chunks.TryGetValue(data.Heightmap.GetInstanceID(), out var chunkData)
            && chunkData != null && chunkData.MeshObject != null) {
            Vector3 chunkOrigin = chunkData.MeshObject.transform.position;
            chunkOriginX = Mathf.RoundToInt(chunkOrigin.x);
            chunkOriginZ = Mathf.RoundToInt(chunkOrigin.z);
        }

        data.ReachableMask = reachableMask;
        data.ChunkOriginX = chunkOriginX;
        data.ChunkOriginZ = chunkOriginZ;

        var localVerts = original.vertices;
        var filtered = new List<int>(originalIndices.Length);
        int dropped = 0, kept = 0, rescued = 0;

        for (int i = 0; i + 2 < originalIndices.Length; i += 3) {
            int ia = originalIndices[i];
            int ib = originalIndices[i + 1];
            int ic = originalIndices[i + 2];

            Vector3 wa = waterT.TransformPoint(localVerts[ia]);
            Vector3 wb = waterT.TransformPoint(localVerts[ib]);
            Vector3 wc = waterT.TransformPoint(localVerts[ic]);

            float cx = (wa.x + wb.x + wc.x) * (1f / 3f);
            float cz = (wa.z + wb.z + wc.z) * (1f / 3f);

            bool insideDig = false;
            for (int d = 0; d < applicable.Count; d++) {
                var dig = applicable[d];
                float dx = cx - dig.WorldPos.x;
                float dz = cz - dig.WorldPos.z;
                float footprint = dig.Radius * OuterRadiusMultiplier;
                if (dx * dx + dz * dz < footprint * footprint) {
                    insideDig = true;
                    break;
                }
            }

            bool drop = insideDig;
            if (drop && reachableMask != null) {
                bool reachable =
                    IsWaterCellReachable(cx,   cz,   reachableMask, chunkOriginX, chunkOriginZ) ||
                    IsWaterCellReachable(wa.x, wa.z, reachableMask, chunkOriginX, chunkOriginZ) ||
                    IsWaterCellReachable(wb.x, wb.z, reachableMask, chunkOriginX, chunkOriginZ) ||
                    IsWaterCellReachable(wc.x, wc.z, reachableMask, chunkOriginX, chunkOriginZ);
                if (reachable) {
                    drop = false;
                    rescued++;
                }
            }

            if (drop) {
                dropped++;
            } else {
                filtered.Add(ia);
                filtered.Add(ib);
                filtered.Add(ic);
                kept++;
            }
        }

        cloned.SetIndices(filtered.ToArray(), MeshTopology.Triangles, 0);

        DebugLog($"[water-punch] hm={data.Heightmap.name} digs={applicable.Count} dropped={dropped} kept={kept} rescued={rescued} reachable={reachableMask?.Count ?? 0}");
    }

    private Dictionary<int, HashSet<int>> EnsureGlobalMasks(float waterY) {
        if (_cachedGlobalMasks != null) return _cachedGlobalMasks;
        _cachedGlobalMasks = ComputeGlobalReachableMasks(waterY);
        return _cachedGlobalMasks;
    }

    private Dictionary<int, HashSet<int>> ComputeGlobalReachableMasks(float waterY) {
        var globalAir = new HashSet<long>();

        foreach (var kv in _chunks) {
            var chunkData = kv.Value;
            if (chunkData == null || chunkData.MeshObject == null) continue;
            Vector3 chunkOrigin = chunkData.MeshObject.transform.position;
            int chunkOriginY = Mathf.RoundToInt(chunkOrigin.y);
            int yIdx = Mathf.RoundToInt(waterY - chunkOriginY);
            if (yIdx < 0 || yIdx > VoxelChunk.SIZE) continue;
            int chunkOriginX = Mathf.RoundToInt(chunkOrigin.x);
            int chunkOriginZ = Mathf.RoundToInt(chunkOrigin.z);
            for (int z = 0; z <= VoxelChunk.SIZE; z++) {
                for (int x = 0; x <= VoxelChunk.SIZE; x++) {
                    if (chunkData.Chunk.Get(x, yIdx, z).Value > 0f) {
                        globalAir.Add(CellKey(chunkOriginX + x, chunkOriginZ + z));
                    }
                }
            }
        }

        var visited = new HashSet<long>();
        var queue = new Queue<long>();

        foreach (long key in globalAir) {
            int wx = (int)(key >> 32);
            int wz = (int)(uint)(key & 0xFFFFFFFFL);
            if (!Heightmap.GetHeight(new Vector3(wx, 0f, wz), out float terrainY)) continue;
            if (terrainY >= waterY) continue;
            if (visited.Add(key)) queue.Enqueue(key);
        }

        while (queue.Count > 0) {
            long key = queue.Dequeue();
            int x = (int)(key >> 32);
            int z = (int)(uint)(key & 0xFFFFFFFFL);
            TryEnqueueGlobalNeighbor(queue, visited, globalAir, x + 1, z);
            TryEnqueueGlobalNeighbor(queue, visited, globalAir, x - 1, z);
            TryEnqueueGlobalNeighbor(queue, visited, globalAir, x, z + 1);
            TryEnqueueGlobalNeighbor(queue, visited, globalAir, x, z - 1);
        }

        var dilated = new HashSet<long>(visited);
        foreach (long key in visited) {
            int x = (int)(key >> 32);
            int z = (int)(uint)(key & 0xFFFFFFFFL);
            dilated.Add(CellKey(x + 1, z));
            dilated.Add(CellKey(x - 1, z));
            dilated.Add(CellKey(x, z + 1));
            dilated.Add(CellKey(x, z - 1));
        }

        var perChunk = new Dictionary<int, HashSet<int>>();
        foreach (var kv in _chunks) {
            var chunkData = kv.Value;
            if (chunkData == null || chunkData.MeshObject == null) continue;
            Vector3 chunkOrigin = chunkData.MeshObject.transform.position;
            int chunkOriginX = Mathf.RoundToInt(chunkOrigin.x);
            int chunkOriginZ = Mathf.RoundToInt(chunkOrigin.z);
            var localMask = new HashSet<int>();
            for (int z = 0; z <= VoxelChunk.SIZE; z++) {
                for (int x = 0; x <= VoxelChunk.SIZE; x++) {
                    if (dilated.Contains(CellKey(chunkOriginX + x, chunkOriginZ + z))) {
                        localMask.Add(WaterCellId(x, z));
                    }
                }
            }
            perChunk[kv.Key] = localMask;
        }

        return perChunk;
    }

    private static void TryEnqueueGlobalNeighbor(Queue<long> queue, HashSet<long> visited,
                                                  HashSet<long> globalAir, int x, int z) {
        long key = CellKey(x, z);
        if (!globalAir.Contains(key)) return;
        if (visited.Add(key)) queue.Enqueue(key);
    }

    private static int WaterCellId(int x, int z) => x * (VoxelChunk.SIZE + 1) + z;

    private static bool IsWaterCellReachable(float worldX, float worldZ, HashSet<int> mask,
                                             int chunkOriginX, int chunkOriginZ) {
        int cellX = Mathf.FloorToInt(worldX - chunkOriginX);
        int cellZ = Mathf.FloorToInt(worldZ - chunkOriginZ);
        if (cellX < 0 || cellX > VoxelChunk.SIZE) return false;
        if (cellZ < 0 || cellZ > VoxelChunk.SIZE) return false;
        return mask.Contains(WaterCellId(cellX, cellZ));
    }

    public bool IsWaterReachableAtPosition(Vector3 worldPos) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;
        if (!_waters.TryGetValue(hm.GetInstanceID(), out var waterData)) return false;
        if (waterData?.ReachableMask == null || waterData.ReachableMask.Count == 0) return false;
        return IsWaterCellReachable(worldPos.x, worldPos.z, waterData.ReachableMask,
                                    waterData.ChunkOriginX, waterData.ChunkOriginZ);
    }
}
