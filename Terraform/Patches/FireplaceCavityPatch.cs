using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Fireplace), "CheckUnderTerrain")]
public static class Fireplace_CheckUnderTerrain_Patch {
    private static readonly AccessTools.FieldRef<Fireplace, bool> Blocked = AccessTools.FieldRefAccess<Fireplace, bool>("m_blocked");
    private static readonly AccessTools.FieldRef<int> SolidRayMask = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Fireplace), "m_solidRayMask"));

    private const float CeilingCheckDistance = 0.5f;

    static void Postfix(Fireplace __instance) {
        if (!Blocked(__instance) || __instance.m_disableCoverCheck) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;
        Vector3 position = __instance.transform.position;
        if (!manager.HasDigsAround(position)) return;
        Blocked(__instance) = IsCoveredBySolid(__instance, position) || IsSmokeBlocked(__instance);
    }

    private static bool IsCoveredBySolid(Fireplace fireplace, Vector3 position) {
        Vector3 origin = position + Vector3.up * fireplace.m_coverCheckOffset;
        return Physics.Raycast(origin, Vector3.up, CeilingCheckDistance, SolidRayMask());
    }

    private static bool IsSmokeBlocked(Fireplace fireplace) {
        return fireplace.m_smokeSpawner != null && fireplace.m_smokeSpawner.IsBlocked();
    }
}
