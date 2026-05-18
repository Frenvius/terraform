using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private readonly List<GameObject> _leakDebugMarkers = new();
    private bool _leakDebugVisible;
    private bool _waterHidden;

    private void ToggleHideWater() {
        _waterHidden = !_waterHidden;
        int affected = 0;
        foreach (var kv in _waters) {
            var d = kv.Value;
            if (d?.SurfaceFilter == null) continue;
            var rend = d.SurfaceFilter.GetComponent<MeshRenderer>();
            if (rend == null) continue;
            rend.enabled = !_waterHidden;
            affected++;
        }
        TerraformPlugin.Log.LogInfo($"[water-hide] hidden={_waterHidden} renderers={affected}");
    }

    private void ToggleLeakDebug() {
        DumpLeakDebugReport();
        _leakDebugVisible = !_leakDebugVisible;
        ClearLeakDebugMarkers();
        if (_leakDebugVisible) BuildLeakDebugMarkers();
        TerraformPlugin.Log.LogInfo($"[leak-debug] markers={_leakDebugVisible}");
    }

    private void DumpLeakDebugReport() {
        var player = Player.m_localPlayer;
        Vector3 pp = player != null ? player.transform.position : Vector3.zero;
        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

        TerraformPlugin.Log.LogInfo("[leak-debug] ===== REPORT BEGIN =====");
        TerraformPlugin.Log.LogInfo($"[leak-debug] player.pos=({pp.x:F2}, {pp.y:F2}, {pp.z:F2})");
        TerraformPlugin.Log.LogInfo($"[leak-debug] waterY={waterY:F2}");

        if (player != null) {
            bool inCavity = IsPlayerInVoxelCavity(pp);
            TerraformPlugin.Log.LogInfo($"[leak-debug] inCavity={inCavity}");
            Heightmap.GetHeight(pp, out float terrAtPlayer);
            TerraformPlugin.Log.LogInfo($"[leak-debug] terrainY@player={terrAtPlayer:F2}");
        }

        var playerHm = Heightmap.FindHeightmap(pp);
        int playerChunkId = playerHm != null ? playerHm.GetInstanceID() : 0;
        TerraformPlugin.Log.LogInfo($"[leak-debug] playerChunk={(playerHm != null ? playerHm.gameObject.name : "null")} id={playerChunkId}");

        foreach (var kv in _chunks) {
            var data = kv.Value;
            if (data?.MeshObject == null || data.Heightmap == null) continue;
            Vector3 co = data.MeshObject.transform.position;
            float dx = (co.x + 32f) - pp.x;
            float dz = (co.z + 32f) - pp.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > 80f && kv.Key != playerChunkId) continue;
            DumpChunkLeakState(kv.Key, data, waterY, dist, kv.Key == playerChunkId);
        }

        TerraformPlugin.Log.LogInfo($"[leak-debug] activeLeaks={_waterLeaks.Count}");
        foreach (var kv in _leakCircleCache) {
            if (kv.Value == null) continue;
            foreach (var c in kv.Value) {
                float dx = c.CenterX - pp.x;
                float dz = c.CenterZ - pp.z;
                float distXZ = Mathf.Sqrt(dx * dx + dz * dz);
                TerraformPlugin.Log.LogInfo($"[leak-debug]   leak chunk={kv.Key} center=({c.CenterX:F1},{c.TopY:F1},{c.CenterZ:F1}) bottomY={c.BottomY:F1} distXZ={distXZ:F1}");
            }
        }

        TerraformPlugin.Log.LogInfo("[leak-debug] ===== REPORT END =====");
    }

    private void DumpChunkLeakState(int chunkId, ChunkRenderData data, float waterY, float distToPlayer, bool isPlayerChunk) {
        Vector3 co = data.MeshObject.transform.position;
        int cx = Mathf.RoundToInt(co.x);
        int cy = Mathf.RoundToInt(co.y);
        int cz = Mathf.RoundToInt(co.z);
        long chunkKey = ChunkKey(cx, cz);

        int pickaxeDigs = 0, surfaceDigs = 0;
        if (_chunkDigHistory.TryGetValue(chunkKey, out var digs)) {
            foreach (var d in digs) {
                if (d.Mode == DigMode.Surface) surfaceDigs++;
                else pickaxeDigs++;
            }
        }

        int holeCount = 0;
        int lakeHoles = 0, nonLakeHoles = 0;
        float halfSize = data.Heightmap.m_width * data.Heightmap.m_scale * 0.5f;
        Vector3 hmPos = data.Heightmap.transform.position;
        float hmCellOriginX = hmPos.x - halfSize;
        float hmCellOriginZ = hmPos.z - halfSize;
        int sampleCount = 0;
        foreach (var hole in HolePunchSystem.GetHoles(data.Heightmap)) {
            holeCount++;
            int wx = Mathf.FloorToInt(hmCellOriginX + hole.x * data.Heightmap.m_scale);
            int wz = Mathf.FloorToInt(hmCellOriginZ + hole.y * data.Heightmap.m_scale);
            Heightmap.GetHeight(new Vector3(wx + 0.5f, 0f, wz + 0.5f), out float terrainY);
            bool isLake = terrainY < waterY;
            if (isLake) lakeHoles++; else nonLakeHoles++;
            if (sampleCount < 5) {
                TerraformPlugin.Log.LogInfo($"[leak-debug]   hole hmCell=({hole.x},{hole.y}) world=({wx},{wz}) terrainY={terrainY:F2} lake={isLake}");
                sampleCount++;
            }
        }

        bool hasLeak = _waterLeaks.ContainsKey(chunkId);
        string tag = isPlayerChunk ? "[PLAYER]" : "";
        TerraformPlugin.Log.LogInfo($"[leak-debug] chunk id={chunkId} {tag} origin=({cx},{cy},{cz}) distXZ={distToPlayer:F1} holes={holeCount} (lake={lakeHoles} land={nonLakeHoles}) digs={pickaxeDigs}px+{surfaceDigs}sf leak={hasLeak}");
    }

    private void ClearLeakDebugMarkers() {
        foreach (var go in _leakDebugMarkers) {
            if (go != null) Destroy(go);
        }
        _leakDebugMarkers.Clear();
    }

    private void BuildLeakDebugMarkers() {
        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        var matLake = MakeDebugMat(Color.cyan);
        var matNonLake = MakeDebugMat(Color.yellow);
        var matLid = MakeDebugMat(Color.magenta);

        foreach (var kv in _chunks) {
            var data = kv.Value;
            if (data?.Heightmap == null) continue;
            float halfSize = data.Heightmap.m_width * data.Heightmap.m_scale * 0.5f;
            Vector3 hmPos = data.Heightmap.transform.position;
            float hmCellOriginX = hmPos.x - halfSize;
            float hmCellOriginZ = hmPos.z - halfSize;

            foreach (var hole in HolePunchSystem.GetHoles(data.Heightmap)) {
                int wx = Mathf.FloorToInt(hmCellOriginX + hole.x * data.Heightmap.m_scale);
                int wz = Mathf.FloorToInt(hmCellOriginZ + hole.y * data.Heightmap.m_scale);
                Heightmap.GetHeight(new Vector3(wx + 0.5f, 0f, wz + 0.5f), out float terrainY);
                bool isLake = terrainY < waterY;
                float markerY = terrainY + 0.5f;
                SpawnDebugMarker(wx + 0.5f, markerY, wz + 0.5f, 0.4f, isLake ? matLake : matNonLake);
            }
        }

        foreach (var kv in _leakCircleCache) {
            if (kv.Value == null) continue;
            foreach (var c in kv.Value) {
                SpawnDebugMarker(c.CenterX, c.TopY, c.CenterZ, 0.8f, matLid);
                SpawnDebugMarker(c.CenterX, c.BottomY, c.CenterZ, 0.8f, matLid);
            }
        }
    }

    private void SpawnDebugMarker(float wx, float wy, float wz, float scale, Material mat) {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "LeakDebugMarker";
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.position = new Vector3(wx, wy, wz);
        go.transform.localScale = new Vector3(scale, scale, scale);
        var rend = go.GetComponent<MeshRenderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;
        _leakDebugMarkers.Add(go);
    }

    private static Material MakeDebugMat(Color color) {
        var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Hidden/Internal-Colored");
        var mat = new Material(shader) { color = color };
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        return mat;
    }
}
