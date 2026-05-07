namespace Terraform.Voxel.Core;

public class VoxelChunk {
    public const int SIZE = 64;
    private const int TOTAL = (SIZE + 1) * (SIZE + 1) * (SIZE + 1);

    private readonly Voxel[] _voxels;

    public VoxelChunk() {
        _voxels = new Voxel[TOTAL];
        for (int i = 0; i < TOTAL; i++)
            _voxels[i] = Voxel.Solid;
    }

    public Voxel Get(int x, int y, int z) {
        if (OutOfBounds(x, y, z)) return Voxel.Empty;
        return _voxels[Index(x, y, z)];
    }

    public void Set(int x, int y, int z, Voxel v) {
        if (OutOfBounds(x, y, z)) return;
        _voxels[Index(x, y, z)] = v;
    }

    private static int Index(int x, int y, int z) => x + (SIZE + 1) * (y + (SIZE + 1) * z);

    private static bool OutOfBounds(int x, int y, int z) =>
        x < 0 || y < 0 || z < 0 || x > SIZE || y > SIZE || z > SIZE;
}
