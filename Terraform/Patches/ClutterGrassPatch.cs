using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(ClutterSystem), "GenerateVegPatch")]
public static class ClutterSystem_GenerateVegPatch_Patch {
    private const int LayeredGroundSlots = 2;

    internal static int LayerSlots = 1;

    static void Prefix(ClutterSystem __instance, Vector2Int patchID, float size, out float __state) {
        __state = __instance.m_amountScale;
        var manager = TerraformManager.Instance;
        Vector3 center = new Vector3(patchID.x * size, 0f, patchID.y * size);
        if (manager == null || !manager.HasVoxelGeometryNear(center, size / 2f)) return;
        LayerSlots = LayeredGroundSlots;
        __instance.m_amountScale = __state * LayeredGroundSlots;
    }

    static void Finalizer(ClutterSystem __instance, float __state) {
        __instance.m_amountScale = __state;
        LayerSlots = 1;
    }
}

[HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
public static class ClutterSystem_GetGroundInfo_Patch {
    private const float LayerStepDown = 0.05f;
    private const int MaxGroundLayers = 4;
    private const int MaxLayerCasts = 8;

    private static readonly RaycastHit[] GroundLayers = new RaycastHit[MaxGroundLayers];

    static bool Prefix(Vector3 p, out Vector3 point, out Vector3 normal,
                       out Heightmap hmap, out Heightmap.Biome biome, ref bool __result) {
        point = p; normal = Vector3.up; hmap = null;
        biome = Heightmap.Biome.Meadows; __result = false;

        int count = CollectGroundLayers(p + Vector3.up * 500f);
        int slot = LayerIndex(p, ClutterSystem_GenerateVegPatch_Patch.LayerSlots);
        if (slot >= count) return false;

        var ground = GroundLayers[slot];
        if (IsVoxel(ground) && ZoneSystem.instance != null && ground.point.y < ZoneSystem.instance.m_waterLevel) return false;
        point = ground.point; normal = ground.normal; hmap = GroundOwner(ground);
        biome = hmap.GetBiome(point); __result = true;
        return false;
    }

    private static int CollectGroundLayers(Vector3 origin) {
        if (!Physics.Raycast(origin, Vector3.down, out var current, 1000f, VoxelGroundProbe.TerrainAndPieceMask)) return 0;

        if (current.collider.GetComponent<Heightmap>() == null && !IsVoxel(current)) {
            if (!Physics.Raycast(origin, Vector3.down, out var below, 1000f, VoxelGroundProbe.TerrainMask)) return 0;
            if (below.collider.GetComponent<Heightmap>() == null) return 0;
            GroundLayers[0] = below;
            return 1;
        }

        int count = 0;
        for (int cast = 0; cast < MaxLayerCasts; cast++) {
            if (cast == 0 || IsCoveredGrassGround(current)) GroundLayers[count++] = current;
            if (count == MaxGroundLayers || current.collider.GetComponent<Heightmap>() != null) break;
            Vector3 next = current.point + Vector3.down * LayerStepDown;
            if (!Physics.Raycast(next, Vector3.down, out current, 1000f, VoxelGroundProbe.TerrainAndPieceMask)) break;
        }
        return count;
    }

    private static bool IsVoxel(RaycastHit hit) {
        var voxel = hit.collider.GetComponent<VoxelSurface>();
        return voxel != null && voxel.Owner != null;
    }

    private static bool IsCoveredGrassGround(RaycastHit hit) {
        if (hit.collider.GetComponent<Heightmap>() != null) return true;
        if (!IsVoxel(hit)) return false;
        return !Heightmap.GetHeight(hit.point, out float groundY)
            || hit.point.y >= groundY - TerraformManager.UndergroundEnterMarginY;
    }

    private static Heightmap GroundOwner(RaycastHit hit) {
        var hm = hit.collider.GetComponent<Heightmap>();
        return hm != null ? hm : hit.collider.GetComponent<VoxelSurface>().Owner;
    }

    private static int LayerIndex(Vector3 p, int slots) {
        if (slots <= 1) return 0;
        int hash = (Mathf.FloorToInt(p.x * 97.13f) * 73856093) ^ (Mathf.FloorToInt(p.z * 89.71f) * 19349663);
        return (hash & int.MaxValue) % slots;
    }
}
