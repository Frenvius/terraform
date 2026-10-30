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
        var manager = TerraformManager.Instance;
        if (manager == null) return true;
        if (character == null || character != Player.m_localPlayer) return true;
        if (manager.DiggingUnavailable()) return true;

        if (manager.PerformDigAt(hitPoint))
            PickaxeEffectFx.PlayRockBreakEffects(hitPoint, Quaternion.identity);
        __result = null;
        return false;
    }
}
