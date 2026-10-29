using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public readonly struct CellRect {
    public readonly int MinX;
    public readonly int MaxX;
    public readonly int MinZ;
    public readonly int MaxZ;

    public CellRect(int minX, int maxX, int minZ, int maxZ) {
        MinX = Mathf.Max(0, minX);
        MaxX = Mathf.Min(VoxelChunk.SIZE - 1, maxX);
        MinZ = Mathf.Max(0, minZ);
        MaxZ = Mathf.Min(VoxelChunk.SIZE - 1, maxZ);
    }

    public bool IsEmpty => MinX > MaxX || MinZ > MaxZ;

    public static CellRect Whole => new CellRect(0, VoxelChunk.SIZE - 1, 0, VoxelChunk.SIZE - 1);
}
