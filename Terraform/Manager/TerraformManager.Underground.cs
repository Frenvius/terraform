using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private readonly System.Collections.Generic.HashSet<MeshCollider> _ignoredHmCols = new();
    private CapsuleCollider _ignoredPlayerCol;

    private void UpdateUndergroundCollision() {
        var player = Player.m_localPlayer;
        if (player == null) return;

        var playerCol = player.GetComponent<CapsuleCollider>();
        if (playerCol == null) return;

        Vector3 playerPos = player.transform.position;
        var currentHm = Heightmap.FindHeightmap(playerPos);
        if (currentHm == null) return;

        float terrainY;
        if (!Heightmap.GetHeight(playerPos, out terrainY)) return;

        bool playerBelowTerrain = playerPos.y < terrainY - UndergroundEnterMarginY;
        bool hasVoxelMesh =
            _chunks.TryGetValue(currentHm.GetInstanceID(), out var data)
            && data.Renderer != null
            && data.Renderer.enabled;
        bool shouldIgnore = playerBelowTerrain && hasVoxelMesh;

        if (shouldIgnore) {
            var bounds = playerCol.bounds;
            foreach (var hm in FindObjectsByType<Heightmap>(FindObjectsSortMode.None)) {
                if (hm.gameObject.name.Contains("lodMesh")) continue;
                var hmCol = hm.GetComponent<MeshCollider>();
                if (hmCol == null) continue;
                if (!hmCol.bounds.Intersects(bounds)) continue;

                if (_ignoredHmCols.Add(hmCol)) {
                    Physics.IgnoreCollision(playerCol, hmCol, true);
                    DebugLog(
                        $"[ground] IGNORE +hm={hm.name} playerY={playerPos.y:F2} terrainY={terrainY:F2} dY={(playerPos.y - terrainY):F2}");
                }
            }
            _ignoredPlayerCol = playerCol;
        } else if (_ignoredHmCols.Count > 0) {
            foreach (var hmCol in _ignoredHmCols) {
                if (hmCol != null && _ignoredPlayerCol != null)
                    Physics.IgnoreCollision(_ignoredPlayerCol, hmCol, false);
            }
            DebugLog(
                $"[ground] RESTORE all ({_ignoredHmCols.Count}) playerY={playerPos.y:F2} terrainY={terrainY:F2}");
            _ignoredHmCols.Clear();
            _ignoredPlayerCol = null;
        }
    }
}
