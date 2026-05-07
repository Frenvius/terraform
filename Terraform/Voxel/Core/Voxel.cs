namespace Terraform.Voxel.Core;

public struct Voxel {
    public float Value;

    public bool IsSolid => Value < 0f;

    public static readonly Voxel Empty = new Voxel { Value =  1f };
    public static readonly Voxel Solid = new Voxel { Value = -1f };

    public Voxel WithValue(float v) => new Voxel { Value = Clamp(v) };

    private static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;
}
