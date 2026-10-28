using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface))]
public static class WaterVolume_GetWaterSurface_Patch {
    private const float NoWaterSentinel = -10000f;

    static bool Prefix(Vector3 point, ref float __result) {
        var manager = TerraformManager.Instance;
        if (manager == null || !manager.HasDigsAround(point)) return true;
        if (!manager.IsPlayerInVoxelCavity(point)) return true;
        __result = NoWaterSentinel;
        return false;
    }

    static void Postfix(WaterVolume __instance, Vector3 point, ref float __result) {
        if (__result <= NoWaterSentinel) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;
        __result -= manager.WaterSinkAt(__instance, point);
    }
}
