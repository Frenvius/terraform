using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Attack), nameof(Attack.SpawnOnHitTerrain))]
public static class Attack_SpawnOnHitTerrain_Patch {
    static bool Prefix(
        Vector3 hitPoint,
        Character character,
        ItemDrop.ItemData weapon,
        ref GameObject __result) {

        if (weapon == null) return true;
        if (weapon.m_shared.m_skillType != Skills.SkillType.Pickaxes) return true;
        if (TerraformManager.Instance == null) return true;
        if (character == null || character != Player.m_localPlayer) return true;

        float radius = Configs.ModConfig.BrushSize?.Value ?? 1.5f;
        if (!PrivateArea.CheckAccess(hitPoint, radius)) {
            __result = null;
            return false;
        }
        if (Location.IsInsideNoBuildLocation(hitPoint)) {
            __result = null;
            return false;
        }

        PickaxeEffectFx.PlayRockBreakEffects(hitPoint, Quaternion.identity);
        TerraformManager.Instance.PerformDigAt(hitPoint);
        __result = null;
        return false;
    }
}
