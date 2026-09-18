using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool IsSurfaceOverlayOnIntactHeightmap(
        ChunkRenderData data,
        Vector3 origin,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        CellState stateA,
        CellState stateB,
        CellState stateC) {
        if (data.Heightmap == null) return false;

        Vector3 cross = Vector3.Cross(b - a, c - a);
        if (cross.sqrMagnitude < 1e-6f) return false;
        if (cross.normalized.y < 0.2f) return false;

        Vector3 wa = origin + a;
        Vector3 wb = origin + b;
        Vector3 wc = origin + c;
        Vector3 centroid = (wa + wb + wc) / 3f;

        var hm = data.Heightmap;
        var centroidState = ClassifyPoint(hm, centroid);
        if (centroidState == CellState.Punched) return false;

        int intactSamples = 0;
        int punchedSamples = 0;
        CountState(stateA, ref intactSamples, ref punchedSamples);
        CountState(stateB, ref intactSamples, ref punchedSamples);
        CountState(stateC, ref intactSamples, ref punchedSamples);
        CountSample(hm, (wa + wb) * 0.5f, ref intactSamples, ref punchedSamples);
        CountSample(hm, (wb + wc) * 0.5f, ref intactSamples, ref punchedSamples);
        CountSample(hm, (wc + wa) * 0.5f, ref intactSamples, ref punchedSamples);
        CountSample(hm, centroid, ref intactSamples, ref punchedSamples);

        if (centroidState != CellState.Intact) return false;
        if (punchedSamples > 0) return false;
        return intactSamples >= 5;
    }

    private enum CellState { Outside, Intact, Punched }

    private static CellState ClassifyPoint(Heightmap hm, Vector3 worldPos) {
        Heightmap target = hm;
        if (!TryWorldToHeightmapCell(target, worldPos, out int x, out int z)) {
            target = Heightmap.FindHeightmap(worldPos);
            if (target == null || target.gameObject.name.Contains("lodMesh")) return CellState.Outside;
            if (!TryWorldToHeightmapCell(target, worldPos, out x, out z)) return CellState.Outside;
        }
        return HolePunchSystem.IsHole(target, x, z) ? CellState.Punched : CellState.Intact;
    }

    private static CellState ClassifyVertexCells(
        Heightmap hm, Vector3 origin, float localX, float localZ, out bool onPunchSeam) {
        const float gridLineTolerance = 0.005f;
        int xLo = Mathf.FloorToInt(localX - gridLineTolerance);
        int xHi = Mathf.FloorToInt(localX + gridLineTolerance);
        int zLo = Mathf.FloorToInt(localZ - gridLineTolerance);
        int zHi = Mathf.FloorToInt(localZ + gridLineTolerance);

        bool anyPunched = false, anyIntact = false, anyOutside = false;
        for (int cz = zLo; cz <= zHi; cz++)
        for (int cx = xLo; cx <= xHi; cx++) {
            switch (ClassifyPoint(hm, new Vector3(origin.x + cx + 0.5f, 0f, origin.z + cz + 0.5f))) {
                case CellState.Punched: anyPunched = true; break;
                case CellState.Intact:  anyIntact  = true; break;
                default:                anyOutside = true; break;
            }
        }

        onPunchSeam = anyPunched && (anyIntact || anyOutside);
        if (anyPunched) return CellState.Punched;
        return anyIntact ? CellState.Intact : CellState.Outside;
    }

    private static void CountSample(Heightmap hm, Vector3 worldPos, ref int intact, ref int punched) {
        CountState(ClassifyPoint(hm, worldPos), ref intact, ref punched);
    }

    private static void CountState(CellState state, ref int intact, ref int punched) {
        switch (state) {
            case CellState.Intact:  intact++;  break;
            case CellState.Punched: punched++; break;
        }
    }

    private static bool TryWorldToHeightmapCell(Heightmap hm, Vector3 worldPos, out int x, out int z) {
        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos = hm.transform.position;
        float xCell = (worldPos.x - (hmPos.x - halfSize)) / hm.m_scale;
        float zCell = (worldPos.z - (hmPos.z - halfSize)) / hm.m_scale;

        x = Mathf.FloorToInt(xCell);
        z = Mathf.FloorToInt(zCell);
        return x >= 0 && z >= 0 && x < hm.m_width && z < hm.m_width;
    }

    private bool DigIntersectsExistingSurfaceHole(Vector3 worldPos, float radius) {
        float outerRadius = radius * OuterRadiusMultiplier;
        int worldCellMinX = Mathf.FloorToInt(worldPos.x - outerRadius);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + outerRadius);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - outerRadius);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + outerRadius);

        foreach (var data in _chunks.Values) {
            var hm = data?.Heightmap;
            if (hm == null || hm.gameObject.name.Contains("lodMesh")) continue;

            float halfSize = hm.m_width * hm.m_scale * 0.5f;
            Vector3 hmPos  = hm.transform.position;
            if (Mathf.Abs(worldPos.x - hmPos.x) > halfSize + outerRadius) continue;
            if (Mathf.Abs(worldPos.z - hmPos.z) > halfSize + outerRadius) continue;

            float hmCellOriginX = hmPos.x - halfSize;
            float hmCellOriginZ = hmPos.z - halfSize;
            int cellMinX = Mathf.FloorToInt((worldCellMinX - hmCellOriginX) / hm.m_scale);
            int cellMaxX = Mathf.FloorToInt((worldCellMaxX - hmCellOriginX) / hm.m_scale);
            int cellMinZ = Mathf.FloorToInt((worldCellMinZ - hmCellOriginZ) / hm.m_scale);
            int cellMaxZ = Mathf.FloorToInt((worldCellMaxZ - hmCellOriginZ) / hm.m_scale);

            for (int z = cellMinZ; z <= cellMaxZ; z++)
            for (int x = cellMinX; x <= cellMaxX; x++) {
                if (x < 0 || z < 0 || x >= hm.m_width || z >= hm.m_width) continue;
                if (HolePunchSystem.IsHole(hm, x, z)) return true;
            }
        }

        return false;
    }
}
