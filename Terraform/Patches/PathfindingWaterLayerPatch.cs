using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Pathfinding), "BuildTile")]
public static class Pathfinding_BuildTile_SkipWaterOverCave_Patch {
    private static LayerMask _stashedWaterLayers;
    private static bool _didStash;

    static void Prefix(Pathfinding __instance, object tile) {
        _didStash = false;
        if (TerraformManager.Instance == null || tile == null) return;

        var centerField = AccessTools.Field(tile.GetType(), "m_center");
        if (centerField == null) return;
        Vector3 center = (Vector3)centerField.GetValue(tile);

        if (TerraformManager.Instance.IsTileOverCaveBelowSeaLevel(center, __instance.m_tileSize)) {
            _stashedWaterLayers = __instance.m_waterLayers;
            __instance.m_waterLayers = 0;
            _didStash = true;
        }
    }

    static void Postfix(Pathfinding __instance) {
        if (_didStash) {
            __instance.m_waterLayers = _stashedWaterLayers;
            _didStash = false;
        }
    }
}
