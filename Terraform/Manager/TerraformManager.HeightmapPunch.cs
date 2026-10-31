using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool CellGroundRemovedByVoxel(
        ChunkRenderData data, List<float> heights, Vector3 origin, int wcX, int wcZ) {
        float wx = wcX + 0.5f;
        float wz = wcZ + 0.5f;
        float terrainY = SampleHeightmapMeshYCached(data.Heightmap, heights, new Vector3(wx, 0f, wz), data.TerrainHeight);

        float localX = wx - origin.x;
        float localZ = wz - origin.z;
        if (localX < 0f || localX > VoxelChunk.SIZE || localZ < 0f || localZ > VoxelChunk.SIZE) return false;

        float localY = terrainY - origin.y - SeamProbeDepth;
        if (localY < 0f) localY = 0f;
        if (localY > VoxelChunk.SIZE) return false;

        return SampleSdfTri(data.Chunk, localX, localY, localZ) > 0f;
    }

    private static bool[,] BuildPunchedCellMask(
        int worldCellMinX,
        int worldCellMaxX,
        int worldCellMinZ,
        int worldCellMaxZ,
        Vector3 worldPos,
        float outerRadius) {
        int rangeX = worldCellMaxX - worldCellMinX + 1;
        int rangeZ = worldCellMaxZ - worldCellMinZ + 1;
        var mask = new bool[rangeX, rangeZ];
        float effectiveRadius = Mathf.Max(0f, outerRadius - 0.15f);
        float outerRadiusSq = effectiveRadius * effectiveRadius;

        for (int z = 0; z < rangeZ; z++)
        for (int x = 0; x < rangeX; x++) {
            float cellCenterX = worldCellMinX + x + 0.5f;
            float cellCenterZ = worldCellMinZ + z + 0.5f;
            float dx = Mathf.Max(Mathf.Abs(cellCenterX - worldPos.x) - 0.5f, 0f);
            float dz = Mathf.Max(Mathf.Abs(cellCenterZ - worldPos.z) - 0.5f, 0f);
            mask[x, z] = (dx * dx + dz * dz) <= outerRadiusSq;
        }

        return mask;
    }

    private static bool IsMaskedCellPunched(
        bool[,] punchedMask,
        int worldCellMinX,
        int worldCellMinZ,
        int worldX,
        int worldZ) {
        int x = worldX - worldCellMinX;
        int z = worldZ - worldCellMinZ;
        return x >= 0 && z >= 0
            && x < punchedMask.GetLength(0)
            && z < punchedMask.GetLength(1)
            && punchedMask[x, z];
    }

    private int PaintCellsOpenedByVoxel(ChunkRenderData data, int wcMinX, int wcMaxX, int wcMinZ, int wcMaxZ) {
        var hm = data.Heightmap;
        if (hm == null || hm.gameObject.name.Contains("lodMesh")) return 0;
        EnsureTerrainCompReflection();
        if (_tcPaintClearedMethod == null) return 0;

        Vector3 origin = data.MeshObject.transform.position;
        var heights = GetHeightsArray(hm);
        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        TerrainComp tc = null;
        int painted = 0;
        for (int wcZ = wcMinZ; wcZ <= wcMaxZ; wcZ++)
        for (int wcX = wcMinX; wcX <= wcMaxX; wcX++) {
            var cellCenter = new Vector3(wcX + 0.5f, 0f, wcZ + 0.5f);
            if (SampleHeightmapMeshYCached(hm, heights, cellCenter, waterY) < waterY) continue;
            if (!CellGroundRemovedByVoxel(data, heights, origin, wcX, wcZ)) continue;
            tc ??= hm.GetAndCreateTerrainCompiler();
            if (tc == null) return 0;
            if (tc.IsOwner()) PaintDirtCleared(tc, cellCenter, OpenedCellPaintRadius);
            else DirtPaintOp.Request(tc, cellCenter);
            painted++;
        }

        if (painted > 0 && tc.IsOwner()) SaveTerrainComp(tc);
        return painted;
    }
}
