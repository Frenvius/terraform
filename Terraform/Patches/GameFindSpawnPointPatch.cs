using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Game), "FindSpawnPoint")]
public static class Game_FindSpawnPoint_Patch {
    private static Vector3 _capturedLogoutPoint;
    private static bool _hadLogoutPoint;

    static void Prefix(Game __instance) {
        _hadLogoutPoint = false;
        var profileField = AccessTools.Field(typeof(Game), "m_playerProfile");
        if (profileField == null) return;
        if (profileField.GetValue(__instance) is not PlayerProfile profile) return;
        if (!profile.HaveLogoutPoint()) return;

        _capturedLogoutPoint = profile.GetLogoutPoint();
        _hadLogoutPoint = true;
    }

    static void Postfix(ref Vector3 point, bool __result) {
        if (!__result) return;
        if (!_hadLogoutPoint) return;
        if (TerraformManager.Instance == null) return;

        if (point.x != _capturedLogoutPoint.x || point.z != _capturedLogoutPoint.z) return;
        if (point.y <= _capturedLogoutPoint.y + 1.5f) return;

        if (!TerraformManager.Instance.HasDigNearXZ(_capturedLogoutPoint.x, _capturedLogoutPoint.z)) return;

        point.y = _capturedLogoutPoint.y;
        TerraformPlugin.Log.LogInfo(
            $"Spawn: restored cave Y={point.y:F2} (vanilla had bumped to terrain)");
    }
}
