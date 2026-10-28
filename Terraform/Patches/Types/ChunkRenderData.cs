using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public class ChunkRenderData {
    public VoxelChunk Chunk;
    public Heightmap Heightmap;
    public MeshCollider HeightmapCollider;
    public GameObject MeshObject;
    public MeshFilter Filter;
    public MeshRenderer Renderer;
    public MeshCollider Collider;
    public Light FillLight;
    public Mesh Mesh;
    public readonly Dictionary<int, Texture2D> PaintBandMasks = new();
    public readonly List<int> SubmeshPaintBands = new();
    public float TerrainHeight;
    public readonly List<RenderFootprint> RenderFootprints = new();
}
