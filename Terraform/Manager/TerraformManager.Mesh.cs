using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Terraform.Voxel.Core;
using Terraform.Voxel.Meshing;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static readonly AccessTools.FieldRef<Heightmap, Mesh> HeightmapRenderMesh =
        AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");

    private static readonly Vector3[] s_heightmapNormals = new Vector3[(VoxelChunk.SIZE + 1) * (VoxelChunk.SIZE + 1)];

    private struct MeshStats {
        public int DroppedDegenerate;
        public int OpenEdges;
        public int Analytic;
        public bool NormalsFromRenderMesh;
    }

    private void RebuildMesh(ChunkRenderData data) {
        _waterStreamsDirty = true;
        var heights = data.Chunk != null ? GetHeightsArray(data.Heightmap) : null;
        int cornerStride = VoxelChunk.SIZE + 1;
        if (data.Chunk == null || heights == null || heights.Count < cornerStride * cornerStride) {
            data.Mesh.Clear();
            SetChunkVisible(data, false);
            if (data.Collider != null) data.Collider.sharedMesh = null;
            return;
        }

        Vector3 origin = data.MeshObject.transform.position;
        var swMc = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        var meshData = MarchingCubesMesher.Build(data.Chunk, BuildMeshingInput(data, heights, origin));
        long mcTicks = swMc?.ElapsedTicks ?? 0;
        data.Mesh.Clear();

        if (meshData.Vertices.Count == 0) {
            SetChunkVisible(data, false);
            if (data.Collider != null) data.Collider.sharedMesh = null;
            return;
        }

        var cornerBiomes = GetCornerBiomes(data.Heightmap);
        Color32 biome00 = CornerBiomeColor(cornerBiomes, 0);
        Color32 biome10 = CornerBiomeColor(cornerBiomes, 1);
        Color32 biome01 = CornerBiomeColor(cornerBiomes, 2);
        Color32 biome11 = CornerBiomeColor(cornerBiomes, 3);

        var swSamp = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        int vertexCount = meshData.Vertices.Count;
        var verts  = new Vector3[vertexCount];
        var uvs    = new Vector2[vertexCount];
        var colors = new Color32[vertexCount];
        var stats = new MeshStats();
        for (int i = 0; i < vertexCount; i++) {
            var v = meshData.Vertices[i];
            verts[i] = new Vector3(v.X, v.Y, v.Z);
            if (meshData.OnTerrainSurface[i]) stats.Analytic++;

            float uvX = v.X / VoxelChunk.SIZE;
            float uvZ = v.Z / VoxelChunk.SIZE;
            uvs[i] = new Vector2(uvX, uvZ);
            float tx = Mathf.SmoothStep(0f, 1f, uvX);
            float tz = Mathf.SmoothStep(0f, 1f, uvZ);
            Color32 row0 = Color32.Lerp(biome00, biome10, tx);
            Color32 row1 = Color32.Lerp(biome01, biome11, tx);
            colors[i] = Color32.Lerp(row0, row1, tz);
        }
        long sampTicks = swSamp?.ElapsedTicks ?? 0;

        stats.NormalsFromRenderMesh = TryBuildHeightmapNormalLookup(data.Heightmap);
        var normals = BuildVertexNormals(data, heights, meshData, verts, origin, stats.NormalsFromRenderMesh);

        var swFilt = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        var srcIndices = meshData.Indices;
        var submeshes = new SubmeshSorter(data, CollectPaintedBands(data), srcIndices.Count);
        for (int t = 0; t + 2 < srcIndices.Count; t += 3) {
            int i0 = srcIndices[t], i1 = srcIndices[t + 1], i2 = srcIndices[t + 2];
            if (IsCollapsedTriangle(i0, i1, i2)) {
                stats.DroppedDegenerate++;
                continue;
            }
            var target = submeshes.For(
                (verts[i0] + verts[i1] + verts[i2]) / 3f,
                (normals[i0] + normals[i1] + normals[i2]).normalized);
            target.Add(i0);
            target.Add(i1);
            target.Add(i2);
        }
        long filtTicks = swFilt?.ElapsedTicks ?? 0;

        if (submeshes.IsEmpty) {
            SetChunkVisible(data, false);
            if (data.Collider != null) data.Collider.sharedMesh = null;
            DebugLog($"[mesh] chunk={data.Heightmap?.gameObject.name} all triangles filtered, renderer disabled");
            return;
        }

        data.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        data.Mesh.vertices    = verts;
        data.Mesh.uv          = uvs;
        data.Mesh.colors32    = colors;
        submeshes.ApplyTo(data);
        data.Mesh.normals     = normals;
        data.Mesh.RecalculateTangents();
        data.Mesh.RecalculateBounds();
        MarkSurfaceSeamTexels(data, verts, meshData.OnTerrainSurface, submeshes);
        SyncUndergroundMasks(data, submeshes.PaintedBands);

        DebugLog(
            $"[mesh] verts={verts.Length} surfaceTris={submeshes.Surface.Count / 3} undergroundTris={submeshes.UndergroundTriangleCount} paintBands={data.SubmeshPaintBands.Count}");

        if (data.Collider != null) {
            data.Collider.sharedMesh = null;
            data.Collider.sharedMesh = data.Mesh;
        }

        TryApplyTerrainMaterial(data);

        SetChunkVisible(data, true);

        if (VerboseLogs) {
            float toMs(long t) => (float)(t * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            DebugLog($"[perf] RebuildMesh mc={toMs(mcTicks):F1}ms samp={toMs(sampTicks):F1}ms filt={toMs(filtTicks):F1}ms tris={submeshes.Surface.Count / 3}");
            stats.OpenEdges = CountOpenEdges(meshData);
            LogMeshDiagnostics(data, verts, submeshes.Surface, stats);
        }
    }

    private static MeshingInput BuildMeshingInput(ChunkRenderData data, List<float> heights, Vector3 origin) {
        int count = (VoxelChunk.SIZE + 1) * (VoxelChunk.SIZE + 1);
        var local = data.SurfaceLocalY ??= new float[count];
        float shift = data.Heightmap.transform.position.y - origin.y;
        for (int i = 0; i < count; i++) local[i] = heights[i] + shift;
        return new MeshingInput(data.CellReplaced, local);
    }

    private static bool TryBuildHeightmapNormalLookup(Heightmap hm) {
        var mesh = HeightmapRenderMesh(hm);
        if (mesh == null) return false;

        var meshNormals = mesh.normals;
        var meshVerts = mesh.vertices;
        if (meshNormals.Length == 0 || meshNormals.Length != meshVerts.Length) return false;

        int stride = VoxelChunk.SIZE + 1;
        float half = hm.m_width * hm.m_scale * 0.5f;
        float invScale = 1f / hm.m_scale;
        System.Array.Clear(s_heightmapNormals, 0, s_heightmapNormals.Length);

        int filled = 0;
        for (int i = 0; i < meshVerts.Length; i++) {
            int x = Mathf.RoundToInt((meshVerts[i].x + half) * invScale);
            int z = Mathf.RoundToInt((meshVerts[i].z + half) * invScale);
            if (x < 0 || z < 0 || x >= stride || z >= stride) continue;
            int index = z * stride + x;
            if (s_heightmapNormals[index].sqrMagnitude < 0.5f && meshNormals[i].sqrMagnitude >= 0.5f) filled++;
            s_heightmapNormals[index] = meshNormals[i];
        }
        return filled == stride * stride;
    }

    private static Vector3 SampleHeightmapNormalLookup(float x, float z) {
        int stride = VoxelChunk.SIZE + 1;
        x = Mathf.Clamp(x, 0f, VoxelChunk.SIZE);
        z = Mathf.Clamp(z, 0f, VoxelChunk.SIZE);
        int x0 = Mathf.Min((int)x, VoxelChunk.SIZE - 1);
        int z0 = Mathf.Min((int)z, VoxelChunk.SIZE - 1);
        float fx = x - x0, fz = z - z0;
        Vector3 n00 = s_heightmapNormals[z0 * stride + x0];
        Vector3 n10 = s_heightmapNormals[z0 * stride + x0 + 1];
        Vector3 n01 = s_heightmapNormals[(z0 + 1) * stride + x0];
        Vector3 n11 = s_heightmapNormals[(z0 + 1) * stride + x0 + 1];
        Vector3 n = Vector3.Lerp(Vector3.Lerp(n00, n10, fx), Vector3.Lerp(n01, n11, fx), fz);
        return n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.up;
    }

    private static Vector3[] BuildVertexNormals(
        ChunkRenderData data, List<float> heights, MeshData meshData, Vector3[] verts, Vector3 origin, bool useRenderMeshNormals) {
        var normals = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++) {
            Vector3 p = verts[i];
            if (!meshData.OnTerrainSurface[i]) {
                normals[i] = ComputeSdfNormal(data.Chunk, p.x, p.y, p.z);
                continue;
            }

            normals[i] = useRenderMeshNormals
                ? SampleHeightmapNormalLookup(p.x, p.z)
                : SampleHeightmapMeshNormalCached(data.Heightmap, heights, new Vector3(origin.x + p.x, 0f, origin.z + p.z), Vector3.up);
        }
        return normals;
    }

    private static void SetChunkVisible(ChunkRenderData data, bool visible) {
        data.Renderer.enabled = visible;
        if (data.FillLight != null) data.FillLight.enabled = visible;
    }

    private static float SampleHeightmapMeshY(Heightmap hm, Vector3 worldXZ, float fallback) {
        return SampleHeightmapMeshYCached(hm, GetHeightsArray(hm), worldXZ, fallback);
    }

    private static float SampleHeightmapMeshYCached(Heightmap hm, List<float> heights, Vector3 worldXZ, float fallback) {
        if (hm == null) return fallback;

        Vector3 hmPos = hm.transform.position;
        float invScale = 1f / hm.m_scale;
        float xCell = Mathf.Clamp((worldXZ.x - hmPos.x) * invScale + hm.m_width * 0.5f, 0f, hm.m_width);
        float zCell = Mathf.Clamp((worldXZ.z - hmPos.z) * invScale + hm.m_width * 0.5f, 0f, hm.m_width);

        int x0 = Mathf.Min((int)xCell, hm.m_width - 1);
        int z0 = Mathf.Min((int)zCell, hm.m_width - 1);
        float fx = xCell - x0;
        float fz = zCell - z0;

        int width = hm.m_width + 1;
        if (x0 < 0 || z0 < 0 || x0 + 1 >= width || z0 + 1 >= width) return fallback;
        if (heights == null || heights.Count < width * width) return fallback;

        float h00 = heights[z0 * width + x0];
        float h10 = heights[z0 * width + (x0 + 1)];
        float h01 = heights[(z0 + 1) * width + x0];
        float h11 = heights[(z0 + 1) * width + (x0 + 1)];

        float h;
        if (fx + fz <= 1f) {
            h = (1f - fx - fz) * h00 + fz * h01 + fx * h10;
        } else {
            h = (1f - fz) * h10 + (1f - fx) * h01 + (fx + fz - 1f) * h11;
        }

        return h + hmPos.y;
    }

    private static Vector3 SampleHeightmapMeshNormalCached(Heightmap hm, List<float> heights, Vector3 worldXZ, Vector3 fallback) {
        if (hm == null) return fallback;

        float scale = hm.m_scale;
        float yL = SampleHeightmapMeshYCached(hm, heights, new Vector3(worldXZ.x - scale, 0f, worldXZ.z), float.NaN);
        float yR = SampleHeightmapMeshYCached(hm, heights, new Vector3(worldXZ.x + scale, 0f, worldXZ.z), float.NaN);
        float yD = SampleHeightmapMeshYCached(hm, heights, new Vector3(worldXZ.x, 0f, worldXZ.z - scale), float.NaN);
        float yU = SampleHeightmapMeshYCached(hm, heights, new Vector3(worldXZ.x, 0f, worldXZ.z + scale), float.NaN);

        if (float.IsNaN(yL) || float.IsNaN(yR) || float.IsNaN(yD) || float.IsNaN(yU))
            return fallback;

        var n = new Vector3(yL - yR, 2f * scale, yD - yU);
        return n.sqrMagnitude > 1e-6f ? n.normalized : fallback;
    }

    private static float SampleSdfTri(Terraform.Voxel.Core.VoxelChunk chunk, float x, float y, float z) {
        x = Mathf.Clamp(x, 0f, VoxelChunk.SIZE);
        y = Mathf.Clamp(y, 0f, VoxelChunk.SIZE);
        z = Mathf.Clamp(z, 0f, VoxelChunk.SIZE);
        int x0 = Mathf.Min((int)Mathf.Floor(x), VoxelChunk.SIZE - 1);
        int y0 = Mathf.Min((int)Mathf.Floor(y), VoxelChunk.SIZE - 1);
        int z0 = Mathf.Min((int)Mathf.Floor(z), VoxelChunk.SIZE - 1);
        float fx = x - x0, fy = y - y0, fz = z - z0;
        float c000 = chunk.Get(x0,     y0,     z0    ).Value;
        float c100 = chunk.Get(x0 + 1, y0,     z0    ).Value;
        float c010 = chunk.Get(x0,     y0 + 1, z0    ).Value;
        float c110 = chunk.Get(x0 + 1, y0 + 1, z0    ).Value;
        float c001 = chunk.Get(x0,     y0,     z0 + 1).Value;
        float c101 = chunk.Get(x0 + 1, y0,     z0 + 1).Value;
        float c011 = chunk.Get(x0,     y0 + 1, z0 + 1).Value;
        float c111 = chunk.Get(x0 + 1, y0 + 1, z0 + 1).Value;
        float c00 = c000 * (1f - fx) + c100 * fx;
        float c10 = c010 * (1f - fx) + c110 * fx;
        float c01 = c001 * (1f - fx) + c101 * fx;
        float c11 = c011 * (1f - fx) + c111 * fx;
        float c0  = c00  * (1f - fy) + c10  * fy;
        float c1  = c01  * (1f - fy) + c11  * fy;
        return c0 * (1f - fz) + c1 * fz;
    }

    private static Vector3 ComputeSdfNormal(Terraform.Voxel.Core.VoxelChunk chunk, float x, float y, float z) {
        const float h = 1.0f;
        float gx = (SampleSdfTri(chunk, x + h, y, z) - SampleSdfTri(chunk, x - h, y, z)) * CentralSpanRatio(x, h);
        float gy = (SampleSdfTri(chunk, x, y + h, z) - SampleSdfTri(chunk, x, y - h, z)) * CentralSpanRatio(y, h);
        float gz = (SampleSdfTri(chunk, x, y, z + h) - SampleSdfTri(chunk, x, y, z - h)) * CentralSpanRatio(z, h);
        Vector3 grad = new Vector3(gx, gy, gz);
        if (grad.sqrMagnitude < 1e-8f) grad = new Vector3(1e-3f, 1f, 1e-3f);
        var n = grad.normalized;

        const float kAxisGuard = 1e-3f;
        if (Mathf.Abs(n.x) < kAxisGuard && Mathf.Abs(n.z) < kAxisGuard) {
            n.x = kAxisGuard;
            n.z = kAxisGuard;
            n.Normalize();
        }
        return n;
    }

    private static float CentralSpanRatio(float c, float h) {
        if (c - h >= 0f && c + h <= VoxelChunk.SIZE) return 1f;
        float span = Mathf.Min(c + h, VoxelChunk.SIZE) - Mathf.Max(c - h, 0f);
        return span > 1e-6f ? 2f * h / span : 1f;
    }

    private static bool IsCollapsedTriangle(int i0, int i1, int i2) => i0 == i1 || i1 == i2 || i0 == i2;

    private static int CountOpenEdges(MeshData meshData) {
        var indices = meshData.Indices;
        var uses = new Dictionary<long, int>(indices.Count);
        for (int t = 0; t + 2 < indices.Count; t += 3) {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if (IsCollapsedTriangle(i0, i1, i2)) continue;
            CountEdgeUse(i0, i1, uses);
            CountEdgeUse(i1, i2, uses);
            CountEdgeUse(i2, i0, uses);
        }

        int open = 0;
        foreach (var edge in uses) {
            if (edge.Value != 1) continue;
            int a = (int)(edge.Key >> 32), b = (int)(edge.Key & 0xFFFFFFFF);
            if (meshData.OnTerrainSurface[a] || meshData.OnTerrainSurface[b]) continue;
            if (IsOnChunkBorder(meshData.Vertices[a]) && IsOnChunkBorder(meshData.Vertices[b])) continue;
            open++;
        }
        return open;
    }

    private static void CountEdgeUse(int a, int b, Dictionary<long, int> uses) {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        uses.TryGetValue(key, out int count);
        uses[key] = count + 1;
    }

    private static bool IsOnChunkBorder(Vertex3 v) =>
        v.X <= 0f || v.X >= VoxelChunk.SIZE || v.Z <= 0f || v.Z >= VoxelChunk.SIZE ||
        v.Y <= 0f || v.Y >= VoxelChunk.SIZE;

    private static void LogMeshDiagnostics(ChunkRenderData data, Vector3[] verts, List<int> indices, MeshStats stats) {
        var used = new bool[verts.Length];
        for (int i = 0; i < indices.Count; i++) used[indices[i]] = true;

        var normals  = data.Mesh.normals;
        var tangents = data.Mesh.tangents;
        int badN = 0, badT = 0;
        for (int i = 0; i < normals.Length; i++) {
            if (!used[i]) continue;
            if (normals[i].sqrMagnitude < 0.5f)  badN++;
            if (i < tangents.Length && tangents[i].sqrMagnitude < 0.5f) badT++;
        }
        int triCount = indices.Count / 3;
        int degenerate = 0;
        int up = 0, down = 0, side = 0;
        var faceNormals = new Vector3[triCount];
        for (int t = 0; t < triCount; t++) {
            int i0 = indices[t * 3], i1 = indices[t * 3 + 1], i2 = indices[t * 3 + 2];
            Vector3 cross = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
            if (cross.sqrMagnitude < 1e-6f) { degenerate++; continue; }
            Vector3 n = cross.normalized;
            faceNormals[t] = n;
            if      (n.y >  0.5f) up++;
            else if (n.y < -0.5f) down++;
            else                  side++;
        }

        var edgeFace = new Dictionary<long, int>(triCount * 3);
        int folds = 0;
        for (int t = 0; t < triCount; t++) {
            int i0 = indices[t * 3], i1 = indices[t * 3 + 1], i2 = indices[t * 3 + 2];
            CheckEdge(i0, i1, t, edgeFace, faceNormals, ref folds);
            CheckEdge(i1, i2, t, edgeFace, faceNormals, ref folds);
            CheckEdge(i2, i0, t, edgeFace, faceNormals, ref folds);
        }

        string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
        TerraformPlugin.Log.LogInfo(
            $"[mesh] hm={hmName} verts={verts.Length} tris={triCount} analytic={stats.Analytic} " +
            $"dropDegen={stats.DroppedDegenerate} open={stats.OpenEdges} " +
            $"normals={(stats.NormalsFromRenderMesh ? "renderMesh" : "heights")} " +
            $"up={up} down={down} side={side} folds={folds} " +
            $"badNormals={badN} badTangents={badT} degenerateTris={degenerate}");
    }

    private static void CheckEdge(int a, int b, int faceIdx, Dictionary<long, int> edgeFace, Vector3[] faceNormals, ref int folds) {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (edgeFace.TryGetValue(key, out int otherFace)) {
            if (Vector3.Dot(faceNormals[faceIdx], faceNormals[otherFace]) < -0.5f) folds++;
        } else {
            edgeFace[key] = faceIdx;
        }
    }
}
