using HarmonyLib;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Character), "UnderWorldCheck")]
public static class Character_UnderWorldCheck_Patch {
    static bool Prefix(Character __instance) {
        if (TerraformManager.Instance == null) return true;
        return !TerraformManager.Instance.ShouldSuppressUnderWorldCheck(__instance.transform.position);
    }
}
