using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(GameCamera), "GetOffsetedEyePos")]
public static class GameCamera_GetOffsetedEyePos_Patch {
    static void Postfix(GameCamera __instance, ref Vector3 __result) {
        var player = Player.m_localPlayer;
        if (player == null || player.m_eye == null) return;
        var manager = TerraformManager.Instance;
        if (manager == null || !manager.HasDigsAround(__result)) return;

        Vector3 eye = player.m_eye.position;
        Vector3 offset = __result - eye;
        float distance = offset.magnitude;
        if (distance < 1e-4f) return;
        Vector3 dir = offset / distance;
        if (!Physics.SphereCast(eye, __instance.m_raycastWidth, dir, out var hit, distance, __instance.m_blockCameraMask)) return;
        __result = eye + dir * hit.distance;
    }
}
