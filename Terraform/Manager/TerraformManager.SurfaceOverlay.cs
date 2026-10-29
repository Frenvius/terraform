using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool TryWorldToHeightmapCell(Heightmap hm, Vector3 worldPos, out int x, out int z) {
        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos = hm.transform.position;
        float xCell = (worldPos.x - (hmPos.x - halfSize)) / hm.m_scale;
        float zCell = (worldPos.z - (hmPos.z - halfSize)) / hm.m_scale;

        x = Mathf.FloorToInt(xCell);
        z = Mathf.FloorToInt(zCell);
        return x >= 0 && z >= 0 && x < hm.m_width && z < hm.m_width;
    }

    private bool DigTouchesCrater(Vector3 worldPos, float radius) {
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
                if (data.CellCrater[z * Terraform.Voxel.Core.VoxelChunk.SIZE + x]) return true;
            }
        }

        return false;
    }
}
