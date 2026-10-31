using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private bool _propagatingFlood;
    private static readonly HashSet<(int x, int y)> NoDryCells = new();

    public bool IsWaterUnderOriginalGround(Vector3 point, float waterSurface) =>
        HasDigsAround(point) && OriginalHeight(point, out float terrainY) && terrainY > waterSurface
        && !IsInOpenWater(point);

    private void RebuildWaterMeshFromHoles(WaterRenderData data) {
        if (data == null) return;
        var original = data.OriginalSharedMesh;
        var cloned = data.ClonedMesh;
        var filter = data.SurfaceFilter;
        var hm = data.Heightmap;
        if (original == null || cloned == null || filter == null || hm == null) return;

        var cutter = new WaterSurfaceCutter(original, filter.transform);
        var originalIndices = original.GetIndices(0);
        float halfSize = hm.m_width * hm.m_scale * 0.5f;
        Vector3 hmPos = hm.transform.position;
        float cellOriginX = hmPos.x - halfSize;
        float cellOriginZ = hmPos.z - halfSize;
        const float invStep = 1f / PlaneStep;

        var dry = _chunks.TryGetValue(hm.GetInstanceID(), out var chunk) ? chunk?.DryWaterCells : null;
        dry ??= NoDryCells;
        int clipped = 0;
        for (int i = 0; i + 2 < originalIndices.Length; i += 3) {
            int ia = originalIndices[i];
            int ib = originalIndices[i + 1];
            int ic = originalIndices[i + 2];
            if (dry.Count == 0) {
                cutter.Keep(ia, ib, ic);
                continue;
            }

            Vector3 wa = cutter.WorldVertex(ia);
            Vector3 wb = cutter.WorldVertex(ib);
            Vector3 wc = cutter.WorldVertex(ic);
            int minX = Mathf.FloorToInt((Mathf.Min(wa.x, wb.x, wc.x) - cellOriginX) * invStep);
            int maxX = Mathf.FloorToInt((Mathf.Max(wa.x, wb.x, wc.x) - cellOriginX) * invStep);
            int minZ = Mathf.FloorToInt((Mathf.Min(wa.z, wb.z, wc.z) - cellOriginZ) * invStep);
            int maxZ = Mathf.FloorToInt((Mathf.Max(wa.z, wb.z, wc.z) - cellOriginZ) * invStep);

            if (!AnyHoleInCellRange(dry, minX, maxX, minZ, maxZ)) {
                cutter.Keep(ia, ib, ic);
                continue;
            }

            clipped++;
            cutter.BeginTriangle(ia, ib, ic);
            for (int z = minZ; z <= maxZ; z++) {
                float z0 = cellOriginZ + z * PlaneStep;
                for (int x = minX; x <= maxX;) {
                    if (dry.Contains((x, z))) {
                        x++;
                        continue;
                    }
                    int start = x;
                    while (x <= maxX && !dry.Contains((x, z))) x++;
                    cutter.AddPieceInside(cellOriginX + start * PlaneStep, z0, cellOriginX + x * PlaneStep, z0 + PlaneStep);
                }
            }
        }
        cutter.ApplyTo(cloned, original.bounds);

        if (dry.Count > 0) DebugLog($"[water-punch] hm={hm.name} dry={dry.Count} clipped={clipped} tris={cutter.TriangleCount}");
    }

    private void PropagateFloodChanges() {
        if (_propagatingFlood || _floodChangedZones.Count == 0) return;
        _propagatingFlood = true;
        try {
            var zones = new List<int>(_floodChangedZones);
            _floodChangedZones.Clear();
            foreach (int key in zones) {
                if (_chunks.TryGetValue(key, out var chunk) && chunk != null) RebuildMesh(chunk);
            }
        } finally {
            _floodChangedZones.Clear();
            _propagatingFlood = false;
        }
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

        public WaterSurfaceCutter(Mesh original, Transform waterT) {
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
