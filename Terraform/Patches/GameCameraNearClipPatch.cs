using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(GameCamera), "UpdateNearClipping")]
public static class GameCamera_UpdateNearClipping_Patch {
    static void Postfix(GameCamera __instance, Vector3 camPos, Camera ___m_camera) {
        if (___m_camera == null || ___m_camera.nearClipPlane <= __instance.m_nearClipPlaneMin) return;
        var manager = TerraformManager.Instance;
        if (manager == null || !manager.HasDigsAround(camPos)) return;
        if (!Physics.CheckSphere(camPos, NearPlaneCornerReach(___m_camera, __instance.m_nearClipPlaneMax), __instance.m_blockCameraMask)) return;
        ___m_camera.nearClipPlane = __instance.m_nearClipPlaneMin;
    }

    private static float NearPlaneCornerReach(Camera camera, float nearPlane) {
        float halfHeight = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        return nearPlane * Mathf.Sqrt(1f + halfHeight * halfHeight * (1f + camera.aspect * camera.aspect));
    }
}
