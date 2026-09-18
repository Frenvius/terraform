using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private int RefinePunchFromVoxel(ChunkRenderData data, int wcMinX, int wcMaxX, int wcMinZ, int wcMaxZ) {
        var hm = data.Heightmap;
        if (hm == null || hm.gameObject.name.Contains("lodMesh")) return 0;

        Vector3 origin = data.MeshObject.transform.position;
        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos  = hm.transform.position;
        float hmCellOriginX = hmPos.x - halfSize;
        float hmCellOriginZ = hmPos.z - halfSize;
        float invScale = 1f / hm.m_scale;
        var heights = GetHeightsArray(hm);

        int punched = 0;
        for (int wcZ = wcMinZ; wcZ <= wcMaxZ; wcZ++)
        for (int wcX = wcMinX; wcX <= wcMaxX; wcX++) {
            int hx = Mathf.FloorToInt((wcX - hmCellOriginX) * invScale);
            int hz = Mathf.FloorToInt((wcZ - hmCellOriginZ) * invScale);
            if (hx < 0 || hz < 0 || hx >= hm.m_width || hz >= hm.m_width) continue;
            if (HolePunchSystem.IsHole(hm, hx, hz)) continue;
            if (!CellGroundRemovedByVoxel(data, heights, origin, wcX, wcZ)) continue;
            HolePunchSystem.PunchHole(hm, hx, hz, affectsCollision: true);
            punched++;
        }
        return punched;
    }

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

    private static bool[,] SmoothPunchedCells(bool[,] src, int rangeX, int rangeZ) {
        bool[,] dst = new bool[rangeX, rangeZ];
        for (int z = 0; z < rangeZ; z++)
        for (int x = 0; x < rangeX; x++) {
            int n = 0;
            if (x > 0          && src[x - 1, z]) n++;
            if (x < rangeX - 1 && src[x + 1, z]) n++;
            if (z > 0          && src[x, z - 1]) n++;
            if (z < rangeZ - 1 && src[x, z + 1]) n++;

            if (src[x, z]) dst[x, z] = n >= 2;
            else            dst[x, z] = n >= 3;
        }
        return dst;
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

        for (int i = 0; i < SmoothingPasses; i++)
            mask = SmoothPunchedCells(mask, rangeX, rangeZ);

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

    private static bool CellNeedsPunch(Heightmap hm, int worldCellX, int worldCellZ, float hitY,
                                        float maxCeilingDelta, float fallbackTerrainY) {
        for (int dz = 0; dz <= 1; dz++)
        for (int dx = 0; dx <= 1; dx++) {
            float ty = SampleHeightmapMeshY(
                hm,
                new Vector3(worldCellX + dx, 0f, worldCellZ + dz),
                fallbackTerrainY);
            if (ty - hitY < maxCeilingDelta) return true;
        }
        return false;
    }

    private void PaintTunnelDirt(Vector3 worldPos, float radius) {
        float outerRadius = radius * OuterRadiusMultiplier;
        const float paintBleed = 4f;
        float paintRadius = outerRadius + paintBleed;

        EnsureTerrainCompReflection();
        foreach (var hm in FindObjectsByType<Heightmap>(FindObjectsSortMode.None)) {
            if (hm.gameObject.name.Contains("lodMesh")) continue;
            float halfSize = hm.m_width * hm.m_scale * 0.5f;
            Vector3 hmPos  = hm.transform.position;
            if (Mathf.Abs(worldPos.x - hmPos.x) > halfSize + paintRadius) continue;
            if (Mathf.Abs(worldPos.z - hmPos.z) > halfSize + paintRadius) continue;

            var tc = hm.GetAndCreateTerrainCompiler();
            if (tc != null && _tcPaintClearedMethod != null) {
                _tcPaintClearedMethod.Invoke(tc, new object[] {
                    worldPos, paintRadius, TerrainModifier.PaintType.Dirt, false, false
                });
                _saveMethod?.Invoke(tc, null);
            }
            hm.Poke(false);
        }
    }

    private void PunchHoles(Heightmap _hitHm, Vector3 worldPos, float outerRadius, float paintRadius, float maxCeilingDelta,
                            bool affectsCollision = true, bool doPaint = true) {

        EnsureTerrainCompReflection();

        int totalPunched = 0;
        var allHms = FindObjectsByType<Heightmap>(FindObjectsSortMode.None);
        DebugLog($"PunchHoles: scanning {allHms.Length} heightmaps in scene (outerRadius={outerRadius:F2} maxCeilingDelta={maxCeilingDelta:F2})");
        int wcMinX_world = Mathf.FloorToInt(worldPos.x - outerRadius);
        int wcMaxX_world = Mathf.FloorToInt(worldPos.x + outerRadius);
        int wcMinZ_world = Mathf.FloorToInt(worldPos.z - outerRadius);
        int wcMaxZ_world = Mathf.FloorToInt(worldPos.z + outerRadius);
        var punchedMask = BuildPunchedCellMask(
            wcMinX_world, wcMaxX_world, wcMinZ_world, wcMaxZ_world, worldPos, outerRadius);

        foreach (var hm in allHms) {
            float halfSize = hm.m_width * hm.m_scale * 0.5f;
            Vector3 hmPos  = hm.transform.position;
            bool isSourceChunk =
                Mathf.Abs(worldPos.x - hmPos.x) < halfSize &&
                Mathf.Abs(worldPos.z - hmPos.z) < halfSize;
            float thisPaintRadius = isSourceChunk ? paintRadius : paintRadius + CrossChunkPaintBleed;
            if (Mathf.Abs(worldPos.x - hmPos.x) > halfSize + thisPaintRadius) continue;
            if (Mathf.Abs(worldPos.z - hmPos.z) > halfSize + thisPaintRadius) continue;

            int punchedHere = PunchHolesOnHeightmap(
                hm, worldPos, maxCeilingDelta,
                punchedMask, wcMinX_world, wcMinZ_world,
                wcMaxX_world, wcMaxZ_world, affectsCollision);

            bool hmIsLod = hm.gameObject.name.Contains("lodMesh");
            bool didSomethingHere = punchedHere > 0;
            if (doPaint && !hmIsLod && punchedHere > 0) {
                var tc = hm.GetAndCreateTerrainCompiler();
                if (tc != null && _tcPaintClearedMethod != null) {
                    _tcPaintClearedMethod.Invoke(tc, new object[] {
                        worldPos, thisPaintRadius, TerrainModifier.PaintType.Dirt, false, false
                    });
                    _saveMethod?.Invoke(tc, null);
                }
            }

            if (!didSomethingHere) continue;
            totalPunched += punchedHere;
            if (punchedHere > 0) {
                DebugLog($"  punched {punchedHere} cells on hm={hm.gameObject.name} pos={hmPos} scale={hm.m_scale} width={hm.m_width}");
            }

            hm.Poke(false);
        }

        if (ClutterSystem.instance != null) {
            float grassRadius = outerRadius * Mathf.Sqrt(2f) + 0.5f;
            ClutterSystem.instance.ResetGrass(worldPos, grassRadius);
        }

        DebugLog($"PunchHoles: {totalPunched} cells across hms @ {worldPos}");
    }

    private static int PunchHolesOnHeightmap(
        Heightmap hm,
        Vector3 worldPos,
        float maxCeilingDelta,
        bool[,] punchedMask,
        int wcMinX_world,
        int wcMinZ_world,
        int wcMaxX_world,
        int wcMaxZ_world,
        bool affectsCollision = true) {

        bool hmIsLod = hm.gameObject.name.Contains("lodMesh");
        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos  = hm.transform.position;
        float hmCellOriginX = hmPos.x - halfSize;
        float hmCellOriginZ = hmPos.z - halfSize;
        int cellMinX = Mathf.FloorToInt((wcMinX_world - hmCellOriginX) / hm.m_scale);
        int cellMaxX = Mathf.FloorToInt((wcMaxX_world - hmCellOriginX) / hm.m_scale);
        int cellMinZ = Mathf.FloorToInt((wcMinZ_world - hmCellOriginZ) / hm.m_scale);
        int cellMaxZ = Mathf.FloorToInt((wcMaxZ_world - hmCellOriginZ) / hm.m_scale);

        int punchedHere = 0;
        for (int z = cellMinZ; z <= cellMaxZ; z++)
        for (int x = cellMinX; x <= cellMaxX; x++) {
            if (x < 0 || z < 0 || x >= hm.m_width || z >= hm.m_width) continue;

            int wcX = Mathf.FloorToInt(hmCellOriginX + x * hm.m_scale);
            int wcZ = Mathf.FloorToInt(hmCellOriginZ + z * hm.m_scale);
            if (!IsMaskedCellPunched(punchedMask, wcMinX_world, wcMinZ_world, wcX, wcZ)) continue;
            if (!hmIsLod && !CellNeedsPunch(hm, wcX, wcZ, worldPos.y, maxCeilingDelta, hmPos.y)) continue;

            HolePunchSystem.PunchHole(hm, x, z, affectsCollision);
            punchedHere++;
        }

        return punchedHere;
    }

    public void ResetGrassForAllHistoricalDigs() {
        if (ClutterSystem.instance == null) return;
        if (_chunkDigHistory.Count == 0) return;
        int resets = 0;
        foreach (var list in _chunkDigHistory.Values) {
            for (int i = 0; i < list.Count; i++) {
                var dig = list[i];
                if (dig.Mode == DigMode.Smooth) continue;
                DeriveModePunchParams(dig.Mode, dig.Radius, out float outerRadius, out _);
                float grassRadius = outerRadius * Mathf.Sqrt(2f) + 0.5f;
                ClutterSystem.instance.ResetGrass(dig.WorldPos, grassRadius);
                resets++;
            }
        }
        DebugLog($"[replay-reset-grass] resets={resets}");
    }

    public void PunchAllHistoricalDigsOnHeightmap(Heightmap hm) {
        if (hm == null) return;
        if (hm.gameObject.name.Contains("lodMesh")) return;
        if (_chunkDigHistory.Count == 0) return;

        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos  = hm.transform.position;
        int totalPunched = 0;
        int digsConsidered = 0;

        foreach (var list in _chunkDigHistory.Values) {
            for (int i = 0; i < list.Count; i++) {
                var dig = list[i];
                if (dig.Mode == DigMode.Smooth) continue;
                DeriveModePunchParams(dig.Mode, dig.Radius, out float outerRadius, out float maxCeilingDelta);

                if (Mathf.Abs(dig.WorldPos.x - hmPos.x) > halfSize + outerRadius) continue;
                if (Mathf.Abs(dig.WorldPos.z - hmPos.z) > halfSize + outerRadius) continue;

                int wcMinX = Mathf.FloorToInt(dig.WorldPos.x - outerRadius);
                int wcMaxX = Mathf.FloorToInt(dig.WorldPos.x + outerRadius);
                int wcMinZ = Mathf.FloorToInt(dig.WorldPos.z - outerRadius);
                int wcMaxZ = Mathf.FloorToInt(dig.WorldPos.z + outerRadius);
                var mask = BuildPunchedCellMask(wcMinX, wcMaxX, wcMinZ, wcMaxZ, dig.WorldPos, outerRadius);

                bool affectsCollision = dig.Mode != DigMode.Raise;
                totalPunched += PunchHolesOnHeightmap(
                    hm, dig.WorldPos, maxCeilingDelta, mask, wcMinX, wcMinZ, wcMaxX, wcMaxZ, affectsCollision);
                digsConsidered++;
            }
        }

        if (totalPunched > 0) {
            hm.Poke(false);
            DebugLog($"[replay-punch-all] hm={hm.gameObject.name} digsConsidered={digsConsidered} punched={totalPunched}");
        }
    }

    private static void DeriveModePunchParams(DigMode mode, float radius, out float outerRadius, out float maxCeilingDelta) {
        if (mode == DigMode.Surface) {
            outerRadius     = radius * OuterRadiusMultiplier + ExtendedPunchBleed;
            maxCeilingDelta = SurfacePunchMaxDelta(radius);
        } else if (mode == DigMode.Raise) {
            outerRadius     = radius * OuterRadiusMultiplier + ExtendedPunchBleed;
            maxCeilingDelta = radius * (ShelfDepthMultiplier + BowlDepthMultiplier) + MinCeilingThickness;
        } else {
            float pickaxeOuterRadius = PickaxeRadius + PickaxeNoiseAmplitude;
            outerRadius     = pickaxeOuterRadius;
            maxCeilingDelta = pickaxeOuterRadius * PickaxeHeightMultiplier;
        }
    }
}
