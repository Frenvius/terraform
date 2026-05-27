using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float NavBakeGridStep     = 0.5f;
    private const float NavBakeRadiusScale  = 1.5f;
    private const float NavBakeMinDrop      = 1.0f;
    private const float NavBakeProbeUp      = 1.0f;
    private const int   NavBakeSmoothPasses = 1;
    private const float NavBakeAgentSlope   = 50f;

    private readonly Dictionary<int, NavMeshDataInstance> _digNavInstances = new();
    private readonly Dictionary<int, Mesh> _digNavMeshes = new();

    private static bool ShouldBakeForDigMode(DigMode mode) {
        return mode == DigMode.Surface || mode == DigMode.OpenHoleDeepen;
    }

    private const float NavSeaLevel          = 30f;
    private const float NavSeaLevelTolerance = 1f;

    public bool IsTileOverCaveBelowSeaLevel(Vector3 tileCenter, float tileSize) {
        float half = tileSize * 0.5f;
        foreach (var kv in _chunkDigHistory) {
            var records = kv.Value;
            for (int i = 0; i < records.Count; i++) {
                var r = records[i];
                if (r.WorldPos.y > NavSeaLevel - NavSeaLevelTolerance) continue;
                if (!ShouldBakeForDigMode(r.Mode)) continue;
                float dx = Mathf.Abs(r.WorldPos.x - tileCenter.x) - half;
                float dz = Mathf.Abs(r.WorldPos.z - tileCenter.z) - half;
                if (dx < r.Radius && dz < r.Radius) return true;
            }
        }
        return false;
    }

    public void RebuildChunkDigNav(int chunkId, List<DigRecord> records) {
        RemoveDigNavForChunk(chunkId);
        if (records == null || records.Count == 0) return;

        int agentId = GetNavLinksAgentId();
        if (agentId == 0) return;

        var bakeable = new List<DigRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
            if (ShouldBakeForDigMode(records[i].Mode)) bakeable.Add(records[i]);
        if (bakeable.Count == 0) return;

        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        float yMin = float.PositiveInfinity, yMax = float.NegativeInfinity;
        for (int i = 0; i < bakeable.Count; i++) {
            var r = bakeable[i];
            float rr = r.Radius * NavBakeRadiusScale;
            if (r.WorldPos.x - rr < minX) minX = r.WorldPos.x - rr;
            if (r.WorldPos.x + rr > maxX) maxX = r.WorldPos.x + rr;
            if (r.WorldPos.z - rr < minZ) minZ = r.WorldPos.z - rr;
            if (r.WorldPos.z + rr > maxZ) maxZ = r.WorldPos.z + rr;
            if (r.WorldPos.y < yMin) yMin = r.WorldPos.y;
            if (r.WorldPos.y > yMax) yMax = r.WorldPos.y;
        }

        int W = Mathf.CeilToInt((maxX - minX) / NavBakeGridStep) + 1;
        int H = Mathf.CeilToInt((maxZ - minZ) / NavBakeGridStep) + 1;
        if (W <= 1 || H <= 1) return;

        var floorY = new float[W, H];
        var has    = new bool[W, H];
        int sampleHits = 0;

        for (int gx = 0; gx < W; gx++) {
            float wx = minX + gx * NavBakeGridStep;
            for (int gz = 0; gz < H; gz++) {
                float wz = minZ + gz * NavBakeGridStep;

                bool insideAnyDig = false;
                for (int i = 0; i < bakeable.Count; i++) {
                    var r = bakeable[i];
                    float rr = r.Radius * NavBakeRadiusScale;
                    float dx = wx - r.WorldPos.x, dz = wz - r.WorldPos.z;
                    if (dx * dx + dz * dz <= rr * rr) { insideAnyDig = true; break; }
                }
                if (!insideAnyDig) continue;

                Vector3 sample = new Vector3(wx, 0f, wz);
                if (!Heightmap.GetHeight(sample, out float heightmapY)) continue;

                Vector3 origin = new Vector3(wx, heightmapY + NavBakeProbeUp, wz);
                if (!VoxelGroundProbe.FindVoxelFloorBelow(origin, 50f, out var hit)) continue;

                float floorWY = hit.point.y;
                if (heightmapY - floorWY < NavBakeMinDrop) continue;

                floorY[gx, gz] = floorWY;
                has[gx, gz] = true;
                sampleHits++;
            }
        }

        if (sampleHits < 3) return;

        for (int pass = 0; pass < NavBakeSmoothPasses; pass++) {
            var next = (float[,])floorY.Clone();
            for (int x = 0; x < W; x++) {
                for (int z = 0; z < H; z++) {
                    if (!has[x, z]) continue;
                    float sum = floorY[x, z]; int cnt = 1;
                    if (x > 0     && has[x - 1, z]) { sum += floorY[x - 1, z]; cnt++; }
                    if (x < W - 1 && has[x + 1, z]) { sum += floorY[x + 1, z]; cnt++; }
                    if (z > 0     && has[x, z - 1]) { sum += floorY[x, z - 1]; cnt++; }
                    if (z < H - 1 && has[x, z + 1]) { sum += floorY[x, z + 1]; cnt++; }
                    next[x, z] = sum / cnt;
                }
            }
            floorY = next;
        }

        var verts = new List<Vector3>();
        var index = new int[W, H];
        for (int x = 0; x < W; x++) {
            for (int z = 0; z < H; z++) {
                index[x, z] = -1;
                if (!has[x, z]) continue;
                float wx = minX + x * NavBakeGridStep;
                float wz = minZ + z * NavBakeGridStep;
                index[x, z] = verts.Count;
                verts.Add(new Vector3(wx, floorY[x, z], wz));
            }
        }
        if (verts.Count < 3) return;

        var tris = new List<int>();
        for (int x = 0; x < W - 1; x++) {
            for (int z = 0; z < H - 1; z++) {
                bool va = has[x, z], vb = has[x + 1, z], vc = has[x, z + 1], vd = has[x + 1, z + 1];
                int v = (va ? 1 : 0) + (vb ? 1 : 0) + (vc ? 1 : 0) + (vd ? 1 : 0);
                if (v < 3) continue;
                int a = index[x, z], b = index[x + 1, z], c = index[x, z + 1], d = index[x + 1, z + 1];
                if (v == 4) {
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                } else if (!vd) { tris.Add(a); tris.Add(c); tris.Add(b); }
                else if (!va)   { tris.Add(b); tris.Add(c); tris.Add(d); }
                else if (!vb)   { tris.Add(a); tris.Add(c); tris.Add(d); }
                else            { tris.Add(a); tris.Add(d); tris.Add(b); }
            }
        }
        if (tris.Count == 0) return;

        var mesh = new Mesh { name = "TerraformDigNav", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        _digNavMeshes[chunkId] = mesh;

        var sources = new List<NavMeshBuildSource> {
            new NavMeshBuildSource {
                shape        = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform    = Matrix4x4.identity,
                area         = 0
            }
        };

        var settings = NavMesh.GetSettingsByID(agentId);
        settings.minRegionArea     = 0f;
        settings.overrideVoxelSize = false;
        settings.agentSlope        = NavBakeAgentSlope;

        Vector3 center = new Vector3((minX + maxX) * 0.5f, (yMin + yMax) * 0.5f, (minZ + maxZ) * 0.5f);
        Vector3 size   = new Vector3(maxX - minX + 4f, 40f, maxZ - minZ + 4f);
        var localBounds = new Bounds(Vector3.zero, size);

        var data = NavMeshBuilder.BuildNavMeshData(settings, sources, localBounds, center, Quaternion.identity);
        var inst = NavMesh.AddNavMeshData(data);
        _digNavInstances[chunkId] = inst;

        if (VerboseLogs)
            DebugLog($"[navbake] chunk={chunkId} digs={bakeable.Count} bbox=({minX:F1},{minZ:F1})..({maxX:F1},{maxZ:F1}) verts={verts.Count} tris={tris.Count / 3}");
    }

    private void RemoveDigNavForChunk(int chunkId) {
        if (_digNavInstances.TryGetValue(chunkId, out var inst)) {
            if (inst.valid) NavMesh.RemoveNavMeshData(inst);
            _digNavInstances.Remove(chunkId);
        }
        if (_digNavMeshes.TryGetValue(chunkId, out var mesh)) {
            if (mesh != null) Destroy(mesh);
            _digNavMeshes.Remove(chunkId);
        }
    }

    private void BakeDigNavForChunk(int chunkId, int chunkOriginX, int chunkOriginZ) {
        long key = ChunkKey(chunkOriginX, chunkOriginZ);
        if (_chunkDigHistory.TryGetValue(key, out var records))
            RebuildChunkDigNav(chunkId, records);
        else
            RemoveDigNavForChunk(chunkId);
    }
}
