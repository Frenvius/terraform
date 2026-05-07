using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public class ChunkRenderData {
    public VoxelChunk Chunk;
    public Heightmap Heightmap;
    public GameObject MeshObject;
    public MeshFilter Filter;
    public MeshRenderer Renderer;
    public MeshCollider Collider;
    public Mesh Mesh;
    public float TerrainHeight;
    public readonly List<RenderFootprint> RenderFootprints = new();
    public readonly HashSet<long> CarvedCells = new();
}
