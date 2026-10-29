namespace Terraform.Voxel.Core;

public class VoxelChunk {
    public const int SIZE = 64;
    public const int TOTAL = (SIZE + 1) * (SIZE + 1) * (SIZE + 1);
    public const float AlteredEpsilon = 1e-3f;
    private const int AlteredWords = (TOTAL + 63) / 64;

    private readonly Voxel[] _voxels;
    private readonly ulong[] _altered;

    public VoxelChunk() {
        _voxels = new Voxel[TOTAL];
        _altered = new ulong[AlteredWords];
        for (int i = 0; i < TOTAL; i++)
            _voxels[i] = Voxel.Solid;
    }

    public Voxel Get(int x, int y, int z) {
        if (OutOfBounds(x, y, z)) return Voxel.Empty;
        return _voxels[Index(x, y, z)];
    }

    public bool IsAltered(int x, int y, int z) {
        if (OutOfBounds(x, y, z)) return false;
        return GetBit(Index(x, y, z));
    }

    public bool Write(int x, int y, int z, float value) {
        if (OutOfBounds(x, y, z)) return false;
        int i = Index(x, y, z);
        float old = _voxels[i].Value;
        if (System.Math.Abs(value - old) <= AlteredEpsilon) return false;
        _voxels[i].Value = value;
        SetBit(i);
        return true;
    }

    public void SetIntact(int x, int y, int z, float value) {
        if (OutOfBounds(x, y, z)) return;
        int i = Index(x, y, z);
        _voxels[i].Value = value;
        ClearBit(i);
    }

    public bool HasAnyAltered() {
        for (int i = 0; i < AlteredWords; i++)
            if (_altered[i] != 0UL) return true;
        return false;
    }

    public int CountAltered() {
        int count = 0;
        for (int i = 0; i < AlteredWords; i++) {
            ulong w = _altered[i];
            while (w != 0UL) { w &= w - 1UL; count++; }
        }
        return count;
    }

    public byte[] SerializeFull() {
        var data = new byte[TOTAL * 4];
        System.Buffer.BlockCopy(ToFloatArray(), 0, data, 0, data.Length);
        return data;
    }

    public byte[] SerializeAltered() {
        var data = new byte[AlteredWords * 8];
        System.Buffer.BlockCopy(_altered, 0, data, 0, data.Length);
        return data;
    }

    public static float[] DecodeFull(byte[] data) {
        int n = (data.Length / 4) < TOTAL ? (data.Length / 4) : TOTAL;
        var f = new float[n];
        System.Buffer.BlockCopy(data, 0, f, 0, n * 4);
        return f;
    }

    public static float[] DecodeQuantized(byte[] data) {
        int n = data.Length < TOTAL ? data.Length : TOTAL;
        var f = new float[n];
        for (int i = 0; i < n; i++)
            f[i] = data[i] / 127.5f - 1f;
        return f;
    }

    public void RestoreAltered(float[] saved, byte[] alteredBits) {
        int words = alteredBits.Length / 8 < AlteredWords ? alteredBits.Length / 8 : AlteredWords;
        var bits = new ulong[words];
        System.Buffer.BlockCopy(alteredBits, 0, bits, 0, words * 8);
        for (int i = 0; i < saved.Length; i++) {
            if ((bits[i >> 6] & (1UL << (i & 63))) == 0UL) continue;
            _voxels[i].Value = saved[i];
            SetBit(i);
        }
    }

    public void RestoreLegacy(float[] saved) {
        for (int i = 0; i < saved.Length; i++) {
            float s = saved[i];
            if (s <= -1f + 1e-5f) continue;
            if (System.Math.Abs(s - _voxels[i].Value) <= AlteredEpsilon) continue;
            _voxels[i].Value = s;
            SetBit(i);
        }
    }

    private float[] ToFloatArray() {
        var f = new float[TOTAL];
        for (int i = 0; i < TOTAL; i++) f[i] = _voxels[i].Value;
        return f;
    }

    private bool GetBit(int i) => (_altered[i >> 6] & (1UL << (i & 63))) != 0UL;
    private void SetBit(int i) => _altered[i >> 6] |= 1UL << (i & 63);
    private void ClearBit(int i) => _altered[i >> 6] &= ~(1UL << (i & 63));

    private static int Index(int x, int y, int z) => x + (SIZE + 1) * (y + (SIZE + 1) * z);

    private static bool OutOfBounds(int x, int y, int z) =>
        x < 0 || y < 0 || z < 0 || x > SIZE || y > SIZE || z > SIZE;
}
