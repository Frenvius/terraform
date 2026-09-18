using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Pathfinding), "BuildTile")]
public static class Pathfinding_BuildTile_SkipWaterOverCave_Patch {
    private const float DigTileMinRegionArea = 0f;

    private static LayerMask _stashedWaterLayers;
    private static bool _didStash;

    private static int   _stashedRegionAgent;
    private static float _stashedMinRegionArea;
    private static bool  _didStashRegion;

    static void Prefix(Pathfinding __instance, object tile) {
        _didStash = false;
        _didStashRegion = false;
        if (TerraformManager.Instance == null || tile == null) return;

        Vector3 center = PathfindingTiles.CenterOf(tile);

        if (TerraformManager.Instance.IsTileOverCaveBelowSeaLevel(center, __instance.m_tileSize)) {
            _stashedWaterLayers = __instance.m_waterLayers;
            __instance.m_waterLayers = 0;
            _didStash = true;
        }

        if (TerraformManager.Instance.IsTileOverCarvingDig(center, __instance.m_tileSize)) {
            int agent = PathfindingTiles.IdOf(tile).z;
            if (PathfindingTiles.TrySetMinRegionArea(__instance, agent, DigTileMinRegionArea, out _stashedMinRegionArea)) {
                _stashedRegionAgent = agent;
                _didStashRegion = true;
            }
        }
    }

    static void Postfix(Pathfinding __instance) {
        if (_didStash) {
            __instance.m_waterLayers = _stashedWaterLayers;
            _didStash = false;
        }
        if (_didStashRegion) {
            PathfindingTiles.TrySetMinRegionArea(__instance, _stashedRegionAgent, _stashedMinRegionArea, out _);
            _didStashRegion = false;
        }
    }
}
