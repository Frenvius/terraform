using System.Collections.Generic;
using Terraform.Voxel.Core;

namespace Terraform.Voxel.Meshing;

public static class MarchingCubesMesher {
    private static readonly float[] _vals = new float[8];
    private static readonly Vertex3[] _edgeVerts = new Vertex3[12];
    private static readonly bool[] _edgeAnalytic = new bool[12];
    private static readonly bool[] _cornerAltered = new bool[8];

    public static MeshData Build(VoxelChunk chunk, MeshingInput input) {
        var data = new MeshData();
        var vertCache = new Dictionary<long, int>();

        for (int z = 0; z < VoxelChunk.SIZE; z++)
        for (int y = 0; y < VoxelChunk.SIZE; y++)
        for (int x = 0; x < VoxelChunk.SIZE; x++)
            ProcessCell(chunk, input, x, y, z, data, vertCache);

        return data;
    }

    private static void ProcessCell(
        VoxelChunk chunk, MeshingInput input, int cx, int cy, int cz,
        MeshData data, Dictionary<long, int> cache) {

        var corners = MarchingCubesTables.Corners;
        bool anyAltered = false;
        for (int i = 0; i < 8; i++) {
            var (ox, oy, oz) = corners[i];
            _vals[i] = chunk.Get(cx + ox, cy + oy, cz + oz).Value;
            _cornerAltered[i] = chunk.IsAltered(cx + ox, cy + oy, cz + oz);
            anyAltered |= _cornerAltered[i];
        }

        int cubeIndex = 0;
        for (int i = 0; i < 8; i++)
            if (_vals[i] < 0f) cubeIndex |= (1 << i);

        int edgeMask = MarchingCubesTables.EdgeTable[cubeIndex];
        if (edgeMask == 0) return;
        if (!anyAltered && !input.CellReplaced[cz * MeshingInput.CellStride + cx]) return;

        var edges = MarchingCubesTables.Edges;
        for (int e = 0; e < 12; e++) {
            if ((edgeMask & (1 << e)) == 0) continue;
            var (a, b) = edges[e];
            bool analytic = !_cornerAltered[a] && !_cornerAltered[b];
            _edgeAnalytic[e] = analytic;
            _edgeVerts[e] = analytic
                ? AnalyticEdgeVertex(input, cx, cy, cz, corners[a], corners[b])
                : InterpolatedEdgeVertex(cx, cy, cz, corners[a], corners[b], _vals[a], _vals[b]);
        }

        var triTable = MarchingCubesTables.TriTable;
        for (int t = 0; triTable[cubeIndex, t] != -1; t += 3) {
            int e0 = triTable[cubeIndex, t];
            int e1 = triTable[cubeIndex, t + 1];
            int e2 = triTable[cubeIndex, t + 2];

            data.Indices.Add(GetOrAddVertex(_edgeVerts[e0], _edgeAnalytic[e0], data, cache));
            data.Indices.Add(GetOrAddVertex(_edgeVerts[e2], _edgeAnalytic[e2], data, cache));
            data.Indices.Add(GetOrAddVertex(_edgeVerts[e1], _edgeAnalytic[e1], data, cache));
        }
    }

    private static Vertex3 InterpolatedEdgeVertex(
        int cx, int cy, int cz, (int x, int y, int z) a, (int x, int y, int z) b, float valueA, float valueB) {
        float t = valueA / (valueA - valueB);
        float vx = (float)System.Math.Round((cx + a.x + t * (b.x - a.x)) * 100.0) / 100f;
        float vy = (float)System.Math.Round((cy + a.y + t * (b.y - a.y)) * 100.0) / 100f;
        float vz = (float)System.Math.Round((cz + a.z + t * (b.z - a.z)) * 100.0) / 100f;
        return new Vertex3(vx, vy, vz);
    }

    private static Vertex3 AnalyticEdgeVertex(
        MeshingInput input, int cx, int cy, int cz, (int x, int y, int z) a, (int x, int y, int z) b) {
        int ax = cx + a.x, az = cz + a.z;
        int bx = cx + b.x, bz = cz + b.z;

        if (ax == bx && az == bz)
            return new Vertex3(ax, input.SurfaceLocalY[az * MeshingInput.CornerStride + ax], az);

        float level = cy + a.y;
        float ha = input.SurfaceLocalY[az * MeshingInput.CornerStride + ax];
        float hb = input.SurfaceLocalY[bz * MeshingInput.CornerStride + bx];
        float span = hb - ha;
        float t = System.Math.Abs(span) > 1e-6f ? (level - ha) / span : 0.5f;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        return new Vertex3(ax + t * (bx - ax), level, az + t * (bz - az));
    }

    private static int GetOrAddVertex(Vertex3 v, bool analytic, MeshData data, Dictionary<long, int> cache) {
        long key = ((long)System.Math.Round(v.X * 100.0) & 0xFFFFF) |
                   (((long)System.Math.Round(v.Y * 100.0) & 0xFFFFF) << 20) |
                   (((long)System.Math.Round(v.Z * 100.0) & 0xFFFFF) << 40);

        if (cache.TryGetValue(key, out int idx)) return idx;
        idx = data.Vertices.Count;
        data.Vertices.Add(v);
        data.OnTerrainSurface.Add(analytic);
        cache[key] = idx;
        return idx;
    }
}
