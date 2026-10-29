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
    public Texture2D UndergroundMask;
    public readonly Dictionary<int, Texture2D> PaintBandMasks = new();
    public readonly List<int> SubmeshPaintBands = new();
    public float TerrainHeight;
    public long ChunkKey;
    public long SeededHeightsHash;
    public int ReseedCount;
    public readonly bool[] CellReplaced = new bool[VoxelChunk.SIZE * VoxelChunk.SIZE];
    public readonly bool[] CellCrater = new bool[VoxelChunk.SIZE * VoxelChunk.SIZE];
    public readonly bool[] SurfaceSeamTexels = new bool[(VoxelChunk.SIZE + 1) * (VoxelChunk.SIZE + 1)];
    public float[] SurfaceLocalY;
}
