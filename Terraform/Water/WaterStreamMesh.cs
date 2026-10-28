using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Terraform.Water;

internal static class WaterStreamMesh {
    private const float UvTile = 16f;
    private const float EdgeSink = 0.03f;
    private const float S = WaterStreamSolver.CellSize;
    private static readonly List<Vector3> Vertices = new();
    private static readonly List<Vector3> Normals = new();
    private static readonly List<Vector2> Uvs = new();
    private static readonly List<int> Triangles = new();

    public static void Build(WaterStreamSolver solver, Vector3 origin, Mesh mesh) {
        Vertices.Clear();
        Normals.Clear();
        Uvs.Clear();
        Triangles.Clear();
        foreach (var cell in solver.Cells) AddSurface(solver, cell, origin);

        mesh.Clear();
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(Vertices);
        mesh.SetNormals(Normals);
        mesh.SetUVs(0, Uvs);
        mesh.SetTriangles(Triangles, 0);
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
    }

    private static void AddSurface(WaterStreamSolver solver, WaterStreamCell cell, Vector3 origin) {
        float x0 = cell.X * S - origin.x, x1 = x0 + S;
        float z0 = cell.Z * S - origin.z, z1 = z0 + S;
        var a = new Vector3(x0, CornerHeight(solver, cell, cell.X, cell.Z) - origin.y, z0);
        var b = new Vector3(x1, CornerHeight(solver, cell, cell.X + 1, cell.Z) - origin.y, z0);
        var c = new Vector3(x1, CornerHeight(solver, cell, cell.X + 1, cell.Z + 1) - origin.y, z1);
        var d = new Vector3(x0, CornerHeight(solver, cell, cell.X, cell.Z + 1) - origin.y, z1);
        AddQuad(a, b, c, d, Vector3.up);
    }

    private static float CornerHeight(WaterStreamSolver solver, WaterStreamCell cell, int cornerX, int cornerZ) {
        float surface = 0f, floor = 0f;
        int touching = 0;
        for (int dz = -1; dz <= 0; dz++)
        for (int dx = -1; dx <= 0; dx++) {
            int index = solver.FindCell(cornerX + dx, cornerZ + dz, cell.FloorY);
            if (index < 0) continue;
            var neighbor = solver.Cells[index];
            surface += neighbor.FloorY + WaterStreamSolver.Depth(neighbor.Strength);
            floor += neighbor.FloorY;
            touching++;
        }
        return touching == 4 ? surface / 4f : floor / touching - EdgeSink;
    }

    private static void AddQuad(Vector3 bottomLeft, Vector3 bottomRight, Vector3 topRight, Vector3 topLeft, Vector3 normal) {
        int first = Vertices.Count;
        AddVertex(bottomLeft, normal);
        AddVertex(bottomRight, normal);
        AddVertex(topRight, normal);
        AddVertex(topLeft, normal);
        Triangles.Add(first);
        Triangles.Add(first + 3);
        Triangles.Add(first + 2);
        Triangles.Add(first);
        Triangles.Add(first + 2);
        Triangles.Add(first + 1);
    }

    private static void AddVertex(Vector3 position, Vector3 normal) {
        Vertices.Add(position);
        Normals.Add(normal);
        Uvs.Add(new Vector2(position.x, position.z) / UvTile);
    }
}
