using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float WaterSinkDepth = 4f;
    private const float WaterSinkRadius = 6f;
    private const float WaterBandScanStep = 0.5f;

    public bool IsWaterUnderOriginalGround(Vector3 point, float waterSurface) =>
        HasDigsAround(point) && OriginalHeight(point, out float terrainY) && terrainY > waterSurface;

    private static float ComputeWaterSink(WaterRenderData data, Vector3 world) {
        if (data.SinkField is not { } field) return 0f;
        float falloff = WaterSinkFalloff(field.DistanceTo(world.x, world.z, WaterSinkRadius));
        if (falloff <= 0f) return 0f;
        float waterY = data.SurfaceFilter.transform.position.y;
        float terrainY = SampleHeightmapMeshYCached(data.Heightmap, GetHeightsArray(data.Heightmap), world, waterY);
        return falloff * WaterSinkDepth * Mathf.Clamp01(terrainY - waterY);
    }

    private static float WaterSinkFalloff(float distance) =>
        distance >= WaterSinkRadius ? 0f : 1f - Mathf.SmoothStep(0f, 1f, distance / WaterSinkRadius);

    private void RebuildWaterMeshFromHoles(WaterRenderData data) {
        if (data == null) return;
        var original = data.OriginalSharedMesh;
        var cloned = data.ClonedMesh;
        var filter = data.SurfaceFilter;
        var hm = data.Heightmap;
        if (original == null || cloned == null || filter == null || hm == null) return;

        var cutter = new WaterSurfaceCutter(original, filter.transform);
        var originalIndices = original.GetIndices(0);

        float scale = hm.m_scale;
        float halfSize = hm.m_width * scale * 0.5f;
        Vector3 hmPos = hm.transform.position;
        float cellOriginX = hmPos.x - halfSize;
        float cellOriginZ = hmPos.z - halfSize;
        float invScale = 1f / scale;

        var holes = CollectWaterOpenCells(data, new Vector3(cellOriginX, filter.transform.position.y, cellOriginZ));
        data.SinkField = null;
        if (holes.Count == 0) {
            for (int i = 0; i + 2 < originalIndices.Length; i += 3)
                cutter.Keep(originalIndices[i], originalIndices[i + 1], originalIndices[i + 2]);
            cutter.ApplyTo(cloned, original.bounds);
            return;
        }

        int clipped = 0;

        for (int i = 0; i + 2 < originalIndices.Length; i += 3) {
            int ia = originalIndices[i];
            int ib = originalIndices[i + 1];
            int ic = originalIndices[i + 2];

            Vector3 wa = cutter.WorldVertex(ia);
            Vector3 wb = cutter.WorldVertex(ib);
            Vector3 wc = cutter.WorldVertex(ic);

            int minX = Mathf.FloorToInt((Mathf.Min(wa.x, wb.x, wc.x) - cellOriginX) * invScale);
            int maxX = Mathf.FloorToInt((Mathf.Max(wa.x, wb.x, wc.x) - cellOriginX) * invScale);
            int minZ = Mathf.FloorToInt((Mathf.Min(wa.z, wb.z, wc.z) - cellOriginZ) * invScale);
            int maxZ = Mathf.FloorToInt((Mathf.Max(wa.z, wb.z, wc.z) - cellOriginZ) * invScale);

            if (!AnyHoleInCellRange(holes, minX, maxX, minZ, maxZ)) {
                cutter.Keep(ia, ib, ic);
                continue;
            }

            clipped++;
            cutter.BeginTriangle(ia, ib, ic);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++) {
                if (holes.Contains((x, z))) continue;
                float x0 = cellOriginX + x * scale;
                float z0 = cellOriginZ + z * scale;
                cutter.AddPieceInside(x0, z0, x0 + scale, z0 + scale);
            }
        }

        var field = new OpenCellField(holes, cellOriginX, cellOriginZ, scale);
        data.SinkField = field;
        int sunk = cutter.SinkVertices(world => ComputeWaterSink(data, world));
        var bounds = original.bounds;
        bounds.Encapsulate(bounds.min - Vector3.up * cutter.LocalSinkDepth);
        cutter.ApplyTo(cloned, bounds);

        DebugLog($"[water-punch] hm={hm.name} holes={holes.Count} clipped={clipped} sunk={sunk} tris={cutter.TriangleCount}");
    }

    public readonly struct OpenCellField {
        private readonly HashSet<(int x, int y)> _cells;
        private readonly float _originX;
        private readonly float _originZ;
        private readonly float _scale;
        private readonly int _minX, _maxX, _minZ, _maxZ;

        public OpenCellField(HashSet<(int x, int y)> cells, float originX, float originZ, float scale) {
            _cells = cells;
            _originX = originX;
            _originZ = originZ;
            _scale = scale;
            _minX = _minZ = int.MaxValue;
            _maxX = _maxZ = int.MinValue;
            foreach (var c in cells) {
                if (c.x < _minX) _minX = c.x;
                if (c.x > _maxX) _maxX = c.x;
                if (c.y < _minZ) _minZ = c.y;
                if (c.y > _maxZ) _maxZ = c.y;
            }
        }

        public float DistanceTo(float worldX, float worldZ, float maxDistance) {
            int reach = Mathf.CeilToInt(maxDistance / _scale);
            int cx = Mathf.FloorToInt((worldX - _originX) / _scale);
            int cz = Mathf.FloorToInt((worldZ - _originZ) / _scale);
            int x0 = Mathf.Max(cx - reach, _minX), x1 = Mathf.Min(cx + reach, _maxX);
            int z0 = Mathf.Max(cz - reach, _minZ), z1 = Mathf.Min(cz + reach, _maxZ);

            float bestSq = maxDistance * maxDistance;
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++) {
                if (!_cells.Contains((x, z))) continue;
                float minX = _originX + x * _scale;
                float minZ = _originZ + z * _scale;
                float dx = Mathf.Max(minX - worldX, 0f, worldX - (minX + _scale));
                float dz = Mathf.Max(minZ - worldZ, 0f, worldZ - (minZ + _scale));
                float dSq = dx * dx + dz * dz;
                if (dSq < bestSq) bestSq = dSq;
            }
            return Mathf.Sqrt(bestSq);
        }
    }

    private HashSet<(int x, int y)> CollectWaterOpenCells(WaterRenderData data, Vector3 cellOrigin) {
        var open = new HashSet<(int x, int y)>();
        var hm = data.Heightmap;
        if (!_chunks.TryGetValue(hm.GetInstanceID(), out var chunk) || chunk?.Chunk == null) return open;

        var heights = GetHeightsArray(hm);
        float scale = hm.m_scale;
        float waterY = cellOrigin.y;
        Vector3 localOrigin = cellOrigin - chunk.MeshObject.transform.position;
        int bandMin = Mathf.Max(0, Mathf.FloorToInt(localOrigin.y - WaterSinkDepth));
        int bandMax = Mathf.Min(VoxelChunk.SIZE, Mathf.CeilToInt(localOrigin.y));
        if (bandMin > bandMax) return open;

        for (int z = 0; z < VoxelChunk.SIZE; z++)
        for (int x = 0; x < VoxelChunk.SIZE; x++) {
            if (!CellHasAlteredInBand(chunk.Chunk, x, z, bandMin, bandMax)) continue;
            var center = new Vector3(cellOrigin.x + (x + 0.5f) * scale, 0f, cellOrigin.z + (z + 0.5f) * scale);
            if (SampleHeightmapMeshYCached(hm, heights, center, waterY) <= waterY) continue;
            var cellMin = new Vector3(localOrigin.x + x * scale, localOrigin.y, localOrigin.z + z * scale);
            if (WaterBandOpenInCell(chunk.Chunk, cellMin, scale)) open.Add((x, z));
        }
        return open;
    }

    private static bool CellHasAlteredInBand(VoxelChunk chunk, int cx, int cz, int yMin, int yMax) {
        for (int y = yMin; y <= yMax; y++) {
            if (chunk.IsAltered(cx, y, cz) || chunk.IsAltered(cx + 1, y, cz)
                || chunk.IsAltered(cx, y, cz + 1) || chunk.IsAltered(cx + 1, y, cz + 1)) return true;
        }
        return false;
    }

    private static bool WaterBandOpenInCell(VoxelChunk chunk, Vector3 localCellMin, float size) {
        float top = Mathf.Min(localCellMin.y, VoxelChunk.SIZE);
        float bottom = Mathf.Max(localCellMin.y - WaterSinkDepth, 0f);
        for (float y = top; y >= bottom; y -= WaterBandScanStep) {
            if (WaterPlaneOpenInCell(chunk, localCellMin.x, y, localCellMin.z, size)) return true;
        }
        return false;
    }

    private static bool WaterPlaneOpenInCell(VoxelChunk chunk, float minX, float y, float minZ, float size) {
        float half = size * 0.5f;
        return IsVoxelAirAt(chunk, minX + half, y, minZ + half)
            || IsVoxelAirAt(chunk, minX,        y, minZ)
            || IsVoxelAirAt(chunk, minX + size, y, minZ)
            || IsVoxelAirAt(chunk, minX,        y, minZ + size)
            || IsVoxelAirAt(chunk, minX + size, y, minZ + size);
    }

    private static bool IsVoxelAirAt(VoxelChunk chunk, float x, float y, float z) {
        int size = VoxelChunk.SIZE;
        if (x < 0f || x > size || z < 0f || z > size) return false;
        return SampleSdfTri(chunk, x, y, z) > 0f;
    }

    private static bool AnyHoleInCellRange(HashSet<(int x, int y)> holes, int minX, int maxX, int minZ, int maxZ) {
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++) {
            if (holes.Contains((x, z))) return true;
        }
        return false;
    }

    private sealed class WaterSurfaceCutter {
        private const float MinPieceArea = 1e-4f;

        private readonly Transform _waterT;

        private readonly Vector3[] _srcVerts;
        private readonly Vector3[] _srcNormals;
        private readonly Vector4[] _srcTangents;
        private readonly Vector2[] _srcUv;
        private readonly Vector2[] _srcUv2;
        private readonly Color[] _srcColors;
        private readonly Vector3[] _worldVerts;

        private readonly List<Vector3> _verts;
        private readonly List<Vector3> _normals;
        private readonly List<Vector4> _tangents;
        private readonly List<Vector2> _uv;
        private readonly List<Vector2> _uv2;
        private readonly List<Color> _colors;
        private readonly List<int> _indices = new();

        private readonly List<Vector3> _poly = new(8);
        private readonly List<Vector3> _scratch = new(8);
        private int _ia, _ib, _ic;

        public int TriangleCount => _indices.Count / 3;

        public float LocalSinkDepth => WaterSinkDepth / Mathf.Max(_waterT.lossyScale.y, 1e-4f);

        public WaterSurfaceCutter(Mesh original, Transform waterT) {
            _waterT      = waterT;
            _srcVerts    = original.vertices;
            _srcNormals  = original.normals;
            _srcTangents = original.tangents;
            _srcUv       = original.uv;
            _srcUv2      = original.uv2;
            _srcColors   = original.colors;

            _worldVerts = new Vector3[_srcVerts.Length];
            for (int i = 0; i < _srcVerts.Length; i++)
                _worldVerts[i] = waterT.TransformPoint(_srcVerts[i]);

            _verts    = new List<Vector3>(_srcVerts);
            _normals  = new List<Vector3>(_srcNormals);
            _tangents = new List<Vector4>(_srcTangents);
            _uv       = new List<Vector2>(_srcUv);
            _uv2      = new List<Vector2>(_srcUv2);
            _colors   = new List<Color>(_srcColors);
        }

        public Vector3 WorldVertex(int index) => _worldVerts[index];

        public void Keep(int ia, int ib, int ic) {
            _indices.Add(ia);
            _indices.Add(ib);
            _indices.Add(ic);
        }

        public void BeginTriangle(int ia, int ib, int ic) {
            _ia = ia;
            _ib = ib;
            _ic = ic;
        }

        public void AddPieceInside(float minX, float minZ, float maxX, float maxZ) {
            _poly.Clear();
            _poly.Add(new Vector3(1f, 0f, 0f));
            _poly.Add(new Vector3(0f, 1f, 0f));
            _poly.Add(new Vector3(0f, 0f, 1f));

            ClipAxis(0, minX, 1f);
            ClipAxis(0, maxX, -1f);
            ClipAxis(2, minZ, 1f);
            ClipAxis(2, maxZ, -1f);
            if (_poly.Count < 3 || PolygonArea() < MinPieceArea) return;

            int first = AddInterpolated(_poly[0]);
            int prev = AddInterpolated(_poly[1]);
            for (int i = 2; i < _poly.Count; i++) {
                int next = AddInterpolated(_poly[i]);
                Keep(first, prev, next);
                prev = next;
            }
        }

        public int SinkVertices(System.Func<Vector3, float> worldSinkAt) {
            float invScaleY = 1f / Mathf.Max(_waterT.lossyScale.y, 1e-4f);
            int sunk = 0;
            for (int i = 0; i < _verts.Count; i++) {
                float sink = worldSinkAt(_waterT.TransformPoint(_verts[i]));
                if (sink <= 0f) continue;
                Vector3 v = _verts[i];
                v.y -= sink * invScaleY;
                _verts[i] = v;
                sunk++;
            }
            return sunk;
        }

        public void ApplyTo(Mesh mesh, Bounds bounds) {
            mesh.Clear();
            mesh.indexFormat = _verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(_verts);
            if (_normals.Count  == _verts.Count) mesh.SetNormals(_normals);
            if (_tangents.Count == _verts.Count) mesh.SetTangents(_tangents);
            if (_uv.Count       == _verts.Count) mesh.SetUVs(0, _uv);
            if (_uv2.Count      == _verts.Count) mesh.SetUVs(1, _uv2);
            if (_colors.Count   == _verts.Count) mesh.SetColors(_colors);
            mesh.SetIndices(_indices, MeshTopology.Triangles, 0);
            mesh.bounds = bounds;
        }

        private void ClipAxis(int axis, float bound, float keepSign) {
            _scratch.Clear();
            int count = _poly.Count;
            for (int i = 0; i < count; i++) {
                Vector3 cur = _poly[i];
                Vector3 nxt = _poly[(i + 1) % count];
                float dCur = keepSign * (WorldCoord(cur, axis) - bound);
                float dNxt = keepSign * (WorldCoord(nxt, axis) - bound);
                if (dCur >= 0f) _scratch.Add(cur);
                if ((dCur >= 0f) != (dNxt >= 0f))
                    _scratch.Add(Vector3.Lerp(cur, nxt, dCur / (dCur - dNxt)));
            }
            _poly.Clear();
            _poly.AddRange(_scratch);
        }

        private float WorldCoord(Vector3 bary, int axis) =>
            _worldVerts[_ia][axis] * bary.x + _worldVerts[_ib][axis] * bary.y + _worldVerts[_ic][axis] * bary.z;

        private float PolygonArea() {
            float twiceArea = 0f;
            for (int i = 0; i < _poly.Count; i++) {
                Vector3 p = _poly[i];
                Vector3 q = _poly[(i + 1) % _poly.Count];
                twiceArea += WorldCoord(p, 0) * WorldCoord(q, 2) - WorldCoord(q, 0) * WorldCoord(p, 2);
            }
            return Mathf.Abs(twiceArea) * 0.5f;
        }

        private int AddInterpolated(Vector3 bary) {
            _verts.Add(Blend(_srcVerts[_ia], _srcVerts[_ib], _srcVerts[_ic], bary));
            if (_srcNormals.Length > 0)
                _normals.Add(Blend(_srcNormals[_ia], _srcNormals[_ib], _srcNormals[_ic], bary).normalized);
            if (_srcTangents.Length > 0) {
                Vector4 t = _srcTangents[_ia] * bary.x + _srcTangents[_ib] * bary.y + _srcTangents[_ic] * bary.z;
                _tangents.Add(new Vector4(t.x, t.y, t.z, _srcTangents[_ia].w));
            }
            if (_srcUv.Length > 0)
                _uv.Add(_srcUv[_ia] * bary.x + _srcUv[_ib] * bary.y + _srcUv[_ic] * bary.z);
            if (_srcUv2.Length > 0)
                _uv2.Add(_srcUv2[_ia] * bary.x + _srcUv2[_ib] * bary.y + _srcUv2[_ic] * bary.z);
            if (_srcColors.Length > 0)
                _colors.Add(_srcColors[_ia] * bary.x + _srcColors[_ib] * bary.y + _srcColors[_ic] * bary.z);
            return _verts.Count - 1;
        }

        private static Vector3 Blend(Vector3 a, Vector3 b, Vector3 c, Vector3 bary) =>
            a * bary.x + b * bary.y + c * bary.z;
    }
}
