using System.Collections.Generic;
using Terraform.Voxel.Core;

namespace Terraform.Voxel.Meshing;

public static class MarchingCubesMesher {
    private static readonly float[] _vals = new float[8];
    private static readonly Vertex3[] _edgeVerts = new Vertex3[12];

    public static MeshData Build(VoxelChunk chunk) {
        var data = new MeshData();
        var vertCache = new Dictionary<long, int>();

        for (int z = 0; z < VoxelChunk.SIZE; z++)
        for (int y = 0; y < VoxelChunk.SIZE; y++)
        for (int x = 0; x < VoxelChunk.SIZE; x++)
            ProcessCell(chunk, x, y, z, data, vertCache);

        return data;
    }

    private static void ProcessCell(
        VoxelChunk chunk, int cx, int cy, int cz,
        MeshData data, Dictionary<long, int> cache) {

        var corners  = MarchingCubesTables.Corners;
        for (int i = 0; i < 8; i++) {
            var (ox, oy, oz) = corners[i];
            _vals[i] = chunk.Get(cx + ox, cy + oy, cz + oz).Value;
        }

        int cubeIndex = 0;
        for (int i = 0; i < 8; i++)
            if (_vals[i] < 0f) cubeIndex |= (1 << i);

        int edgeMask = MarchingCubesTables.EdgeTable[cubeIndex];
        if (edgeMask == 0) return;

        System.Array.Clear(_edgeVerts, 0, _edgeVerts.Length);
        var edges = MarchingCubesTables.Edges;
        for (int e = 0; e < 12; e++) {
            if ((edgeMask & (1 << e)) == 0) continue;
            var (a, b) = edges[e];
            var (ax, ay, az) = corners[a];
            var (bx, by, bz) = corners[b];
            float t = _vals[a] / (_vals[a] - _vals[b]);
            float vx = (float)System.Math.Round((cx + ax + t * (bx - ax)) * 100.0) / 100f;
            float vy = (float)System.Math.Round((cy + ay + t * (by - ay)) * 100.0) / 100f;
            float vz = (float)System.Math.Round((cz + az + t * (bz - az)) * 100.0) / 100f;
            _edgeVerts[e] = new Vertex3(vx, vy, vz);
        }

        var triTable = MarchingCubesTables.TriTable;
        for (int t = 0; triTable[cubeIndex, t] != -1; t += 3) {
            int e0 = triTable[cubeIndex, t];
            int e1 = triTable[cubeIndex, t + 1];
            int e2 = triTable[cubeIndex, t + 2];

            data.Indices.Add(GetOrAddVertex(_edgeVerts[e0], data, cache));
            data.Indices.Add(GetOrAddVertex(_edgeVerts[e2], data, cache));
            data.Indices.Add(GetOrAddVertex(_edgeVerts[e1], data, cache));
        }
    }

    private static int GetOrAddVertex(Vertex3 v, MeshData data, Dictionary<long, int> cache) {
        long key = ((long)(v.X * 100) & 0xFFFFF) |
                   (((long)(v.Y * 100) & 0xFFFFF) << 20) |
                   (((long)(v.Z * 100) & 0xFFFFF) << 40);

        if (cache.TryGetValue(key, out int idx)) return idx;
        idx = data.Vertices.Count;
        data.Vertices.Add(v);
        cache[key] = idx;
        return idx;
    }
}
