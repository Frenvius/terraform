namespace Terraform.Voxel.Core;

public struct Voxel {
    public float Value;

    public static readonly Voxel Empty = new Voxel { Value =  1f };
    public static readonly Voxel Solid = new Voxel { Value = -1f };
}
