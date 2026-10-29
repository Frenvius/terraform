using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float CraterMinDepth = 0.5f;

    private readonly HashSet<long> _craterCells = new();
    private readonly HashSet<int> _lodSeen = new();
    private readonly List<long> _craterChanges = new();

    private static bool IsLodHeightmap(Heightmap hm) => hm.gameObject.name.Contains("lodMesh");

    private int RefreshCellOwnership(ChunkRenderData data, CellRect rect) {
        if (data.Chunk == null || data.Heightmap == null || rect.IsEmpty) return 0;

        var hm = data.Heightmap;
        var heights = GetHeightsArray(hm);
        int stride = hm.m_width + 1;
        if (heights == null || heights.Count < stride * stride) return 0;

        Vector3 origin = data.MeshObject.transform.position;
        int originX = Mathf.RoundToInt(origin.x);
        int originY = Mathf.RoundToInt(origin.y);
        int originZ = Mathf.RoundToInt(origin.z);
        float hmY = hm.transform.position.y;

        int changed = 0;
        int minCx = int.MaxValue, maxCx = int.MinValue, minCz = int.MaxValue, maxCz = int.MinValue;
        _craterChanges.Clear();

        for (int cz = rect.MinZ; cz <= rect.MaxZ; cz++)
        for (int cx = rect.MinX; cx <= rect.MaxX; cx++) {
            int index = cz * VoxelChunk.SIZE + cx;
            bool replaced = IsCellReplaced(data.Chunk, heights, stride, hmY, originY, cx, cz);
            bool crater = replaced && IsCraterCell(data, heights, stride, hmY, cx, cz);

            if (crater != data.CellCrater[index]) {
                data.CellCrater[index] = crater;
                long key = ChunkKey(originX + cx, originZ + cz);
                if (crater) _craterCells.Add(key);
                else _craterCells.Remove(key);
                _craterChanges.Add(key);
            }

            if (replaced == data.CellReplaced[index]) continue;
            data.CellReplaced[index] = replaced;
            HolePunchSystem.SetCell(hm, cx, cz, replaced);
            changed++;
            minCx = Mathf.Min(minCx, cx); maxCx = Mathf.Max(maxCx, cx);
            minCz = Mathf.Min(minCz, cz); maxCz = Mathf.Max(maxCz, cz);
        }

        if (_craterChanges.Count > 0) ApplyLodHoles(_craterChanges);

        if (changed > 0) {
            hm.Poke();
            ResetGrassOverCells(originX, originZ, minCx, maxCx, minCz, maxCz);
        }

        if (VerboseLogs) {
            int replacedTotal = 0;
            for (int i = 0; i < data.CellReplaced.Length; i++) if (data.CellReplaced[i]) replacedTotal++;
            DebugLog($"[cells] hm={hm.gameObject.name} replaced={replacedTotal} changed={changed}");
        }
        return changed;
    }

    private static void ResetGrassOverCells(int originX, int originZ, int minCx, int maxCx, int minCz, int maxCz) {
        if (ClutterSystem.instance == null) return;
        float centerX = originX + (minCx + maxCx + 1) * 0.5f;
        float centerZ = originZ + (minCz + maxCz + 1) * 0.5f;
        float halfX = (maxCx - minCx + 1) * 0.5f;
        float halfZ = (maxCz - minCz + 1) * 0.5f;
        float radius = Mathf.Sqrt(halfX * halfX + halfZ * halfZ) + 0.5f;
        ClutterSystem.instance.ResetGrass(new Vector3(centerX, 0f, centerZ), radius);
    }

    private static bool IsCellReplaced(
        VoxelChunk chunk, List<float> heights, int stride, float hmY, int originY, int cx, int cz) {
        float h00 = heights[cz * stride + cx] + hmY;
        float h10 = heights[cz * stride + cx + 1] + hmY;
        float h01 = heights[(cz + 1) * stride + cx] + hmY;
        float h11 = heights[(cz + 1) * stride + cx + 1] + hmY;
        float minH = Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h01, h11));
        float maxH = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));

        int yLo = Mathf.Max(0, Mathf.CeilToInt(minH - 1f - originY));
        int yHi = Mathf.Min(VoxelChunk.SIZE, Mathf.FloorToInt(maxH + 1f - originY));

        for (int y = yLo; y <= yHi; y++) {
            if (chunk.IsAltered(cx, y, cz)
                || chunk.IsAltered(cx + 1, y, cz)
                || chunk.IsAltered(cx, y, cz + 1)
                || chunk.IsAltered(cx + 1, y, cz + 1)) return true;
        }
        return false;
    }

    private static bool IsCraterCell(
        ChunkRenderData data, List<float> heights, int stride, float hmY, int cx, int cz) {
        for (int dz = 0; dz <= 1; dz++)
        for (int dx = 0; dx <= 1; dx++) {
            int vx = cx + dx, vz = cz + dz;
            float h = heights[vz * stride + vx] + hmY;
            if (VoxelTopSurfaceY(data, vx, vz, h) < h - CraterMinDepth) return true;
        }
        return false;
    }

    private static float VoxelTopSurfaceY(ChunkRenderData data, int vx, int vz, float fallbackY) {
        var chunk = data.Chunk;
        int originY = Mathf.RoundToInt(data.MeshObject.transform.position.y);

        if (chunk.Get(vx, VoxelChunk.SIZE, vz).Value < 0f) return originY + VoxelChunk.SIZE;

        for (int y = VoxelChunk.SIZE - 1; y >= 0; y--) {
            float here = chunk.Get(vx, y, vz).Value;
            if (here >= 0f) continue;
            float above = chunk.Get(vx, y + 1, vz).Value;
            return originY + y + here / (here - above);
        }
        return fallbackY;
    }

    private static bool IsColumnReplaced(ChunkRenderData data, int vx, int vz) {
        for (int dz = -1; dz <= 0; dz++)
        for (int dx = -1; dx <= 0; dx++) {
            int cx = vx + dx, cz = vz + dz;
            if (cx < 0 || cz < 0 || cx >= VoxelChunk.SIZE || cz >= VoxelChunk.SIZE) continue;
            if (data.CellReplaced[cz * VoxelChunk.SIZE + cx]) return true;
        }
        return false;
    }

    private static float ColumnTopY(ChunkRenderData data, int vx, int vz, float heightmapY) =>
        IsColumnReplaced(data, vx, vz) ? VoxelTopSurfaceY(data, vx, vz, heightmapY) : heightmapY;

    private static void DecodeCellKey(long key, out int worldX, out int worldZ) {
        worldX = (int)(key >> 32);
        worldZ = (int)(key & 0xFFFFFFFFL);
    }

    private bool LodCellHasCrater(Heightmap lod, int x, int z) {
        float half = lod.m_width * lod.m_scale * 0.5f;
        Vector3 pos = lod.transform.position;
        int minWx = Mathf.FloorToInt(pos.x - half + x * lod.m_scale);
        int minWz = Mathf.FloorToInt(pos.z - half + z * lod.m_scale);
        int size = Mathf.CeilToInt(lod.m_scale);
        for (int dz = 0; dz < size; dz++)
        for (int dx = 0; dx < size; dx++)
            if (_craterCells.Contains(ChunkKey(minWx + dx, minWz + dz))) return true;
        return false;
    }

    private bool SyncLodCell(Heightmap lod, int x, int z) {
        bool crater = LodCellHasCrater(lod, x, z);
        if (crater == HolePunchSystem.IsHole(lod, x, z)) return false;
        HolePunchSystem.SetCell(lod, x, z, crater);
        return true;
    }

    private void ApplyLodHoles(List<long> cellKeys) {
        foreach (var lod in Heightmap.GetAllHeightmaps()) {
            if (!IsLodHeightmap(lod)) continue;
            bool changed = false;
            foreach (long key in cellKeys) {
                DecodeCellKey(key, out int wx, out int wz);
                if (!TryWorldToHeightmapCell(lod, new Vector3(wx + 0.5f, 0f, wz + 0.5f), out int x, out int z)) continue;
                changed |= SyncLodCell(lod, x, z);
            }
            if (changed) lod.Poke();
        }
    }

    private void ApplyAllCratersToLod(Heightmap lod) {
        if (_craterCells.Count == 0) return;
        bool changed = false;
        foreach (long key in _craterCells) {
            DecodeCellKey(key, out int wx, out int wz);
            if (!TryWorldToHeightmapCell(lod, new Vector3(wx + 0.5f, 0f, wz + 0.5f), out int x, out int z)) continue;
            changed |= SyncLodCell(lod, x, z);
        }
        if (changed) lod.Poke();
    }

    private static CellRect CellRectForDig(ChunkRenderData data, DigRecord dig) {
        Vector3 origin = data.MeshObject.transform.position;
        int originX = Mathf.RoundToInt(origin.x);
        int originZ = Mathf.RoundToInt(origin.z);
        float extent = DigFootprintRadius(dig);
        const int margin = 2;
        return new CellRect(
            Mathf.FloorToInt(dig.WorldPos.x - extent) - originX - margin,
            Mathf.FloorToInt(dig.WorldPos.x + extent) - originX + margin,
            Mathf.FloorToInt(dig.WorldPos.z - extent) - originZ - margin,
            Mathf.FloorToInt(dig.WorldPos.z + extent) - originZ + margin);
    }

    private static float DigFootprintRadius(DigRecord dig) {
        switch (dig.Mode) {
            case DigMode.Surface:
            case DigMode.Raise:
                return dig.Radius * OuterRadiusMultiplier;
            case DigMode.Smooth:
            case DigMode.Level:
                return dig.Radius;
            default:
                return PickaxeRadius + PickaxeNoiseAmplitude;
        }
    }
}
