using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private void HandleInput() {
        bool shift = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
        bool alt   = Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt);
        bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (!VerboseLogs) return;

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

    private const float RaiseLiftMaxRise   = 4f;
    private const float RaiseLiftClearance = 0.05f;

    public bool PerformRaiseAt(Vector3 worldPos) {
        return PerformRaiseAt(worldPos, Vector3.up);
    }

    private void PerformRaiseAt(RaycastHit hit) {
        PerformRaiseAt(hit.point, hit.normal);
    }

    private bool PerformRaiseAt(Vector3 worldPos, Vector3 surfaceNormal) {
        return PerformRaiseAt(worldPos, surfaceNormal, Configs.ModConfig.BrushSize.Value * RaiseRadiusMultiplier, 0f);
    }

    private bool PerformRaiseAt(Vector3 worldPos, Vector3 surfaceNormal, float radius, float height) {
        if (!CanDigAt(worldPos)) return false;
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;

        Dig(new DigRecord {
            WorldPos = worldPos, Normal = surfaceNormal.normalized, Radius = radius, Mode = DigMode.Raise, Height = height
        });
        LiftCharactersOutOfRaise(worldPos, radius * OuterRadiusMultiplier);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
        return true;
    }

    private void LiftCharactersOutOfRaise(Vector3 center, float footprintRadius) {
        foreach (var character in Character.GetAllCharacters()) {
            if (character == null) continue;
            Vector3 feet = character.transform.position;
            if (HorizontalDistance(feet, center) > footprintRadius + character.GetRadius()) continue;
            if (!TryFindAirAbove(feet, RaiseLiftMaxRise, out float airY)) continue;

            Vector3 lifted = new Vector3(feet.x, airY + RaiseLiftClearance, feet.z);
            character.transform.position = lifted;
            var body = character.GetComponent<Rigidbody>();
            if (body != null) body.position = lifted;
        }
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

        OriginalHeight(worldPos, out float terrainY);
        float surfaceY = SurfaceHeight(worldPos, terrainY);
        DigMode mode;
        if (DigTouchesCrater(worldPos, radius))          mode = DigMode.OpenHoleDeepen;
        else if (worldPos.y < surfaceY - CraterMinDepth) mode = DigMode.UndergroundTunnel;
        else                                             mode = DigMode.Surface;

        Vector3 digPos = worldPos;
        if (mode == DigMode.Surface && hitCollider != null && hitCollider.gameObject.layer == LayerMask.NameToLayer("piece")) {
            digPos.y = surfaceY;
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
