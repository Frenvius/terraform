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
        }

        if (!Input.GetKeyDown(KeyCode.F8)) return;
        if (Player.m_localPlayer == null) return;
        if (GameCamera.instance == null) return;

        var cam = GameCamera.instance.transform;
        int terrainMask = LayerMask.GetMask("terrain", "piece");

        if (!Physics.Raycast(cam.position, cam.forward, out var hit, 50f, terrainMask))
            return;

        var hm = Heightmap.FindHeightmap(hit.point);
        if (hm == null) return;

        float radius = Configs.ModConfig.BrushSize.Value;

        float terrainY;
        Heightmap.GetHeight(hit.point, out terrainY);
        bool belowTerrain = hit.point.y < terrainY - 0.5f;
        bool intersectsExistingSurfaceHole = belowTerrain && DigIntersectsExistingSurfaceHole(hit.point, radius);
        DigMode mode;
        if (!belowTerrain) mode = DigMode.Surface;
        else if (intersectsExistingSurfaceHole) mode = DigMode.OpenHoleDeepen;
        else mode = DigMode.UndergroundTunnel;

        if (mode == DigMode.Surface) {
            float surfaceOuterRadius = radius * OuterRadiusMultiplier;
            float surfacePunchRadius = surfaceOuterRadius + ExtendedPunchBleed;
            PunchHoles(hm, hit.point, surfacePunchRadius, surfaceOuterRadius, SurfacePunchMaxDelta(radius));
        } else {
            float pickaxeOuterRadius = PickaxeRadius + PickaxeNoiseAmplitude;
            PunchHoles(hm, hit.point, pickaxeOuterRadius, pickaxeOuterRadius, pickaxeOuterRadius);
        }
        Dig(hit.point, radius, mode);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
    }

}
