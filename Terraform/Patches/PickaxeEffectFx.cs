using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

internal static class PickaxeEffectFx {
    private const string VfxPlaceDiggName     = "vfx_Place_digg";
    private const string SfxRockDestroyedName = "sfx_rock_destroyed";

    private static System.Reflection.FieldInfo _humanoidCurrentAttackField;
    private static GameObject _vfxPlaceDigg;
    private static GameObject _sfxRockDestroyed;

    public static Quaternion RotationFromDir(Vector3 dir) =>
        dir.sqrMagnitude > 1e-4f
            ? Quaternion.LookRotation(dir, Vector3.up)
            : Quaternion.identity;

    public static void PlayWeaponAndAttackTerrainEffects(Humanoid humanoid, Vector3 point, Quaternion rot) {
        if (humanoid == null) return;
        var weapon = humanoid.GetCurrentWeapon();
        weapon?.m_shared.m_hitTerrainEffect.Create(point, rot);

        _humanoidCurrentAttackField ??= AccessTools.Field(typeof(Humanoid), "m_currentAttack");
        if (_humanoidCurrentAttackField?.GetValue(humanoid) is Attack atk) {
            atk.m_hitTerrainEffect.Create(point, rot);
        }
    }

    public static void PlayRockBreakEffects(Vector3 point, Quaternion rot) {
        if (ZNetScene.instance == null) return;

        if (_vfxPlaceDigg == null)     _vfxPlaceDigg     = ZNetScene.instance.GetPrefab(VfxPlaceDiggName);
        if (_sfxRockDestroyed == null) _sfxRockDestroyed = ZNetScene.instance.GetPrefab(SfxRockDestroyedName);

        if (_vfxPlaceDigg != null) {
            var go = UnityEngine.Object.Instantiate(_vfxPlaceDigg, point, rot);
            UnityEngine.Object.Destroy(go, 5f);
        }
        if (_sfxRockDestroyed != null) {
            var go = UnityEngine.Object.Instantiate(_sfxRockDestroyed, point, rot);
            UnityEngine.Object.Destroy(go, 5f);
        }
    }
}
