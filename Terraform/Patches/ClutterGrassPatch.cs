using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
public static class ClutterSystem_GetGroundInfo_Patch {
    static bool Prefix(Vector3 p, out Vector3 point, out Vector3 normal,
                       out Heightmap hmap, out Heightmap.Biome biome, ref bool __result) {
        Vector3 origin = p + Vector3.up * 500f;

        if (Physics.Raycast(origin, Vector3.down, out var hit, 1000f, VoxelGroundProbe.TerrainAndPieceMask)) {
            var terrainHm = hit.collider.GetComponent<Heightmap>();
            if (terrainHm != null) {
                point = hit.point; normal = hit.normal; hmap = terrainHm;
                biome = terrainHm.GetBiome(point); __result = true;
                return false;
            }

            var voxel = hit.collider.GetComponent<VoxelSurface>();
            if (voxel != null && voxel.Owner != null) {
                point = hit.point; normal = hit.normal; hmap = voxel.Owner;
                biome = voxel.Owner.GetBiome(point); __result = true;
                return false;
            }

            if (Physics.Raycast(origin, Vector3.down, out var below, 1000f, VoxelGroundProbe.TerrainMask)) {
                var belowHm = below.collider.GetComponent<Heightmap>();
                if (belowHm != null) {
                    point = below.point; normal = below.normal; hmap = belowHm;
                    biome = belowHm.GetBiome(point); __result = true;
                    return false;
                }
            }
        }

        point = p; normal = Vector3.up; hmap = null;
        biome = Heightmap.Biome.Meadows; __result = false;
        return false;
    }
}
