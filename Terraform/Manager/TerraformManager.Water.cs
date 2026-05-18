using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private Dictionary<int, WaterRenderData> _waters;

    private void ScanWaterVolumes() {
        List<int> dead = null;
        foreach (var kv in _waters) {
            var d = kv.Value;
            if (d == null || d.Volume == null || d.SurfaceFilter == null) {
                (dead ??= new List<int>()).Add(kv.Key);
            }
        }
        if (dead != null) {
            foreach (var k in dead) {
                var d = _waters[k];
                if (d?.ClonedMesh != null) Destroy(d.ClonedMesh);
                _waters.Remove(k);
            }
        }

        foreach (var wv in FindObjectsByType<WaterVolume>(FindObjectsSortMode.None)) {
            if (wv.m_heightmap == null) continue;
            int key = wv.m_heightmap.GetInstanceID();
            if (_waters.ContainsKey(key)) continue;
            if (!_chunks.TryGetValue(key, out var chunkData)) continue;

            if (wv.m_waterSurface == null) continue;
            var filter = wv.m_waterSurface.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;

            var original = filter.sharedMesh;
            var cloned = Object.Instantiate(original);
            cloned.name = original.name + "_TerraformClone";
            filter.sharedMesh = cloned;

            Vector3 chunkOrigin = chunkData.MeshObject.transform.position;
            long chunkKey = ChunkKey(Mathf.RoundToInt(chunkOrigin.x), Mathf.RoundToInt(chunkOrigin.z));

            var data = new WaterRenderData {
                Volume             = wv,
                Heightmap          = wv.m_heightmap,
                SurfaceFilter      = filter,
                OriginalSharedMesh = original,
                ClonedMesh         = cloned,
                ChunkKey           = chunkKey,
            };
            _waters[key] = data;

            DebugLog($"WaterRenderData -> {wv.gameObject.name} verts={cloned.vertexCount} tris={cloned.triangles.Length / 3}");

            RebuildWaterMeshFromHistory(data);
        }
    }
}
