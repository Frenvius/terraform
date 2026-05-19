using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool IsSurfaceOverlayOnIntactHeightmap(
        ChunkRenderData data,
        Vector3 origin,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        float offA,
        float offB,
        float offC) {
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
        CountSample(hm, wa, ref intactSamples, ref punchedSamples);
        CountSample(hm, wb, ref intactSamples, ref punchedSamples);
        CountSample(hm, wc, ref intactSamples, ref punchedSamples);
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

    private static void CountSample(Heightmap hm, Vector3 worldPos, ref int intact, ref int punched) {
        switch (ClassifyPoint(hm, worldPos)) {
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
