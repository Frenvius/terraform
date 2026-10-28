using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const int PaintLatticeSize = VoxelChunk.SIZE + 1;
    private const int PaintBandHeight = 2;
    private const int PaintBandCount = VoxelChunk.SIZE / PaintBandHeight;
    private const float UndergroundPaintVerticalReach = 1.5f;
    private const float UndergroundCeilingMargin = 0.5f;
    private const float OpenColumnScanStep = 0.5f;
    private const byte UndergroundMaskAlpha = 255;

    private readonly Dictionary<long, Dictionary<int, byte>> _undergroundPaved = new();
    private readonly Color32[] _paintBandPixels = new Color32[PaintLatticeSize * PaintLatticeSize];
    private static Texture2D _undergroundDirtMask;

    private static Texture2D UndergroundDirtMask {
        get {
            if (_undergroundDirtMask != null) return _undergroundDirtMask;
            _undergroundDirtMask = new Texture2D(1, 1) { name = "TerraformUndergroundDirt", wrapMode = TextureWrapMode.Clamp };
            _undergroundDirtMask.SetPixels32(new[] { new Color32(255, 0, 0, UndergroundMaskAlpha) });
            _undergroundDirtMask.Apply();
            return _undergroundDirtMask;
        }
    }

    private static long ChunkKeyOf(ChunkRenderData data) {
        Vector3 origin = data.MeshObject.transform.position;
        return ChunkKey(Mathf.RoundToInt(origin.x), Mathf.RoundToInt(origin.z));
    }

    private static int LatticeIndex(int x, int y, int z) => (y * PaintLatticeSize + z) * PaintLatticeSize + x;

    private static int PaintBandOf(float localY) =>
        Mathf.Clamp(Mathf.FloorToInt(localY / PaintBandHeight), 0, PaintBandCount - 1);

    public void PaintUnderground(Vector3 worldPos, float radius, TerrainModifier.PaintType type) {
        if (type != TerrainModifier.PaintType.Paved && type != TerrainModifier.PaintType.Dirt) return;
        float target = type == TerrainModifier.PaintType.Paved ? 255f : 0f;

        foreach (var data in _chunks.Values) {
            if (data?.MeshObject == null) continue;
            if (!PaintUndergroundChunk(data, worldPos, radius, target)) continue;
            if (PaintedBandsChanged(data)) RebuildMesh(data);
            else RefreshPaintBandMasks(data);
        }
    }

    private bool PaintUndergroundChunk(ChunkRenderData data, Vector3 worldPos, float radius, float target) {
        Vector3 local = worldPos - data.MeshObject.transform.position;
        int minX = Mathf.Max(0, Mathf.FloorToInt(local.x - radius));
        int maxX = Mathf.Min(PaintLatticeSize - 1, Mathf.CeilToInt(local.x + radius));
        int minZ = Mathf.Max(0, Mathf.FloorToInt(local.z - radius));
        int maxZ = Mathf.Min(PaintLatticeSize - 1, Mathf.CeilToInt(local.z + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(local.y - UndergroundPaintVerticalReach));
        int maxY = Mathf.Min(PaintLatticeSize - 1, Mathf.CeilToInt(local.y + UndergroundPaintVerticalReach));
        if (minX > maxX || minZ > maxZ || minY > maxY) return false;

        long key = ChunkKeyOf(data);
        if (!_undergroundPaved.TryGetValue(key, out var paved)) {
            if (target <= 0f) return false;
            paved = new Dictionary<int, byte>();
            _undergroundPaved[key] = paved;
        }

        bool changed = false;
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++) {
            float dx = x - local.x;
            float dz = z - local.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if (distance > radius) continue;
            float weight = Mathf.Pow(1f - Mathf.Clamp01(distance / radius), 0.1f);
            for (int y = minY; y <= maxY; y++)
                changed |= BlendLatticePoint(paved, LatticeIndex(x, y, z), target, weight);
        }
        return changed;
    }

    private static bool BlendLatticePoint(Dictionary<int, byte> paved, int index, float target, float weight) {
        paved.TryGetValue(index, out byte current);
        byte blended = (byte)Mathf.RoundToInt(Mathf.Lerp(current, target, weight));
        if (blended == current) return false;
        if (blended == 0) paved.Remove(index);
        else paved[index] = blended;
        return true;
    }

    private bool[] CollectPaintedBands(ChunkRenderData data) {
        var bands = new bool[PaintBandCount];
        if (!_undergroundPaved.TryGetValue(ChunkKeyOf(data), out var paved)) return bands;
        foreach (int index in paved.Keys) {
            int y = index / (PaintLatticeSize * PaintLatticeSize);
            bands[PaintBandOf(y)] = true;
            if (y % PaintBandHeight == 0 && y > 0) bands[PaintBandOf(y - 1)] = true;
        }
        return bands;
    }

    private bool PaintedBandsChanged(ChunkRenderData data) {
        var current = CollectPaintedBands(data);
        for (int b = 0; b < PaintBandCount; b++) {
            bool hasSubmesh = data.PaintBandMasks.ContainsKey(b);
            if (current[b] != hasSubmesh) return true;
        }
        return false;
    }

    private void RefreshPaintBandMasks(ChunkRenderData data) {
        _undergroundPaved.TryGetValue(ChunkKeyOf(data), out var paved);
        foreach (var band in data.PaintBandMasks) WritePaintBandMask(band.Value, paved, band.Key);
    }

    private void WritePaintBandMask(Texture2D mask, Dictionary<int, byte> paved, int band) {
        int minY = band * PaintBandHeight;
        int maxY = Mathf.Min(PaintLatticeSize - 1, minY + PaintBandHeight);
        for (int z = 0; z < PaintLatticeSize; z++)
        for (int x = 0; x < PaintLatticeSize; x++) {
            byte weight = 0;
            if (paved != null) {
                for (int y = minY; y <= maxY; y++)
                    if (paved.TryGetValue(LatticeIndex(x, y, z), out byte value) && value > weight) weight = value;
            }
            _paintBandPixels[z * PaintLatticeSize + x] = new Color32((byte)(255 - weight), 0, weight, UndergroundMaskAlpha);
        }
        mask.SetPixels32(_paintBandPixels);
        mask.Apply();
    }

    private void SyncPaintBandMasks(ChunkRenderData data, bool[] paintedBands) {
        _undergroundPaved.TryGetValue(ChunkKeyOf(data), out var paved);
        for (int b = 0; b < PaintBandCount; b++) {
            bool has = data.PaintBandMasks.TryGetValue(b, out var mask);
            if (!paintedBands[b]) {
                if (has) {
                    Destroy(mask);
                    data.PaintBandMasks.Remove(b);
                }
                continue;
            }
            if (!has) {
                mask = new Texture2D(PaintLatticeSize, PaintLatticeSize) {
                    name = $"TerraformUndergroundPaint_{b}", wrapMode = TextureWrapMode.Clamp,
                };
                data.PaintBandMasks[b] = mask;
            }
            WritePaintBandMask(mask, paved, b);
        }
    }

    private static void DestroyPaintBandMasks(ChunkRenderData data) {
        foreach (var mask in data.PaintBandMasks.Values) Destroy(mask);
        data.PaintBandMasks.Clear();
    }

    private static float OpenSkyFloorLocalY(ChunkRenderData data, List<float> heights, Vector3 origin, int cellX, int cellZ) {
        float localX = cellX + 0.5f;
        float localZ = cellZ + 0.5f;
        float terrainY = SampleHeightmapMeshYCached(
            data.Heightmap, heights, new Vector3(origin.x + localX, 0f, origin.z + localZ), data.TerrainHeight);
        float y = Mathf.Min(terrainY - origin.y, VoxelChunk.SIZE) - SeamProbeDepth;
        while (y > 0f && SampleSdfTri(data.Chunk, localX, y, localZ) > 0f) y -= OpenColumnScanStep;
        return y;
    }

    private const int SurfaceSubmesh     = 0;
    private const int UndergroundSubmesh = 1;
    private const int FirstPaintBandSubmesh = 2;

    private sealed class SubmeshSorter {
        private readonly ChunkRenderData _data;
        private readonly List<float> _heights;
        private readonly Vector3 _origin;
        private readonly float[] _openFloorCache = new float[VoxelChunk.SIZE * VoxelChunk.SIZE];
        private readonly List<int>[] _bandIndices = new List<int>[PaintBandCount];

        public readonly List<int> Surface;
        public readonly List<int> Underground;
        public readonly bool[] PaintedBands;

        public SubmeshSorter(ChunkRenderData data, List<float> heights, bool[] paintedBands, int capacity) {
            _data = data;
            _heights = heights;
            _origin = data.MeshObject.transform.position;
            PaintedBands = paintedBands;
            Surface = new List<int>(capacity);
            Underground = new List<int>(capacity);
            for (int i = 0; i < _openFloorCache.Length; i++) _openFloorCache[i] = float.NaN;
        }

        public int UndergroundTriangleCount {
            get {
                int count = Underground.Count;
                foreach (var band in _bandIndices) count += band?.Count ?? 0;
                return count / 3;
            }
        }

        public bool IsEmpty => Surface.Count == 0 && UndergroundTriangleCount == 0;

        public List<int> For(Vector3 localCentroid) {
            if (!IsUnderground(localCentroid)) return Surface;
            int band = PaintBandOf(localCentroid.y);
            if (!PaintedBands[band]) return Underground;
            return _bandIndices[band] ??= new List<int>();
        }

        public void ApplyTo(ChunkRenderData data) {
            data.SubmeshPaintBands.Clear();
            for (int b = 0; b < PaintBandCount; b++)
                if (_bandIndices[b] != null) data.SubmeshPaintBands.Add(b);

            var mesh = data.Mesh;
            mesh.subMeshCount = FirstPaintBandSubmesh + data.SubmeshPaintBands.Count;
            mesh.SetTriangles(Surface, SurfaceSubmesh, calculateBounds: false);
            mesh.SetTriangles(Underground, UndergroundSubmesh, calculateBounds: false);
            for (int i = 0; i < data.SubmeshPaintBands.Count; i++)
                mesh.SetTriangles(_bandIndices[data.SubmeshPaintBands[i]], FirstPaintBandSubmesh + i, calculateBounds: false);
        }

        private bool IsUnderground(Vector3 localCentroid) {
            int cellX = Mathf.Clamp(Mathf.FloorToInt(localCentroid.x), 0, VoxelChunk.SIZE - 1);
            int cellZ = Mathf.Clamp(Mathf.FloorToInt(localCentroid.z), 0, VoxelChunk.SIZE - 1);
            int index = cellZ * VoxelChunk.SIZE + cellX;
            if (float.IsNaN(_openFloorCache[index]))
                _openFloorCache[index] = OpenSkyFloorLocalY(_data, _heights, _origin, cellX, cellZ);
            return localCentroid.y < _openFloorCache[index] - UndergroundCeilingMargin;
        }
    }

    private void LoadUndergroundPaint(long chunkKey, Dictionary<int, byte> paved) {
        if (paved == null || paved.Count == 0) return;
        _undergroundPaved[chunkKey] = paved;
    }
}
