using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public class WaterRenderData {
    public WaterVolume Volume;
    public Heightmap Heightmap;
    public MeshFilter SurfaceFilter;
    public Mesh OriginalSharedMesh;
    public Mesh ClonedMesh;
    public long ChunkKey;
    public HashSet<int> ReachableMask;
    public int ChunkOriginX;
    public int ChunkOriginZ;
}
