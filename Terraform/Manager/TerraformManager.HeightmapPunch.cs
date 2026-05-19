using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
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

    private static void MergePunchedMaskIntoCarvedCells(
        ChunkRenderData data,
        bool[,] punchedMask,
        int worldCellMinX,
        int worldCellMinZ) {
        int width = punchedMask.GetLength(0);
        int depth = punchedMask.GetLength(1);
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++) {
            if (!punchedMask[x, z]) continue;
            data.CarvedCells.Add(CellKey(worldCellMinX + x, worldCellMinZ + z));
        }
    }

    private static bool IsCarvedCell(ChunkRenderData data, int worldX, int worldZ) =>
        data.CarvedCells.Contains(CellKey(worldX, worldZ));

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

    private static bool CornerNeedsPunch(float cornerTerrainY, float hitY, bool isUnderground, float radius) {
        float maxDelta = isUnderground ? MinCeilingThickness : SurfacePunchMaxDelta(radius);
        return cornerTerrainY - hitY < maxDelta;
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

    private void PunchHoles(Heightmap _hitHm, Vector3 worldPos, float outerRadius, float paintRadius, float maxCeilingDelta) {

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
                wcMaxX_world, wcMaxZ_world);

            bool hmIsLod = hm.gameObject.name.Contains("lodMesh");
            bool didSomethingHere = punchedHere > 0;
            if (!hmIsLod && punchedHere > 0) {
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
        int wcMaxZ_world) {

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

            HolePunchSystem.PunchHole(hm, x, z);
            punchedHere++;
        }

        return punchedHere;
    }

    public void ReplayDigPunchesOnHeightmap(Heightmap hm, System.Collections.Generic.List<DigRecord> digs) {
        if (hm == null || digs == null || digs.Count == 0) return;
        if (hm.gameObject.name.Contains("lodMesh")) return;

        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos  = hm.transform.position;
        int totalPunched = 0;

        foreach (var dig in digs) {
            DeriveModePunchParams(dig.Mode, dig.Radius, out float outerRadius, out float maxCeilingDelta);

            if (Mathf.Abs(dig.WorldPos.x - hmPos.x) > halfSize + outerRadius) continue;
            if (Mathf.Abs(dig.WorldPos.z - hmPos.z) > halfSize + outerRadius) continue;

            int wcMinX = Mathf.FloorToInt(dig.WorldPos.x - outerRadius);
            int wcMaxX = Mathf.FloorToInt(dig.WorldPos.x + outerRadius);
            int wcMinZ = Mathf.FloorToInt(dig.WorldPos.z - outerRadius);
            int wcMaxZ = Mathf.FloorToInt(dig.WorldPos.z + outerRadius);
            var mask = BuildPunchedCellMask(wcMinX, wcMaxX, wcMinZ, wcMaxZ, dig.WorldPos, outerRadius);

            totalPunched += PunchHolesOnHeightmap(
                hm, dig.WorldPos, maxCeilingDelta, mask, wcMinX, wcMinZ, wcMaxX, wcMaxZ);
        }

        if (totalPunched > 0) {
            hm.Poke(false);
            DebugLog($"[replay-punch] hm={hm.gameObject.name} digs={digs.Count} punched={totalPunched}");
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
