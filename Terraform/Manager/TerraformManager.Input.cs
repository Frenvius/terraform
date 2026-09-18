using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private void HandleInput() {
        bool shift = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
        bool alt   = Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt);
        bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (VerboseLogs) {
            if (Input.GetKeyDown(KeyCode.F10)) {
                if      (alt)   MeasureNavMeshFragmentation();
                else if (shift) ToggleNavOverlay();
                else            ProbeNavMeshAtPlayer();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F11)) {
                if      (alt)   ToggleDebugNoNormalMap();
                else if (shift) ToggleDebugMagenta();
                else            InspectTriangleAtCrosshair();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F12)) {
                if (ctrl) {
                    ToggleLeakDebug();
                } else if (alt) {
                    ToggleHideWater();
                } else if (shift) {
                    ToggleHideHeightmaps();
                } else {
                    _showDebugGrid = !_showDebugGrid;
                    if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
                    else                { ClearDebugGrids();   ClearDebugChunkWalls();   }
                    TerraformPlugin.Log.LogInfo($"Debug grid: {_showDebugGrid}");
                }
                return;
            }
        }

        if (shift || alt || ctrl) return;

        if (Input.GetKeyDown(KeyCode.F1)) {
            TryRaycastForAction(action: PerformSmoothAt);
            return;
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

    private bool _clientDigWarned;

    public bool DiggingBlockedAsClient() {
        if (ZNet.instance == null || ZNet.instance.IsServer()) return false;
        if (!_clientDigWarned) {
            _clientDigWarned = true;
            TerraformPlugin.Log.LogWarning("Voxel digging is disabled while connected to a server as a client.");
        }
        return true;
    }

    private bool CanDigAt(Vector3 worldPos) {
        if (DiggingBlockedAsClient()) return false;
        float radius = Configs.ModConfig.BrushSize?.Value ?? 1.5f;
        if (!PrivateArea.CheckAccess(worldPos, radius)) return false;
        return !Location.IsInsideNoBuildLocation(worldPos);
    }

    public bool PerformRaiseAt(Vector3 worldPos) {
        return PerformRaiseAt(worldPos, Vector3.up);
    }

    private void PerformRaiseAt(RaycastHit hit) {
        PerformRaiseAt(hit.point, hit.normal);
    }

    private bool PerformRaiseAt(Vector3 worldPos, Vector3 surfaceNormal) {
        if (!CanDigAt(worldPos)) return false;
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;

        float radius = Configs.ModConfig.BrushSize.Value * RaiseRadiusMultiplier;
        DeriveModePunchParams(DigMode.Raise, radius, out float punchRadius, out float maxRaiseHeight);
        float outerRadius = radius * OuterRadiusMultiplier;

        PunchHoles(hm, worldPos, punchRadius, outerRadius, maxRaiseHeight, affectsCollision: false, doPaint: false);
        Dig(worldPos, radius, DigMode.Raise, surfaceNormal);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
        return true;
    }

    public bool PerformDigAt(Vector3 worldPos) {
        return PerformDigAt(worldPos, null);
    }

    private void PerformDigAt(RaycastHit hit) {
        PerformDigAt(hit.point, hit.collider);
    }

    private bool PerformDigAt(Vector3 worldPos, Collider hitCollider) {
        if (!CanDigAt(worldPos)) return false;
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;

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
            PunchHoles(hm, digPos, surfacePunchRadius, surfaceOuterRadius, SurfacePunchMaxDelta(radius), doPaint: false);
        } else {
            float pickaxeOuterRadius = PickaxeRadius + PickaxeNoiseAmplitude;
            float pickaxeReachY      = pickaxeOuterRadius * PickaxeHeightMultiplier;
            PunchHoles(hm, digPos, pickaxeOuterRadius, pickaxeOuterRadius, pickaxeReachY);
        }
        Dig(digPos, radius, mode);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
        return true;
    }

    public bool PerformSmoothAt(Vector3 worldPos) {
        if (!CanDigAt(worldPos)) return false;
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;

        var player = Player.m_localPlayer;
        if (player == null) return false;

        Vector3 target = new Vector3(worldPos.x, player.transform.position.y, worldPos.z);
        Dig(target, SmoothRadius, DigMode.Smooth);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
        return true;
    }

    private void PerformSmoothAt(RaycastHit hit) {
        PerformSmoothAt(hit.point);
    }
}
