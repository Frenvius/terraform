using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    public struct TerrainOpVoxelPass {
        public bool Level;
        public bool Smooth;
        public Vector3 Target;
        public Vector3 SurfaceNormal;
        public bool Any => Level || Smooth;
    }

    private const float PlacementSurfaceMatchXZ   = 0.05f;
    private const float CeilingProbeHeight        = 64f;
    private const float CeilingProbeClearance     = 0.3f;
    private const float HoeRaiseRadiusScale       = 0.75f;
    private const float HoeRaiseHeightScale       = 0.6f;

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
        if (DiggingUnavailable()) return default;

        if (IsVoxelRaise(settings)) {
            if (OpFitsVerticalWindow(opPos, settings.m_raiseRadius + settings.m_raiseDelta)) {
                RaiseFromTerrainOp(opPos, settings);
                settings.m_raise = false;
            } else {
                DebugLog("[route] window: raise left to vanilla");
            }
        }

        bool surfaceAligned = TryGetSurfaceAlignedPlacement(opPos, out Vector3 alignedPoint, out Vector3 alignedNormal);
        if (surfaceAligned && !OpFitsVerticalWindow(alignedPoint, HeightOpRadius(settings))) {
            DebugLog("[route] window: surface-aligned op left to vanilla");
            return default;
        }
        if (surfaceAligned) return BeginSurfaceAlignedOp(settings, alignedPoint, alignedNormal);

        if (settings.m_paintCleared && IsUnderTerrainCeiling(opPos)) {
            PaintUnderground(opPos, settings.m_paintRadius, settings.m_paintType);
            settings.m_paintCleared = false;
        }

        if (!settings.m_level && !settings.m_smooth) return default;
        Vector3 target = LevelTarget(opPos, settings);
        if (!OpFitsVerticalWindow(target, HeightOpRadius(settings))) {
            DebugLog("[route] window: level / smooth left to vanilla");
            return default;
        }

        var pass = new TerrainOpVoxelPass {
            Level = settings.m_level, Smooth = settings.m_smooth, Target = target, SurfaceNormal = Vector3.up,
        };
        settings.m_level  = false;
        settings.m_smooth = false;
        return pass;
    }

    private static float HeightOpRadius(TerrainOp.Settings settings) => Mathf.Max(
        settings.m_level  ? settings.m_levelRadius  : 0f,
        settings.m_smooth ? settings.m_smoothRadius : 0f);

    private static TerrainOpVoxelPass BeginSurfaceAlignedOp(TerrainOp.Settings settings, Vector3 point, Vector3 normal) {
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

    public bool TryGetSurfaceAlignedPlacement(Vector3 opPos, out Vector3 point, out Vector3 normal) {
        return TryGetPlacementSurface(opPos, out point, out normal) && normal.y < GroundOpNormalMinY;
    }

    private void RaiseFromTerrainOp(Vector3 opPos, TerrainOp.Settings settings) {
        Vector3 point  = opPos;
        Vector3 normal = Vector3.up;
        if (PlacementSurfaceMatches(opPos)) {
            point  = _placementSurfacePoint;
            normal = _placementSurfaceNormal;
        } else if (OriginalHeight(opPos, out float groundY)) {
            point.y = SurfaceHeight(opPos, groundY);
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

    private bool IsUnderTerrainCeiling(Vector3 target) {
        if (!OriginalHeight(target, out float terrainY) || target.y >= terrainY - UndergroundEnterMarginY) return false;

        Vector3 origin = target + Vector3.up * CeilingProbeHeight;
        float distance = CeilingProbeHeight - CeilingProbeClearance;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, _ceilingHits, distance, VoxelGroundProbe.TerrainAndPieceMask);
        for (int i = 0; i < count; i++) {
            var hit = _ceilingHits[i];
            if (VoxelGroundProbe.IsVoxelHit(hit) || hit.collider.GetComponent<Heightmap>() != null) return true;
        }
        return false;
    }
}
