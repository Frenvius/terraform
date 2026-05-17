using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float WaterDigReachMargin = 0.5f;

    private void RebuildWaterMeshFromHistory(WaterRenderData data) {
        if (data == null) return;
        var original = data.OriginalSharedMesh;
        var cloned = data.ClonedMesh;
        var filter = data.SurfaceFilter;
        if (original == null || cloned == null || filter == null) return;

        var originalIndices = original.GetIndices(0);

        if (!_chunkDigHistory.TryGetValue(data.ChunkKey, out var digs) || digs.Count == 0) {
            cloned.SetIndices(originalIndices, MeshTopology.Triangles, 0);
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
            return;
        }

        var localVerts = original.vertices;
        var filtered = new List<int>(originalIndices.Length);
        int dropped = 0, kept = 0;

        for (int i = 0; i + 2 < originalIndices.Length; i += 3) {
            int ia = originalIndices[i];
            int ib = originalIndices[i + 1];
            int ic = originalIndices[i + 2];

            Vector3 wa = waterT.TransformPoint(localVerts[ia]);
            Vector3 wb = waterT.TransformPoint(localVerts[ib]);
            Vector3 wc = waterT.TransformPoint(localVerts[ic]);

            float cx = (wa.x + wb.x + wc.x) * (1f / 3f);
            float cz = (wa.z + wb.z + wc.z) * (1f / 3f);

            bool inside = false;
            for (int d = 0; d < applicable.Count; d++) {
                var dig = applicable[d];
                float dx = cx - dig.WorldPos.x;
                float dz = cz - dig.WorldPos.z;
                float footprint = dig.Radius * OuterRadiusMultiplier;
                if (dx * dx + dz * dz < footprint * footprint) {
                    inside = true;
                    break;
                }
            }

            if (inside) {
                dropped++;
            } else {
                filtered.Add(ia);
                filtered.Add(ib);
                filtered.Add(ic);
                kept++;
            }
        }

        cloned.SetIndices(filtered.ToArray(), MeshTopology.Triangles, 0);

        DebugLog($"[water-punch] hm={data.Heightmap.name} digs={applicable.Count} dropped={dropped} kept={kept}");
    }
}
