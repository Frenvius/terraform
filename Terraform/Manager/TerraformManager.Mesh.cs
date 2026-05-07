using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private void RebuildMesh(ChunkRenderData data) {
        var swMc = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        var meshData = Terraform.Voxel.Meshing.MarchingCubesMesher.Build(data.Chunk);
        long mcTicks = swMc?.ElapsedTicks ?? 0;
        data.Mesh.Clear();

        if (meshData.Vertices.Count == 0) {
            data.Renderer.enabled = false;
            if (data.Collider != null) data.Collider.sharedMesh = null;
            return;
        }

        Vector3 origin = data.MeshObject.transform.position;
        var heights = GetHeightsArray(data.Heightmap);
        var cornerBiomes = GetCornerBiomes(data.Heightmap);
        Color32 biome00 = cornerBiomes != null ? GetBiomeColorReflected(cornerBiomes.GetValue(0)) : new Color32(0, 0, 0, 0);
        Color32 biome10 = cornerBiomes != null ? GetBiomeColorReflected(cornerBiomes.GetValue(1)) : new Color32(0, 0, 0, 0);
        Color32 biome01 = cornerBiomes != null ? GetBiomeColorReflected(cornerBiomes.GetValue(2)) : new Color32(0, 0, 0, 0);
        Color32 biome11 = cornerBiomes != null ? GetBiomeColorReflected(cornerBiomes.GetValue(3)) : new Color32(0, 0, 0, 0);

        var swSamp = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        var verts  = new Vector3[meshData.Vertices.Count];
        var uvs    = new Vector2[meshData.Vertices.Count];
        var colors = new Color32[meshData.Vertices.Count];
        var terrainNormals = new Vector3[meshData.Vertices.Count];
        var aboveTerrain = new bool[meshData.Vertices.Count];
        var offsetAboveTerrain = new float[meshData.Vertices.Count];
        float maxOffset = float.NegativeInfinity;
        int aboveTerrainCount = 0;
        for (int i = 0; i < verts.Length; i++) {
            var v = meshData.Vertices[i];
            var worldXZ = new Vector3(origin.x + v.X, 0f, origin.z + v.Z);

            float terrainY = SampleHeightmapMeshYCached(data.Heightmap, heights, worldXZ, data.TerrainHeight);
            terrainNormals[i] = SampleHeightmapMeshNormalCached(data.Heightmap, heights, worldXZ, Vector3.up);

            float localTerrainY = terrainY - origin.y;
            float offset        = v.Y - localTerrainY;
            offsetAboveTerrain[i] = offset;
            if (offset > maxOffset) maxOffset = offset;
            aboveTerrain[i]     = v.Y > localTerrainY;
            if (aboveTerrain[i]) aboveTerrainCount++;

            float clampedY;
            if (Mathf.Abs(offset) < ShallowAboveKeep) {
                var heightmapState = ClassifyPoint(data.Heightmap,
                    new Vector3(worldXZ.x, terrainY, worldXZ.z));
                clampedY = heightmapState == CellState.Intact
                    ? localTerrainY - SeamInset
                    : localTerrainY;
            } else {
                clampedY = Mathf.Min(v.Y, localTerrainY);
            }
            verts[i] = new Vector3(v.X, clampedY, v.Z);

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

        var swFilt = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        var srcIndices = meshData.Indices;
        var filteredIndices = new List<int>(srcIndices.Count);
        int droppedChimney = 0, droppedDegenerate = 0;
        int keptRimRecovered  = 0;
        int dropChimneyAllOutside = 0;
        int dropChimneyAllInside  = 0;
        int dropAboveTinyEps      = 0;
        int droppedOutsideFootprint = 0;
        int droppedSurfaceOverlap = 0;
        const int kMaxChimneySamples = 15;
        const float kNearSurfaceDepth = 0.18f;
        var chimneyDropSamples = new List<(int i0, int i1, int i2, bool in0, bool in1, bool in2)>(kMaxChimneySamples);
        for (int t = 0; t + 2 < srcIndices.Count; t += 3) {
            int i0 = srcIndices[t], i1 = srcIndices[t + 1], i2 = srcIndices[t + 2];
            if (aboveTerrain[i0] && aboveTerrain[i1] && aboveTerrain[i2]) {
                float maxAbove = Mathf.Max(offsetAboveTerrain[i0], Mathf.Max(offsetAboveTerrain[i1], offsetAboveTerrain[i2]));
                if (maxAbove > ShallowAboveKeep) {
                    droppedChimney++;
                    if (offsetAboveTerrain[i0] < 0.01f && offsetAboveTerrain[i1] < 0.01f && offsetAboveTerrain[i2] < 0.01f)
                        dropAboveTinyEps++;
                    if (chimneyDropSamples.Count < kMaxChimneySamples)
                        chimneyDropSamples.Add((i0, i1, i2, false, false, false));
                    continue;
                }
                keptRimRecovered++;
            }
            Vector3 a = verts[i0], b = verts[i1], c = verts[i2];
            if (!TriangleOverlapsRenderFootprint(data, origin, a, b, c)) {
                droppedOutsideFootprint++;
                if (chimneyDropSamples.Count < kMaxChimneySamples)
                    chimneyDropSamples.Add((i0, i1, i2, false, false, false));
                continue;
            }
            float offA = offsetAboveTerrain[i0], offB = offsetAboveTerrain[i1], offC = offsetAboveTerrain[i2];
            bool triangleNearOrAboveSurface =
                offA >= -kNearSurfaceDepth && offB >= -kNearSurfaceDepth && offC >= -kNearSurfaceDepth;
            if (triangleNearOrAboveSurface && IsSurfaceOverlayOnIntactHeightmap(data, origin, a, b, c, offA, offB, offC)) {
                droppedSurfaceOverlap++;
                if (chimneyDropSamples.Count < kMaxChimneySamples)
                    chimneyDropSamples.Add((i0, i1, i2, false, false, false));
                continue;
            }
            if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-6f) {
                droppedDegenerate++;
                continue;
            }
            filteredIndices.Add(i0);
            filteredIndices.Add(i1);
            filteredIndices.Add(i2);
        }
        long filtTicks = swFilt?.ElapsedTicks ?? 0;

        if (filteredIndices.Count == 0) {
            data.Renderer.enabled = false;
            if (data.Collider != null) data.Collider.sharedMesh = null;
            DebugLog($"[mesh] chunk={data.Heightmap?.gameObject.name} all triangles filtered → renderer disabled");
            return;
        }

        const float kShelfBias          = 0f;
        const float kShelfDepthRange    = 1.75f;
        const float kTerrainNormalBlend = 1.0f;
        var sdfNormals = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++) {
            var v = meshData.Vertices[i];
            Vector3 n = ComputeSdfNormal(data.Chunk, v.X, v.Y, v.Z);
            float depthBelow = -offsetAboveTerrain[i];
            if (depthBelow < kShelfDepthRange && offsetAboveTerrain[i] < 0.25f) {
                depthBelow = Mathf.Max(0f, depthBelow);
                float falloff = 1f - depthBelow / kShelfDepthRange;
                Vector3 terrainN = terrainNormals[i];
                if (terrainN.sqrMagnitude > 0.5f) {
                    n = Vector3.Slerp(n, terrainN.normalized, kTerrainNormalBlend * falloff);
                    n.Normalize();
                }
                n.y += kShelfBias * falloff;
                n.Normalize();
            }
            sdfNormals[i] = n;
        }

        var allVerts    = verts;
        var allUvs      = uvs;
        var allColors   = colors;
        var allNormals  = sdfNormals;

        data.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        data.Mesh.vertices    = allVerts;
        data.Mesh.uv          = allUvs;
        data.Mesh.colors32    = allColors;
        data.Mesh.subMeshCount = 1;
        data.Mesh.SetTriangles(filteredIndices, 0, calculateBounds: false);
        data.Mesh.normals     = allNormals;
        data.Mesh.RecalculateTangents();
        data.Mesh.RecalculateBounds();

        DebugLog(
            $"[mesh] verts={allVerts.Length} totalTris={filteredIndices.Count / 3}");

        if (data.Collider != null) {
            data.Collider.sharedMesh = null;
            data.Collider.sharedMesh = data.Mesh;
        }

        TryApplyTerrainMaterial(data);

        data.Renderer.enabled = true;

        if (VerboseLogs) {
            float toMs(long t) => (float)(t * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            DebugLog($"[perf] RebuildMesh mc={toMs(mcTicks):F1}ms samp={toMs(sampTicks):F1}ms filt={toMs(filtTicks):F1}ms tris={filteredIndices.Count / 3}");
        }

        if (VerboseLogs) {
            LogMeshDiagnostics(data, allVerts, filteredIndices,
                droppedChimney, droppedDegenerate, keptRimRecovered,
                dropChimneyAllOutside, dropChimneyAllInside, dropAboveTinyEps,
                droppedOutsideFootprint, droppedSurfaceOverlap,
                offsetAboveTerrain, aboveTerrainCount, maxOffset,
                chimneyDropSamples, origin);
        }
    }

    private static bool TriangleOverlapsRenderFootprint(
        ChunkRenderData data,
        Vector3 origin,
        Vector3 a,
        Vector3 b,
        Vector3 c) {
        if (data.RenderFootprints.Count == 0) return true;

        float minX = origin.x + Mathf.Min(a.x, Mathf.Min(b.x, c.x));
        float maxX = origin.x + Mathf.Max(a.x, Mathf.Max(b.x, c.x));
        float minZ = origin.z + Mathf.Min(a.z, Mathf.Min(b.z, c.z));
        float maxZ = origin.z + Mathf.Max(a.z, Mathf.Max(b.z, c.z));

        const float eps = 0.02f;
        for (int i = 0; i < data.RenderFootprints.Count; i++) {
            var fp = data.RenderFootprints[i];
            if (maxX < fp.MinX - eps || minX > fp.MaxX + eps) continue;
            if (maxZ < fp.MinZ - eps || minZ > fp.MaxZ + eps) continue;
            return true;
        }

        return false;
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
        int x0 = (int)Mathf.Floor(x);
        int y0 = (int)Mathf.Floor(y);
        int z0 = (int)Mathf.Floor(z);
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
        float gx = SampleSdfTri(chunk, x + h, y, z) - SampleSdfTri(chunk, x - h, y, z);
        float gy = SampleSdfTri(chunk, x, y + h, z) - SampleSdfTri(chunk, x, y - h, z);
        float gz = SampleSdfTri(chunk, x, y, z + h) - SampleSdfTri(chunk, x, y, z - h);
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

    private static void LogMeshDiagnostics(ChunkRenderData data, Vector3[] verts, List<int> indices,
                                            int droppedChimney, int droppedDegenerate, int keptRimRecovered,
                                            int dropChimneyAllOutside, int dropChimneyAllInside, int dropAboveTinyEps,
                                            int droppedOutsideFootprint, int droppedSurfaceOverlap,
                                            float[] offsetAboveTerrain, int aboveTerrainCount, float maxOffset,
                                            List<(int i0, int i1, int i2, bool in0, bool in1, bool in2)> chimneyDropSamples, Vector3 origin) {
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
            $"[mesh] hm={hmName} verts={verts.Length} tris={triCount} " +
            $"dropChimney={droppedChimney}(tinyEps={dropAboveTinyEps}) " +
            $"dropOutsideFootprint={droppedOutsideFootprint} " +
            $"dropSurfaceOverlap={droppedSurfaceOverlap} " +
            $"dropDegen={droppedDegenerate} " +
            $"aboveTerrain={aboveTerrainCount}/{verts.Length} maxOffset={maxOffset:F4}u " +
            $"up={up} down={down} side={side} folds={folds} " +
            $"badNormals={badN} badTangents={badT} degenerateTris={degenerate}");

        for (int s = 0; s < chimneyDropSamples.Count; s++) {
            var (i0, i1, i2, _, _, _) = chimneyDropSamples[s];
            Vector3 w0 = verts[i0] + origin;
            Vector3 w1 = verts[i1] + origin;
            Vector3 w2 = verts[i2] + origin;
            TerraformPlugin.Log.LogInfo(
                $"[mesh]   drop#{s} " +
                $"w=({w0.x:F3},{w0.y:F3},{w0.z:F3})/({w1.x:F3},{w1.y:F3},{w1.z:F3})/({w2.x:F3},{w2.y:F3},{w2.z:F3}) " +
                $"above=({offsetAboveTerrain[i0]:F4},{offsetAboveTerrain[i1]:F4},{offsetAboveTerrain[i2]:F4})");
        }
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
