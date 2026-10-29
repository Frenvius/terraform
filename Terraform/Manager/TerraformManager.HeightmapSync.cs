using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float TerrainCompMaxDelta = 8f;

    private static readonly AccessTools.FieldRef<TerrainComp, float[]> TerrainCompLevelDelta =
        AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
    private static readonly AccessTools.FieldRef<TerrainComp, float[]> TerrainCompSmoothDelta =
        AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
    private static readonly AccessTools.FieldRef<TerrainComp, bool[]> TerrainCompModifiedHeight =
        AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");

    private readonly List<(int Index, float Height)> _heightmapSyncTargets = new();

    private void SyncHeightmapToVoxelSurface(ChunkRenderData data, DigRecord dig) {
        if (IsCeilingNormal(dig.Normal)) return;
        CollectHeightmapSyncTargets(data, dig, _heightmapSyncTargets);
        if (_heightmapSyncTargets.Count == 0) return;

        var hm = data.Heightmap;
        var tc = hm.GetAndCreateTerrainCompiler();
        if (tc == null || !SetVertexHeights(tc, hm, _heightmapSyncTargets)) return;

        EnsureTerrainCompReflection();
        SaveTerrainComp(tc);
        hm.Poke();
        ClutterSystem.instance?.ResetGrass(dig.WorldPos, dig.Radius + 1f);
        if (VerboseLogs) DebugLog($"[heightmap-sync] hm={hm.gameObject.name} vertices={_heightmapSyncTargets.Count}");
    }

    private static void CollectHeightmapSyncTargets(ChunkRenderData data, DigRecord dig, List<(int Index, float Height)> targets) {
        targets.Clear();
        var hm = data.Heightmap;
        Vector3 origin   = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);
        var columns = SurfaceMoveColumns(dig);
        var query   = SurfaceQueryFor(dig, Mathf.RoundToInt(origin.y));
        var falloff = FalloffFor(dig);
        float hmY   = hm.transform.position.y;
        int stride  = hm.m_width + 1;

        for (int worldVz = columns.MinZ; worldVz <= columns.MaxZ; worldVz++)
        for (int worldVx = columns.MinX; worldVx <= columns.MaxX; worldVx++) {
            int vx = worldVx - chunkOriginX;
            int vz = worldVz - chunkOriginZ;
            if (vx < 0 || vx > VoxelChunk.SIZE || vz < 0 || vz > VoxelChunk.SIZE) continue;
            if (SurfaceMoveWeight(falloff, worldVx - dig.WorldPos.x, worldVz - dig.WorldPos.z, dig.Radius) <= 0f) continue;
            if (!TryFindColumnSurface(data.Chunk, vx, vz, query, out var surface) || !surface.OpenToSky) continue;

            hm.WorldToVertex(new Vector3(worldVx, 0f, worldVz), out int x, out int z);
            if (x < 0 || z < 0 || x >= stride || z >= stride) continue;

            float height = surface.WorldY - hmY;
            if (height < hm.GetHeight(x, z) && IsVertexInsideCollisionHoles(hm, worldVx, worldVz)) continue;
            targets.Add((z * stride + x, height));
        }
    }

    private static bool SetVertexHeights(TerrainComp tc, Heightmap hm, List<(int Index, float Height)> targets) {
        var level    = TerrainCompLevelDelta(tc);
        var smooth   = TerrainCompSmoothDelta(tc);
        var modified = TerrainCompModifiedHeight(tc);
        if (level == null || smooth == null || modified == null) return false;

        int stride = hm.m_width + 1;
        foreach (var (index, height) in targets) {
            float current    = hm.GetHeight(index % stride, index / stride);
            float baseHeight = current - Mathf.Clamp(level[index] + smooth[index], -TerrainCompMaxDelta, TerrainCompMaxDelta);
            level[index]    = Mathf.Clamp(height - baseHeight, -TerrainCompMaxDelta, TerrainCompMaxDelta);
            smooth[index]   = 0f;
            modified[index] = true;
        }
        return true;
    }
}
