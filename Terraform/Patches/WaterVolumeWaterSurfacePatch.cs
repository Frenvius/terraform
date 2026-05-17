using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface))]
public static class WaterVolume_GetWaterSurface_Patch {
    private const float NoWaterSentinel = -10000f;

    static bool Prefix(Vector3 point, ref float __result) {
        if (TerraformManager.Instance == null) return true;
        if (!TerraformManager.Instance.IsPlayerInVoxelCavity(point)) return true;
        if (TerraformManager.Instance.IsWaterReachableAtPosition(point)) return true;
        __result = NoWaterSentinel;
        return false;
    }
}
