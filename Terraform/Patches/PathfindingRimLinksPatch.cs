using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Terraform.Patches;

internal static class PathfindingTiles {
    private const float DirtyBuildTime = -1000f;

    public static readonly Type TileType = AccessTools.Inner(typeof(Pathfinding), "NavMeshTile");
    private static readonly Type SettingsType = AccessTools.Inner(typeof(Pathfinding), "AgentSettings");

    private static readonly FieldInfo TilesField         = AccessTools.Field(typeof(Pathfinding), "m_tiles");
    private static readonly FieldInfo AgentSettingsField = AccessTools.Field(typeof(Pathfinding), "m_agentSettings");
    private static readonly FieldInfo TileIdField        = AccessTools.Field(TileType, "m_tile");
    private static readonly FieldInfo CenterField        = AccessTools.Field(TileType, "m_center");
    private static readonly FieldInfo BuildTimeField     = AccessTools.Field(TileType, "m_buildTime");
    private static readonly FieldInfo BuildField         = AccessTools.Field(SettingsType, "m_build");
    private static readonly FieldInfo CanWalkField       = AccessTools.Field(SettingsType, "m_canWalk");
    private static readonly FieldInfo AreaMaskField      = AccessTools.Field(SettingsType, "m_areaMask");

    private static readonly object DirtyBuildTimeBoxed = DirtyBuildTime;

    public static Vector3Int IdOf(object tile) => (Vector3Int)TileIdField.GetValue(tile);

    public static Vector3 CenterOf(object tile) => (Vector3)CenterField.GetValue(tile);

    public static Rect AreaOf(object tile, float tileSize) {
        Vector3 c = CenterOf(tile);
        float half = tileSize * 0.5f;
        return new Rect(c.x - half, c.z - half, tileSize, tileSize);
    }

    public static bool TryGetWalkingAgent(Pathfinding pf, int agentType, out NavMeshQueryFilter filter, out float radius) {
        filter = default;
        radius = 0f;
        var list = (IList)AgentSettingsField.GetValue(pf);
        if (agentType < 0 || agentType >= list.Count) return false;
        var settings = list[agentType];
        if (settings == null || !(bool)CanWalkField.GetValue(settings)) return false;
        var build = (NavMeshBuildSettings)BuildField.GetValue(settings);
        filter = new NavMeshQueryFilter {
            agentTypeID = build.agentTypeID,
            areaMask    = (int)AreaMaskField.GetValue(settings)
        };
        radius = build.agentRadius;
        return true;
    }

    public static bool TrySetMinRegionArea(Pathfinding pf, int agentType, float value, out float previous) {
        previous = 0f;
        var list = (IList)AgentSettingsField.GetValue(pf);
        if (agentType < 0 || agentType >= list.Count) return false;
        var settings = list[agentType];
        if (settings == null) return false;
        var build = (NavMeshBuildSettings)BuildField.GetValue(settings);
        previous = build.minRegionArea;
        build.minRegionArea = value;
        BuildField.SetValue(settings, build);
        return true;
    }

    public static int MarkDirty(Pathfinding pf, Rect area) {
        var tiles = (IDictionary)TilesField.GetValue(pf);
        float half = pf.m_tileSize * 0.5f;
        int marked = 0;
        foreach (var tile in tiles.Values) {
            Vector3 c = CenterOf(tile);
            if (c.x + half <= area.xMin || c.x - half >= area.xMax) continue;
            if (c.z + half <= area.yMin || c.z - half >= area.yMax) continue;
            BuildTimeField.SetValue(tile, DirtyBuildTimeBoxed);
            marked++;
        }
        return marked;
    }
}

[HarmonyPatch(typeof(Pathfinding), "RebuildLinks")]
public static class Pathfinding_RebuildLinks_RimLinks_Patch {
    static void Postfix(Pathfinding __instance, object tile) {
        var manager = TerraformManager.Instance;
        if (manager == null || tile == null) return;
        try {
            Vector3Int id = PathfindingTiles.IdOf(tile);
            if (!PathfindingTiles.TryGetWalkingAgent(__instance, id.z, out var filter, out float radius)) {
                manager.RemoveRimLinks(id);
                return;
            }
            manager.RebuildRimLinks(id, PathfindingTiles.AreaOf(tile, __instance.m_tileSize), filter, radius);
        } catch (Exception e) {
            TerraformPlugin.Log.LogWarning($"[navlinks] RebuildLinks postfix failed: {e.Message}");
        }
    }
}

[HarmonyPatch]
public static class Pathfinding_ClearLinks_RimLinks_Patch {
    static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Pathfinding), "ClearLinks", new[] { PathfindingTiles.TileType });

    static void Postfix(object tile) {
        var manager = TerraformManager.Instance;
        if (manager == null || tile == null) return;
        try {
            manager.RemoveRimLinks(PathfindingTiles.IdOf(tile));
        } catch (Exception e) {
            TerraformPlugin.Log.LogWarning($"[navlinks] ClearLinks postfix failed: {e.Message}");
        }
    }
}
