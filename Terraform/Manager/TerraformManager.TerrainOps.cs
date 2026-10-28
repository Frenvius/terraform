using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    public struct TerrainOpVoxelPass {
        public bool Level;
        public bool Smooth;
        public Vector3 Target;
        public Vector3 SurfaceNormal;
        public bool Any => Level || Smooth;
    }

    private const float PlacementSurfaceMatchXZ = 0.05f;
    private const float CeilingProbeHeight      = 64f;
    private const float CeilingProbeClearance   = 0.3f;
    private const float HoeRaiseRadiusScale     = 0.6f;
    private const float HoeRaiseHeightScale     = 0.5f;

    private readonly RaycastHit[] _ceilingHits = new RaycastHit[16];
    private Vector3 _placementSurfacePoint;
    private Vector3 _placementSurfaceNormal = Vector3.up;
    private bool    _hasPlacementSurface;

    public static bool IsVoxelRaise(TerrainOp.Settings settings) =>
        settings.m_raise && settings.m_raiseDelta > 0f;

    public void RecordPlacementSurface(Vector3 point, Vector3 normal) {
        _placementSurfacePoint  = point;
        _placementSurfaceNormal = normal;
        _hasPlacementSurface    = true;
    }

    public TerrainOpVoxelPass BeginTerrainOp(Vector3 opPos, TerrainOp.Settings settings) {
        if (DiggingBlockedAsClient()) return default;

        if (IsVoxelRaise(settings)) {
            RaiseFromTerrainOp(opPos, settings);
            settings.m_raise = false;
        }

        if (TryGetCeilingPlacement(opPos, out Vector3 ceilingPoint, out Vector3 ceilingNormal))
            return BeginCeilingOp(settings, ceilingPoint, ceilingNormal);

        if (settings.m_paintCleared && IsUnderTerrainCeiling(opPos)) {
            PaintUnderground(opPos, settings.m_paintRadius, settings.m_paintType);
            settings.m_paintCleared = false;
        }

        Vector3 target = LevelTarget(opPos, settings);
        var pass = new TerrainOpVoxelPass {
            Level         = settings.m_level  && HasVoxelSurfaceNear(target, settings.m_levelRadius),
            Smooth        = settings.m_smooth && HasVoxelSurfaceNear(target, settings.m_smoothRadius),
            Target        = target,
            SurfaceNormal = Vector3.up,
        };

        if (pass.Any && IsUnderTerrainCeiling(target)) {
            settings.m_level  = false;
            settings.m_smooth = false;
        }
        return pass;
    }

    private static TerrainOpVoxelPass BeginCeilingOp(TerrainOp.Settings settings, Vector3 point, Vector3 normal) {
        var pass = new TerrainOpVoxelPass {
            Level = settings.m_level, Smooth = settings.m_smooth, Target = point, SurfaceNormal = normal,
        };
        settings.m_level        = false;
        settings.m_smooth       = false;
        settings.m_paintCleared = false;
        return pass;
    }

    public void FinishTerrainOp(TerrainOp.Settings settings, TerrainOpVoxelPass pass) {
        if (pass.Level) {
            Dig(new DigRecord {
                WorldPos = pass.Target, Normal = pass.SurfaceNormal, Radius = settings.m_levelRadius,
                Mode = DigMode.Level, Square = settings.m_square
            });
        }
        if (pass.Smooth) Dig(pass.Target, settings.m_smoothRadius, DigMode.Smooth, pass.SurfaceNormal);

        if (_showDebugGrid) { RefreshDebugGrids(); RefreshDebugChunkWalls(); }
    }

    private static Vector3 LevelTarget(Vector3 opPos, TerrainOp.Settings settings) =>
        opPos + Vector3.up * settings.m_levelOffset;

    private bool PlacementSurfaceMatches(Vector3 opPos) =>
        _hasPlacementSurface && HorizontalDistance(_placementSurfacePoint, opPos) < PlacementSurfaceMatchXZ;

    public bool TryGetPlacementSurface(Vector3 opPos, out Vector3 point, out Vector3 normal) {
        point  = _placementSurfacePoint;
        normal = _placementSurfaceNormal;
        return PlacementSurfaceMatches(opPos);
    }

    public bool TryGetCeilingPlacement(Vector3 opPos, out Vector3 point, out Vector3 normal) {
        return TryGetPlacementSurface(opPos, out point, out normal) && IsCeilingNormal(normal);
    }

    private void RaiseFromTerrainOp(Vector3 opPos, TerrainOp.Settings settings) {
        Vector3 point  = opPos;
        Vector3 normal = Vector3.up;
        if (PlacementSurfaceMatches(opPos)) {
            point  = _placementSurfacePoint;
            normal = _placementSurfaceNormal;
        } else if (Heightmap.GetHeight(opPos, out float groundY)) {
            point.y = groundY;
        }
        if (VerboseLogs) DebugLog($"[hoe-raise] radius={settings.m_raiseRadius:F2} delta={settings.m_raiseDelta:F2} power={settings.m_raisePower:F2}");
        PerformRaiseAt(point, normal,
            settings.m_raiseRadius * HoeRaiseRadiusScale / OuterRadiusMultiplier,
            settings.m_raiseDelta * HoeRaiseHeightScale);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private bool HasVoxelSurfaceNear(Vector3 worldPos, float radius) {
        int minX = Mathf.FloorToInt(worldPos.x - radius);
        int maxX = Mathf.CeilToInt (worldPos.x + radius);
        int minZ = Mathf.FloorToInt(worldPos.z - radius);
        int maxZ = Mathf.CeilToInt (worldPos.z + radius);

        foreach (var data in _chunks.Values) {
            if (data?.Renderer == null || !data.Renderer.enabled) continue;
            foreach (var footprint in data.RenderFootprints) {
                if (footprint.MaxX >= minX && footprint.MinX <= maxX &&
                    footprint.MaxZ >= minZ && footprint.MinZ <= maxZ) return true;
            }
        }
        return false;
    }

    private bool IsUnderTerrainCeiling(Vector3 target) {
        if (!Heightmap.GetHeight(target, out float terrainY)) return false;
        if (target.y >= terrainY - UndergroundEnterMarginY) return false;

        Vector3 origin = target + Vector3.up * CeilingProbeHeight;
        float distance = CeilingProbeHeight - CeilingProbeClearance;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, _ceilingHits, distance, VoxelGroundProbe.TerrainAndPieceMask);
        for (int i = 0; i < count; i++) {
            var hit = _ceilingHits[i];
            if (hit.collider.GetComponent<Heightmap>() != null || VoxelGroundProbe.IsVoxelHit(hit)) return true;
        }
        return false;
    }
}
