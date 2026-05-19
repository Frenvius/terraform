using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

internal static class VoxelGroundProbe {
    private const string VoxelMeshObjectName = "TerraformVoxels";

    private static int s_pieceMask;
    private static int s_terrainMask;
    private static bool s_init;

    public static int PieceMask {
        get { Init(); return s_pieceMask; }
    }
    public static int TerrainMask {
        get { Init(); return s_terrainMask; }
    }
    public static int TerrainAndPieceMask => TerrainMask | PieceMask;

    private static void Init() {
        if (s_init) return;
        s_pieceMask   = LayerMask.GetMask("piece");
        s_terrainMask = LayerMask.GetMask("terrain");
        s_init        = true;
    }

    public static bool IsVoxelHit(RaycastHit hit) {
        return hit.collider != null && hit.collider.gameObject.name == VoxelMeshObjectName;
    }

    public static float VoxelTopY(Vector3 originAt6000) {
        if (Physics.Raycast(originAt6000, Vector3.down, out var hit, 10000f, PieceMask)
            && IsVoxelHit(hit)) {
            return hit.point.y;
        }
        return float.NegativeInfinity;
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), new[] { typeof(Vector3) })]
public static class ZoneSystem_GetGroundHeight_Patch {
    static bool Prefix(Vector3 p, ref float __result) {
        Vector3 origin = p;
        origin.y = 6000f;

        bool hitTerrain = Physics.Raycast(origin, Vector3.down, out var hit, 10000f, VoxelGroundProbe.TerrainMask);
        float terrainY  = hitTerrain ? hit.point.y : float.NegativeInfinity;
        float voxelY    = VoxelGroundProbe.VoxelTopY(origin);

        if (voxelY > terrainY)  __result = voxelY;
        else if (hitTerrain)    __result = terrainY;
        else                    __result = p.y;
        return false;
    }
}

[HarmonyPatch]
public static class ZoneSystem_GetGroundHeight_Out_Patch {
    static System.Reflection.MethodBase TargetMethod() {
        return AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight),
            new[] { typeof(Vector3), typeof(float).MakeByRefType() });
    }

    static bool Prefix(Vector3 p, ref float height, ref bool __result) {
        p.y = 6000f;

        bool hitTerrain = Physics.Raycast(p, Vector3.down, out var hit, 10000f, VoxelGroundProbe.TerrainMask);
        float terrainY  = hitTerrain ? hit.point.y : float.NegativeInfinity;
        float voxelY    = VoxelGroundProbe.VoxelTopY(p);

        if (voxelY > terrainY) {
            height = voxelY; __result = true;
        } else if (hitTerrain) {
            height = terrainY; __result = true;
        } else {
            height = 0f; __result = false;
        }
        return false;
    }
}
