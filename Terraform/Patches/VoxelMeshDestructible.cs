using UnityEngine;

namespace Terraform.Patches;

public class VoxelMeshDestructible : MonoBehaviour, IDestructible {
    public void Damage(HitData hit) {
        if (hit == null) return;
        if (hit.m_skill != Skills.SkillType.Pickaxes) return;
        if (TerraformManager.Instance == null) return;
        var attacker = hit.GetAttacker();
        if (attacker == null || attacker != Player.m_localPlayer) return;

        if (!TerraformManager.Instance.PerformDigAt(hit.m_point)) return;

        var humanoid = attacker as Humanoid;
        var rot = PickaxeEffectFx.RotationFromDir(hit.m_dir);
        PickaxeEffectFx.PlayWeaponAndAttackTerrainEffects(humanoid, hit.m_point, rot);
        PickaxeEffectFx.PlayRockBreakEffects(hit.m_point, rot);
    }

    public DestructibleType GetDestructibleType() => DestructibleType.Default;
}
