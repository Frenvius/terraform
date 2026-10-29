using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float SeedSurfaceValue = 0.001f;

    private void EnsureChunkAllocated(ChunkRenderData data) {
        if (data.Chunk != null) return;
        data.Chunk = new VoxelChunk();
        SeedIntactVoxels(data);
    }

    private void SeedIntactVoxels(ChunkRenderData data) {
        var heights = GetHeightsArray(data.Heightmap);
        int stride = data.Heightmap.m_width + 1;
        if (heights == null || heights.Count < stride * stride) return;

        float hmY = data.Heightmap.transform.position.y;
        int originY = Mathf.RoundToInt(data.MeshObject.transform.position.y);
        var chunk = data.Chunk;
        int intact = 0, altered = 0;

        for (int vz = 0; vz <= VoxelChunk.SIZE; vz++)
        for (int vx = 0; vx <= VoxelChunk.SIZE; vx++) {
            float height = heights[vz * stride + vx] + hmY;
            for (int vy = 0; vy <= VoxelChunk.SIZE; vy++) {
                if (chunk.IsAltered(vx, vy, vz)) { altered++; continue; }
                float seed = Mathf.Clamp(originY + vy - height, -1f, 1f);
                if (seed == 0f) seed = SeedSurfaceValue;
                chunk.SetIntact(vx, vy, vz, seed);
                intact++;
            }
        }

        data.SeededHeightsHash = ComputeHeightsHash(data.Heightmap);
        if (VerboseLogs)
            DebugLog($"[seed] hm={data.Heightmap.gameObject.name} intact={intact} altered={altered} reseeds={data.ReseedCount}");
    }

    private void ReseedDirtyChunks() {
        var dirty = HolePunchSystem.TakeHeightsDirty();
        if (dirty == null) return;

        foreach (int id in dirty) {
            if (!_chunks.TryGetValue(id, out var data) || data?.Heightmap == null || data.Chunk == null) continue;
            if (_pendingBuildHash.ContainsKey(id)) continue;
            if (ComputeHeightsHash(data.Heightmap) == data.SeededHeightsHash) {
                if (data.UndergroundMask != null) RefreshUndergroundMasks(data);
                continue;
            }

            data.ReseedCount++;
            SeedIntactVoxels(data);
            RefreshCellOwnership(data, CellRect.Whole);
            RebuildMesh(data);
        }
    }

    private static long ComputeHeightsHash(Heightmap hm) {
        var heights = GetHeightsArray(hm);
        if (heights == null) return 0;
        long h = unchecked((long)14695981039346656037UL);
        for (int i = 0; i < heights.Count; i++) {
            long bits = (long)(heights[i] * 1000000f);
            h = (h ^ bits) * 1099511628211L;
        }
        return h;
    }
}
