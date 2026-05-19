using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private void HandleInput() {
        if (VerboseLogs) {
            if (Input.GetKeyDown(KeyCode.F9)) {
                _showDebugGrid = !_showDebugGrid;
                if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
                else                { ClearDebugGrids();   ClearDebugChunkWalls();   }
                TerraformPlugin.Log.LogInfo($"Debug grid: {_showDebugGrid}");
            }

            if (Input.GetKeyDown(KeyCode.F10)) {
                ToggleDebugNoNormalMap();
            }

            if (Input.GetKeyDown(KeyCode.F7)) {
                ToggleHideHeightmaps();
            }

            if (Input.GetKeyDown(KeyCode.F11)) {
                ToggleDebugMagenta();
            }

            if (Input.GetKeyDown(KeyCode.F12)) {
                InspectTriangleAtCrosshair();
            }

            if (Input.GetKeyDown(KeyCode.F6)) {
                ToggleLeakDebug();
            }

            if (Input.GetKeyDown(KeyCode.F8)) {
                ToggleHideWater();
            }
        }

        if (Input.GetKeyDown(KeyCode.F3)) {
            TryRaycastForAction(action: PerformRaiseAt);
            return;
        }

        if (Input.GetKeyDown(KeyCode.F4)) {
            TryRaycastForAction(action: PerformDigAt);
        }
    }

    private static void TryRaycastForAction(System.Action<RaycastHit> action) {
        if (Player.m_localPlayer == null) return;
        if (GameCamera.instance == null) return;

        var cam = GameCamera.instance.transform;
        int terrainMask = VoxelGroundProbe.TerrainAndPieceMask;

        if (!Physics.Raycast(cam.position, cam.forward, out var hit, 50f, terrainMask))
            return;

        action(hit);
    }

    public void PerformRaiseAt(Vector3 worldPos) {
        PerformRaiseAt(worldPos, Vector3.up);
    }

    private void PerformRaiseAt(RaycastHit hit) {
        PerformRaiseAt(hit.point, hit.normal);
    }

    private void PerformRaiseAt(Vector3 worldPos, Vector3 surfaceNormal) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return;

        float radius = Configs.ModConfig.BrushSize.Value * RaiseRadiusMultiplier;
        DeriveModePunchParams(DigMode.Raise, radius, out float punchRadius, out float maxRaiseHeight);
        float outerRadius = radius * OuterRadiusMultiplier;

        PunchHoles(hm, worldPos, punchRadius, outerRadius, maxRaiseHeight);
        Dig(worldPos, radius, DigMode.Raise, surfaceNormal);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
    }

    public void PerformDigAt(Vector3 worldPos) {
        PerformDigAt(worldPos, null);
    }

    private void PerformDigAt(RaycastHit hit) {
        PerformDigAt(hit.point, hit.collider);
    }

    private void PerformDigAt(Vector3 worldPos, Collider hitCollider) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return;

        float radius = Configs.ModConfig.BrushSize.Value;

        Heightmap.GetHeight(worldPos, out float terrainY);
        bool belowTerrain          = worldPos.y < terrainY - 0.5f;
        bool overlapsExistingHole  = DigIntersectsExistingSurfaceHole(worldPos, radius);
        DigMode mode;
        if (overlapsExistingHole) mode = DigMode.OpenHoleDeepen;
        else if (belowTerrain)    mode = DigMode.UndergroundTunnel;
        else                       mode = DigMode.Surface;

        Vector3 digPos = worldPos;
        if (mode == DigMode.Surface && hitCollider != null && hitCollider.gameObject.layer == LayerMask.NameToLayer("piece")) {
            digPos.y = terrainY;
        }

        if (mode == DigMode.Surface) {
            float surfaceOuterRadius = radius * OuterRadiusMultiplier;
            float surfacePunchRadius = surfaceOuterRadius + ExtendedPunchBleed;
            PunchHoles(hm, digPos, surfacePunchRadius, surfaceOuterRadius, SurfacePunchMaxDelta(radius));
        } else {
            float pickaxeOuterRadius = PickaxeRadius + PickaxeNoiseAmplitude;
            float pickaxeReachY      = pickaxeOuterRadius * PickaxeHeightMultiplier;
            PunchHoles(hm, digPos, pickaxeOuterRadius, pickaxeOuterRadius, pickaxeReachY);
        }
        Dig(digPos, radius, mode);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
    }

}
