using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(Player), "PieceRayTest")]
public static class Player_PieceRayTest_Patch {
    private const float PlaceRayDistance = 50f;
    private const float SameHitTolerance = 1e-4f;

    static void Postfix(bool __result, ref Vector3 point, ref Vector3 normal, ref Heightmap heightmap) {
        if (!__result) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;

        manager.RecordPlacementSurface(point, normal);
        if (heightmap != null) return;

        var surface = VoxelSurfaceUnderCrosshair(point);
        if (surface != null) heightmap = surface.Owner;
    }

    private static VoxelSurface VoxelSurfaceUnderCrosshair(Vector3 vanillaHitPoint) {
        if (GameCamera.instance == null) return null;
        var cam = GameCamera.instance.transform;
        if (!Physics.Raycast(cam.position, cam.forward, out var hit, PlaceRayDistance, VoxelGroundProbe.PieceMask))
            return null;
        if ((hit.point - vanillaHitPoint).sqrMagnitude > SameHitTolerance) return null;
        return hit.collider.GetComponent<VoxelSurface>();
    }
}

[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
public static class Player_UpdatePlacementGhost_Patch {
    private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhost =
        AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

    static void Postfix(Player __instance) {
        if (__instance != Player.m_localPlayer) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;

        var ghost = PlacementGhost(__instance);
        if (ghost == null || !ghost.activeSelf) return;
        var piece = ghost.GetComponent<Piece>();
        if (piece == null || !piece.m_groundPiece) return;

        var ghostTransform = ghost.transform;
        if (!TryGetSurfacePose(manager, ghost, out var point, out var normal)) return;
        ghostTransform.position = point;
        ghostTransform.rotation = Quaternion.FromToRotation(Vector3.up, normal) * ghostTransform.rotation;
        AlignRingsToTransform(ghost);
    }

    private static GameObject _alignedGhost;

    private static void AlignRingsToTransform(GameObject ghost) {
        if (_alignedGhost == ghost) return;
        _alignedGhost = ghost;
        var ghostTransform = ghost.transform;
        foreach (var renderer in ghost.GetComponentsInChildren<ParticleSystemRenderer>(true)) {
            if (renderer.renderMode != ParticleSystemRenderMode.HorizontalBillboard) continue;
            var particles = renderer.GetComponent<ParticleSystem>();
            if (particles == null) continue;

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment  = ParticleSystemRenderSpace.Local;
            FaceQuadsAlongGhostUp(particles, ghostTransform);
        }
    }

    private static void FaceQuadsAlongGhostUp(ParticleSystem particles, Transform ghostTransform) {
        Quaternion particlesInGhost = Quaternion.Inverse(ghostTransform.rotation) * particles.transform.rotation;
        Vector3 ghostUpInParticles  = Quaternion.Inverse(particlesInGhost) * Vector3.up;
        Vector3 euler = Quaternion.FromToRotation(Vector3.forward, ghostUpInParticles).eulerAngles * Mathf.Deg2Rad;

        var main = particles.main;
        main.startRotation3D = true;
        main.startRotationX  = euler.x;
        main.startRotationY  = euler.y;
        main.startRotationZ  = euler.z;
        particles.Clear(true);
        particles.Play(true);
    }

    private static bool TryGetSurfacePose(TerraformManager manager, GameObject ghost, out Vector3 point, out Vector3 normal) {
        Vector3 ghostPos = ghost.transform.position;
        var op = ghost.GetComponent<TerrainOp>();
        if (op != null && TerraformManager.IsVoxelRaise(op.m_settings))
            return manager.TryGetPlacementSurface(ghostPos, out point, out normal);
        return manager.TryGetCeilingPlacement(ghostPos, out point, out normal);
    }
}

[HarmonyPatch(typeof(TerrainOp), "Awake")]
public static class TerrainOp_Awake_Patch {
    static void Prefix(TerrainOp __instance, out TerraformManager.TerrainOpVoxelPass __state) {
        __state = default;
        if (TerrainOp.m_forceDisableTerrainOps) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;
        __state = manager.BeginTerrainOp(__instance.transform.position, __instance.m_settings);
    }

    static void Postfix(TerrainOp __instance, TerraformManager.TerrainOpVoxelPass __state) {
        if (!__state.Any) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;
        manager.FinishTerrainOp(__instance.m_settings, __state);
    }
}
