using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float RimLinkWidth           = 1f;
    private const float RimLinkCost            = 8f;
    private const float RimLinkMaxSnapY        = 1f;
    private const float RimLinkTopSampleSlack  = 1f;
    private const float RimLinkFloorProbeUp    = 1f;
    private const float RimLinkFloorProbe      = 64f;
    private const float RimLinkFloorSample     = 2f;
    private const float RimLinkMinFloorNormalY = 0.5f;
    private const int   RimLinkLandingColumns  = 3;
    private const float RimLinkLandingStep     = 0.5f;
    private const float RimLinkMinDrop         = 0.6f;
    private const float RimLinkClearance       = 1f;
    private const float NavDirtyDebounce       = 1f;
    private const float NavDirtyMargin         = 1f;

    private enum RimLinkResult { Added, NoTop, TopSnapY, NoFloor, NoBottom, BottomSnapY, Shallow, Blocked, Invalid }

    private static readonly int[] RimLinkResultCounts = new int[(int)RimLinkResult.Invalid + 1];

    private static readonly Vector2Int[] RimNeighbours = {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    };

    private readonly Dictionary<Vector3Int, List<NavMeshLinkInstance>> _rimLinks = new();
    private readonly Dictionary<Vector2Int, float> _holeSurfaceY = new();

    private bool  _navDirty;
    private Rect  _navDirtyArea;
    private float _navDirtySince;

    public void RebuildRimLinks(Vector3Int tileId, Rect tileArea, NavMeshQueryFilter filter, float agentRadius) {
        RemoveRimLinks(tileId);
        if (_chunks == null || Pathfinding.instance == null) return;

        int blockMask = Pathfinding.instance.m_layers.value;
        List<NavMeshLinkInstance> links = null;
        int candidates = 0;
        System.Array.Clear(RimLinkResultCounts, 0, RimLinkResultCounts.Length);
        _holeSurfaceY.Clear();

        foreach (var data in _chunks.Values) {
            var hm = data?.Heightmap;
            if (hm == null) continue;
            if (!HolePunchSystem.TryGetCollisionHoles(hm, out var holes) || holes.Count == 0) continue;

            float halfSize = hm.m_width * hm.m_scale * 0.5f;
            Vector3 hmPos = hm.transform.position;
            if (hmPos.x + halfSize <= tileArea.xMin || hmPos.x - halfSize >= tileArea.xMax) continue;
            if (hmPos.z + halfSize <= tileArea.yMin || hmPos.z - halfSize >= tileArea.yMax) continue;

            float scale = hm.m_scale;
            foreach (var cell in holes) {
                var cellCenter = new Vector3(
                    hmPos.x - halfSize + (cell.x + 0.5f) * scale, 0f,
                    hmPos.z - halfSize + (cell.y + 0.5f) * scale);
                if (!tileArea.Contains(new Vector2(cellCenter.x, cellCenter.z))) continue;
                if (!TryGetHoleSurfaceY(cellCenter, blockMask, out float cellY)) continue;

                for (int i = 0; i < RimNeighbours.Length; i++) {
                    var rim = new Vector3(
                        cellCenter.x + RimNeighbours[i].x * scale, 0f,
                        cellCenter.z + RimNeighbours[i].y * scale);
                    if (!TryGetLipY(hm, rim, blockMask, out rim.y)) continue;
                    candidates++;
                    if (rim.y - cellY < RimLinkMinDrop) {
                        RimLinkResultCounts[(int)RimLinkResult.Shallow]++;
                        continue;
                    }
                    var result = TryAddRimLink(cellCenter, rim, filter, agentRadius, blockMask, out var link);
                    RimLinkResultCounts[(int)result]++;
                    if (result != RimLinkResult.Added) continue;
                    if (links == null && !_rimLinks.TryGetValue(tileId, out links)) {
                        links = new List<NavMeshLinkInstance>();
                        _rimLinks[tileId] = links;
                    }
                    links.Add(link);
                }
            }
        }

        if (VerboseLogs && candidates > 0) {
            var c = RimLinkResultCounts;
            DebugLog($"[navlinks] tile={tileId} agentTypeID={filter.agentTypeID} edges={candidates} added={c[(int)RimLinkResult.Added]} " +
                     $"noTop={c[(int)RimLinkResult.NoTop]} topSnapY={c[(int)RimLinkResult.TopSnapY]} " +
                     $"noFloor={c[(int)RimLinkResult.NoFloor]} noBottom={c[(int)RimLinkResult.NoBottom]} " +
                     $"bottomSnapY={c[(int)RimLinkResult.BottomSnapY]} shallow={c[(int)RimLinkResult.Shallow]} " +
                     $"blocked={c[(int)RimLinkResult.Blocked]} invalid={c[(int)RimLinkResult.Invalid]}");
        }
    }

    private static RimLinkResult TryAddRimLink(Vector3 holeCell, Vector3 rim, NavMeshQueryFilter filter, float agentRadius, int blockMask, out NavMeshLinkInstance link) {
        link = default;
        if (!NavMesh.SamplePosition(rim, out var top, agentRadius + RimLinkTopSampleSlack, filter)) return RimLinkResult.NoTop;
        if (Mathf.Abs(top.position.y - rim.y) > RimLinkMaxSnapY) return RimLinkResult.TopSnapY;

        var inward = new Vector3(holeCell.x - rim.x, 0f, holeCell.z - rim.z);
        float halfCell = inward.magnitude * 0.5f;
        if (!TryFindLanding(rim, inward.normalized, halfCell, blockMask, out var landing)) return RimLinkResult.NoFloor;
        if (!NavMesh.SamplePosition(landing, out var bottom, RimLinkFloorSample, filter)) return RimLinkResult.NoBottom;
        if (Mathf.Abs(bottom.position.y - landing.y) > RimLinkMaxSnapY) return RimLinkResult.BottomSnapY;

        if (top.position.y - bottom.position.y < RimLinkMinDrop) return RimLinkResult.Shallow;
        if (!HasDropClearance(top.position, landing, blockMask)) return RimLinkResult.Blocked;

        link = NavMesh.AddLink(new NavMeshLinkData {
            startPosition = top.position,
            endPosition   = bottom.position,
            width         = RimLinkWidth,
            costModifier  = RimLinkCost,
            bidirectional = false,
            agentTypeID   = filter.agentTypeID,
            area          = 2
        });
        return NavMesh.IsLinkValid(link) ? RimLinkResult.Added : RimLinkResult.Invalid;
    }

    private static bool TryFindLanding(Vector3 rim, Vector3 inward, float halfCell, int blockMask, out Vector3 landing) {
        for (int i = 1; i <= RimLinkLandingColumns; i++) {
            var origin = rim + inward * (halfCell + i * RimLinkLandingStep);
            origin.y = rim.y + RimLinkFloorProbeUp;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, RimLinkFloorProbe, blockMask, QueryTriggerInteraction.Ignore)) continue;
            if (!VoxelGroundProbe.IsVoxelHit(hit) || hit.normal.y <= RimLinkMinFloorNormalY) continue;
            landing = hit.point;
            return true;
        }
        landing = default;
        return false;
    }

    private static bool HasDropClearance(Vector3 top, Vector3 landing, int blockMask) {
        var above       = top + Vector3.up * RimLinkClearance;
        var overLanding = new Vector3(landing.x, above.y, landing.z);
        return !Physics.Linecast(above, overLanding, blockMask, QueryTriggerInteraction.Ignore);
    }

    private const float NavSeaLevel          = 30f;
    private const float NavSeaLevelTolerance = 1f;

    private static bool OpensToSurface(DigMode mode) =>
        mode == DigMode.Surface || mode == DigMode.OpenHoleDeepen;

    public bool IsTileOverCaveBelowSeaLevel(Vector3 tileCenter, float tileSize) {
        float half = tileSize * 0.5f;
        foreach (var records in _chunkDigHistory.Values) {
            for (int i = 0; i < records.Count; i++) {
                var r = records[i];
                if (r.WorldPos.y > NavSeaLevel - NavSeaLevelTolerance) continue;
                if (!OpensToSurface(r.Mode)) continue;
                float dx = Mathf.Abs(r.WorldPos.x - tileCenter.x) - half;
                float dz = Mathf.Abs(r.WorldPos.z - tileCenter.z) - half;
                if (dx < r.Radius && dz < r.Radius) return true;
            }
        }
        return false;
    }

    public bool IsTileOverCarvingDig(Vector3 tileCenter, float tileSize) {
        float half = tileSize * 0.5f;
        foreach (var records in _chunkDigHistory.Values) {
            for (int i = 0; i < records.Count; i++) {
                var r = records[i];
                if (!IsCarvingMode(r.Mode)) continue;
                float reach = r.Radius * OuterRadiusMultiplier + ExtendedPunchBleed;
                if (Mathf.Abs(r.WorldPos.x - tileCenter.x) - half < reach
                    && Mathf.Abs(r.WorldPos.z - tileCenter.z) - half < reach) return true;
            }
        }
        return false;
    }

    private bool TryGetLipY(Heightmap hm, Vector3 cellCenter, int blockMask, out float y) {
        y = 0f;
        var target = hm;
        if (!TryWorldToHeightmapCell(target, cellCenter, out int x, out int z)) {
            target = Heightmap.FindHeightmap(cellCenter);
            if (target == null || target.gameObject.name.Contains("lodMesh")) return false;
            if (!TryWorldToHeightmapCell(target, cellCenter, out x, out z)) return false;
        }
        if (HolePunchSystem.IsCollisionHole(target, x, z)) return TryGetHoleSurfaceY(cellCenter, blockMask, out y);
        return Heightmap.GetHeight(cellCenter, out y);
    }

    private bool TryGetHoleSurfaceY(Vector3 cellCenter, int blockMask, out float y) {
        var key = new Vector2Int(Mathf.FloorToInt(cellCenter.x), Mathf.FloorToInt(cellCenter.z));
        if (_holeSurfaceY.TryGetValue(key, out y)) return !float.IsNaN(y);

        y = float.NaN;
        if (Heightmap.GetHeight(cellCenter, out float terrainY)) {
            var origin = new Vector3(cellCenter.x, SurfaceHeight(cellCenter, terrainY) + RimLinkFloorProbeUp, cellCenter.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, RimLinkFloorProbe, blockMask, QueryTriggerInteraction.Ignore)
                && VoxelGroundProbe.IsVoxelHit(hit) && hit.normal.y > RimLinkMinFloorNormalY)
                y = hit.point.y;
        }
        _holeSurfaceY[key] = y;
        return !float.IsNaN(y);
    }

    public void RemoveRimLinks(Vector3Int tileId) {
        if (!_rimLinks.TryGetValue(tileId, out var list)) return;
        for (int i = 0; i < list.Count; i++)
            if (NavMesh.IsLinkValid(list[i])) NavMesh.RemoveLink(list[i]);
        list.Clear();
    }

    private void RemoveAllRimLinks() {
        foreach (var list in _rimLinks.Values) {
            for (int i = 0; i < list.Count; i++)
                if (NavMesh.IsLinkValid(list[i])) NavMesh.RemoveLink(list[i]);
            list.Clear();
        }
        _rimLinks.Clear();
    }

    private void MarkNavDirty(float minX, float minZ, float maxX, float maxZ) {
        var area = Rect.MinMaxRect(minX - NavDirtyMargin, minZ - NavDirtyMargin, maxX + NavDirtyMargin, maxZ + NavDirtyMargin);
        _navDirtyArea = _navDirty
            ? Rect.MinMaxRect(
                Mathf.Min(_navDirtyArea.xMin, area.xMin), Mathf.Min(_navDirtyArea.yMin, area.yMin),
                Mathf.Max(_navDirtyArea.xMax, area.xMax), Mathf.Max(_navDirtyArea.yMax, area.yMax))
            : area;
        _navDirty      = true;
        _navDirtySince = Time.time;
    }

    private void FlushNavDirty() {
        if (!_navDirty || Time.time - _navDirtySince < NavDirtyDebounce) return;
        _navDirty = false;
        if (Pathfinding.instance == null) return;
        int marked = PathfindingTiles.MarkDirty(Pathfinding.instance, _navDirtyArea);
        if (VerboseLogs) DebugLog($"[navdirty] area=({_navDirtyArea.xMin:F0},{_navDirtyArea.yMin:F0})..({_navDirtyArea.xMax:F0},{_navDirtyArea.yMax:F0}) tiles={marked}");
    }
}
