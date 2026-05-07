using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

public static class HolePunchSystem {
    private static readonly Dictionary<int, HashSet<(int x, int y)>> _holes = new();

    public static void PunchHole(Heightmap hm, int cellX, int cellY) {
        int id = hm.GetInstanceID();
        if (!_holes.TryGetValue(id, out var set))
            _holes[id] = set = new HashSet<(int, int)>();
        set.Add((cellX, cellY));
    }

    public static bool IsHole(Heightmap hm, int cellX, int cellY) {
        return _holes.TryGetValue(hm.GetInstanceID(), out var set)
            && set.Contains((cellX, cellY));
    }

    public static IEnumerable<(int x, int y)> GetHoles(Heightmap hm) {
        if (_holes.TryGetValue(hm.GetInstanceID(), out var set))
            return set;
        return Enumerable.Empty<(int, int)>();
    }

    internal static void FilterHoleTriangles(Heightmap hm, Mesh mesh) {
        if (mesh == null) return;
        var holes = GetHoles(hm);
        if (!holes.Any()) return;

        var indices = mesh.GetIndices(0);
        var verts = mesh.vertices;
        if (verts.Length == 0) return;

        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        float invScale = 1f / hm.m_scale;
        var filtered = new List<int>(indices.Length);
        int dropped = 0, kept = 0;

        for (int i = 0; i + 2 < indices.Length; i += 3) {
            int ia = indices[i];
            int ib = indices[i + 1];
            int ic = indices[i + 2];
            UnityEngine.Vector3 a = verts[ia];
            UnityEngine.Vector3 b = verts[ib];
            UnityEngine.Vector3 c = verts[ic];

            float centroidLocalX = (a.x + b.x + c.x) * (1f / 3f);
            float centroidLocalZ = (a.z + b.z + c.z) * (1f / 3f);
            int cx = UnityEngine.Mathf.FloorToInt((centroidLocalX + halfSize) * invScale);
            int cz = UnityEngine.Mathf.FloorToInt((centroidLocalZ + halfSize) * invScale);

            if (IsHole(hm, cx, cz)) {
                dropped++;
            } else {
                filtered.Add(ia);
                filtered.Add(ib);
                filtered.Add(ic);
                kept++;
            }
        }

        mesh.SetIndices(filtered.ToArray(), MeshTopology.Triangles, 0);

        if (Terraform.Configs.ModConfig.DebugMode != null && Terraform.Configs.ModConfig.DebugMode.Value) {
            int holeCount = 0;
            foreach (var _ in holes) holeCount++;
            TerraformPlugin.Log.LogInfo(
                $"[punch-filter] hm={hm.gameObject.name} mesh={mesh.name} holes={holeCount} dropped={dropped} kept={kept}");
        }
    }
}

[HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh")]
public static class Heightmap_RebuildCollisionMesh_Patch {
    static void Postfix(Heightmap __instance, Mesh ___m_collisionMesh, MeshCollider ___m_collider) {
        HolePunchSystem.FilterHoleTriangles(__instance, ___m_collisionMesh);
        if (___m_collider != null)
            ___m_collider.sharedMesh = ___m_collisionMesh;
    }
}

[HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
public static class Heightmap_RebuildRenderMesh_Patch {
    static void Postfix(Heightmap __instance, Mesh ___m_renderMesh) {
        HolePunchSystem.FilterHoleTriangles(__instance, ___m_renderMesh);
    }
}
