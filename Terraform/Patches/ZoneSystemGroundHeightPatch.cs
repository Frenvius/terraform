using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

internal static class VoxelGroundProbe {
    private const string VoxelMeshObjectName = "TerraformVoxels";

    private static int s_pieceMask;
    private static int s_terrainMask;
    private static bool s_init;

    private static readonly RaycastHit[] s_hits = new RaycastHit[64];

    private const float GroundProbeUp = 1.5f;

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

    public static bool NearestGroundY(Vector3 p, out float result) {
        float queryY  = p.y;
        bool  any     = false;
        float best    = 0f;
        float bestDist = float.MaxValue;

        var manager = TerraformManager.Instance;
        bool replaced = manager != null && manager.IsReplaced(p);
        bool hasTerrain = Heightmap.GetHeight(p, out float terrainY);
        if (hasTerrain && !replaced) {
            any = true; best = terrainY; bestDist = Mathf.Abs(terrainY - queryY);
        }

        bool voxelFound = false;
        Vector3 origin = p; origin.y = p.y + GroundProbeUp;
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, s_hits, 10000f, PieceMask);
        for (int i = 0; i < n; i++) {
            if (!IsVoxelHit(s_hits[i])) continue;
            voxelFound = true;
            float d = Mathf.Abs(s_hits[i].point.y - queryY);
            if (d < bestDist) { bestDist = d; best = s_hits[i].point.y; any = true; }
        }

        if (!any && hasTerrain && !voxelFound) { any = true; best = terrainY; }
        result = best;
        return any;
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), new[] { typeof(Vector3) })]
public static class ZoneSystem_GetGroundHeight_Patch {
    static bool Prefix(Vector3 p, ref float __result) {
        var manager = TerraformManager.Instance;
        if (manager == null || !manager.HasDigsAround(p)) return true;
        __result = VoxelGroundProbe.NearestGroundY(p, out float y) ? y : p.y;
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
        var manager = TerraformManager.Instance;
        if (manager == null || !manager.HasDigsAround(p)) return true;
        if (VoxelGroundProbe.NearestGroundY(p, out float y)) {
            height = y; __result = true;
        } else {
            height = 0f; __result = false;
        }
        return false;
    }
}
