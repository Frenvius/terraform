namespace Terraform.Voxel.Core;

public class VoxelChunk {
    public const int SIZE = 64;
    public const int TOTAL = (SIZE + 1) * (SIZE + 1) * (SIZE + 1);

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

    public void DeserializeQuantized(byte[] data) {
        int n = data.Length < TOTAL ? data.Length : TOTAL;
        for (int i = 0; i < n; i++)
            _voxels[i] = new Voxel { Value = data[i] / 127.5f - 1f };
        for (int i = n; i < TOTAL; i++)
            _voxels[i] = Voxel.Solid;
    }

    public byte[] SerializeFull() {
        var data = new byte[TOTAL * 4];
        System.Buffer.BlockCopy(ToFloatArray(), 0, data, 0, data.Length);
        return data;
    }

    private float[] ToFloatArray() {
        var f = new float[TOTAL];
        for (int i = 0; i < TOTAL; i++) f[i] = _voxels[i].Value;
        return f;
    }

    public void DeserializeFull(byte[] data) {
        int n = (data.Length / 4) < TOTAL ? (data.Length / 4) : TOTAL;
        var f = new float[n];
        System.Buffer.BlockCopy(data, 0, f, 0, n * 4);
        for (int i = 0; i < n; i++)
            _voxels[i] = new Voxel { Value = f[i] };
        for (int i = n; i < TOTAL; i++)
            _voxels[i] = Voxel.Solid;
    }

    public bool HasAnyNonDefault() {
        for (int i = 0; i < TOTAL; i++)
            if (_voxels[i].Value > -1f + 1e-5f) return true;
        return false;
    }

    private static int Index(int x, int y, int z) => x + (SIZE + 1) * (y + (SIZE + 1) * z);

    private static bool OutOfBounds(int x, int y, int z) =>
        x < 0 || y < 0 || z < 0 || x > SIZE || y > SIZE || z > SIZE;
}
