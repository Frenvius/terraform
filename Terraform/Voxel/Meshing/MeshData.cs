using System.Collections.Generic;

namespace Terraform.Voxel.Meshing;

public class MeshData {
    public readonly List<Vertex3> Vertices = new();
    public readonly List<int>     Indices  = new();

    public void Clear() {
        Vertices.Clear();
        Indices.Clear();
    }
}

public readonly struct Vertex3 {
    public readonly float X, Y, Z;
    public Vertex3(float x, float y, float z) { X = x; Y = y; Z = z; }
}
