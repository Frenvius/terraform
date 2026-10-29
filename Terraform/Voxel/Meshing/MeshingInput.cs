using Terraform.Voxel.Core;

namespace Terraform.Voxel.Meshing;

public sealed class MeshingInput {
    public const int CellStride    = VoxelChunk.SIZE;
    public const int CornerStride  = VoxelChunk.SIZE + 1;

    public readonly bool[]  CellReplaced;
    public readonly float[] SurfaceLocalY;

    public MeshingInput(bool[] cellReplaced, float[] surfaceLocalY) {
        CellReplaced  = cellReplaced;
        SurfaceLocalY = surfaceLocalY;
    }
}
