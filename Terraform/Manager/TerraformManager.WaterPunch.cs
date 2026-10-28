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
        float pickaxeReach  = PickaxeRadius + PickaxeNoiseAmplitude;
        float pickaxeReachY = pickaxeReach * PickaxeHeightMultiplier;
        foreach (var dig in digs) {
            float surfaceBowlDepth = dig.Radius * (BowlDepthMultiplier + ShelfDepthMultiplier);
            float reachY = dig.Mode == DigMode.Surface ? surfaceBowlDepth + WaterDigReachMargin
                                                       : pickaxeReachY + WaterDigReachMargin;
            if (dig.WorldPos.y - waterY > reachY) continue;
            if (waterY - dig.WorldPos.y > reachY) continue;
            (applicable ??= new List<DigRecord>()).Add(dig);
        }

        if (applicable == null) {
            cloned.SetIndices(originalIndices, MeshTopology.Triangles, 0);
            return;
        }

        _chunks.TryGetValue(data.Heightmap.GetInstanceID(), out var chunk);
        var heights = chunk != null ? GetHeightsArray(chunk.Heightmap) : null;
        Vector3 chunkOrigin = chunk != null ? chunk.MeshObject.transform.position : Vector3.zero;

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

            bool insideDig = false;
            float footprintSq = pickaxeReach * pickaxeReach;
            for (int d = 0; d < applicable.Count; d++) {
                var dig = applicable[d];
                float dx = cx - dig.WorldPos.x;
                float dz = cz - dig.WorldPos.z;
                if (dx * dx + dz * dz >= footprintSq) continue;
                if (dig.Mode != DigMode.Surface && !GroundOpenedUnder(chunk, heights, chunkOrigin, cx, cz)) continue;
                insideDig = true;
                break;
            }

            if (insideDig) {
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

    private static bool GroundOpenedUnder(ChunkRenderData chunk, List<float> heights, Vector3 chunkOrigin, float x, float z) =>
        chunk?.Chunk != null
        && CellGroundRemovedByVoxel(chunk, heights, chunkOrigin, Mathf.FloorToInt(x), Mathf.FloorToInt(z));
}
