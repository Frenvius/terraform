using HarmonyLib;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Character), "UnderWorldCheck")]
public static class Character_UnderWorldCheck_Patch {
    static bool Prefix(Character __instance) {
        if (__instance != Player.m_localPlayer) return true;
        if (TerraformManager.Instance == null) return true;
        if (!TerraformManager.Instance.IsPlayerInVoxelCavity(__instance.transform.position)) return true;
        return false;
    }
}
