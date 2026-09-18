using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Character), "UnderWorldCheck")]
public static class Character_UnderWorldCheck_Patch {
    static bool Prefix(Character __instance) {
        var manager = TerraformManager.Instance;
        if (manager == null) return true;
        Vector3 position = __instance.transform.position;
        if (!manager.HasDigsAround(position)) return true;
        return !manager.ShouldSuppressUnderWorldCheck(position);
    }
}
