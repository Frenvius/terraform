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

        if (!Input.GetKeyDown(KeyCode.F8)) return;
        if (Input.GetKeyDown(KeyCode.F4)) {
        if (Player.m_localPlayer == null) return;
        if (GameCamera.instance == null) return;

        var cam = GameCamera.instance.transform;
        int terrainMask = LayerMask.GetMask("terrain", "piece");

        if (!Physics.Raycast(cam.position, cam.forward, out var hit, 50f, terrainMask))
            return;

        PerformDigAt(hit.point);
    }

    public void PerformDigAt(Vector3 worldPos) {
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

        if (mode == DigMode.Surface) {
            float surfaceOuterRadius = radius * OuterRadiusMultiplier;
            float surfacePunchRadius = surfaceOuterRadius + ExtendedPunchBleed;
            PunchHoles(hm, worldPos, surfacePunchRadius, surfaceOuterRadius, SurfacePunchMaxDelta(radius));
        } else {
            float pickaxeOuterRadius = PickaxeRadius + PickaxeNoiseAmplitude;
            float pickaxeReachY      = pickaxeOuterRadius * PickaxeHeightMultiplier;
            PunchHoles(hm, worldPos, pickaxeOuterRadius, pickaxeOuterRadius, pickaxeReachY);
        }
        Dig(worldPos, radius, mode);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
    }

}
