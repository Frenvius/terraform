using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const int PaintLatticeSize = VoxelChunk.SIZE + 1;
    private const int PaintBandHeight = 2;
    private const int PaintBandCount = VoxelChunk.SIZE / PaintBandHeight;
    private const float UndergroundPaintVerticalReach = 1.5f;
    private const float UndergroundAirProbe = 0.5f;
    private const byte UndergroundMaskAlpha = 255;

    private readonly Dictionary<long, Dictionary<int, byte>> _undergroundPaved = new();
    private const byte UndergroundDirt = 255;

    private readonly Color32[] _maskPixels = new Color32[PaintLatticeSize * PaintLatticeSize];
    private readonly Color32[] _undergroundBasePixels = new Color32[PaintLatticeSize * PaintLatticeSize];

    private static long ChunkKeyOf(ChunkRenderData data) {
        Vector3 origin = data.MeshObject.transform.position;
        return ChunkKey(Mathf.RoundToInt(origin.x), Mathf.RoundToInt(origin.z));
    }

    private static int LatticeIndex(int x, int y, int z) => (y * PaintLatticeSize + z) * PaintLatticeSize + x;

    private static int PaintBandOf(float localY) =>
        Mathf.Clamp(Mathf.FloorToInt(localY / PaintBandHeight), 0, PaintBandCount - 1);

    public void PaintUnderground(Vector3 worldPos, float radius, TerrainModifier.PaintType type) {
        foreach (var data in _chunks.Values) {
            if (data?.MeshObject == null) continue;
            if (PaintUndergroundInChunk(data, worldPos, radius, type)) SubmitUndergroundPaint(data, worldPos, radius, type);
        }
    }

    private bool PaintUndergroundInChunk(ChunkRenderData data, Vector3 worldPos, float radius, TerrainModifier.PaintType type) {
        if (type != TerrainModifier.PaintType.Paved && type != TerrainModifier.PaintType.Dirt) return false;
        float target = type == TerrainModifier.PaintType.Paved ? 255f : 0f;
        if (!PaintUndergroundChunk(data, worldPos, radius, target)) return false;
        if (PaintedBandsChanged(data)) RebuildMesh(data);
        else RefreshUndergroundMasks(data);
        return true;
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

    private void RefreshUndergroundMasks(ChunkRenderData data) {
        BuildUndergroundBasePixels(data);
        WriteMask(data.UndergroundMask ??= NewUndergroundMask("TerraformUndergroundDirt"), _undergroundBasePixels);
        _undergroundPaved.TryGetValue(ChunkKeyOf(data), out var paved);
        foreach (var band in data.PaintBandMasks) WritePaintBandMask(band.Value, paved, band.Key);
    }

    private void BuildUndergroundBasePixels(ChunkRenderData data) {
        EnsureHeightmapReflection();
        var surface = _paintMaskField?.GetValue(data.Heightmap) as Texture2D;
        bool matchesLattice = surface != null && surface.width == PaintLatticeSize && surface.height == PaintLatticeSize;
        var source = matchesLattice ? surface.GetPixels32() : null;
        for (int i = 0; i < _undergroundBasePixels.Length; i++) {
            _undergroundBasePixels[i] = source != null && data.SurfaceSeamTexels[i]
                ? new Color32(source[i].r, 0, source[i].b, UndergroundMaskAlpha)
                : new Color32(UndergroundDirt, 0, 0, UndergroundMaskAlpha);
        }
    }

    private static void MarkSurfaceSeamTexels(
        ChunkRenderData data, Vector3[] verts, List<bool> onTerrainSurface, SubmeshSorter submeshes) {
        var usedBySurface = new bool[verts.Length];
        foreach (int i in submeshes.Surface) usedBySurface[i] = true;

        var seam = data.SurfaceSeamTexels;
        System.Array.Clear(seam, 0, seam.Length);
        foreach (var indices in submeshes.UndergroundLists()) {
            foreach (int i in indices) {
                if (usedBySurface[i] || onTerrainSurface[i]) MarkTexelsAround(seam, verts[i]);
            }
        }
    }

    private static void MarkTexelsAround(bool[] texels, Vector3 local) {
        int minX = Mathf.Max(0, Mathf.FloorToInt(local.x) - 1);
        int maxX = Mathf.Min(PaintLatticeSize - 1, Mathf.FloorToInt(local.x) + 2);
        int minZ = Mathf.Max(0, Mathf.FloorToInt(local.z) - 1);
        int maxZ = Mathf.Min(PaintLatticeSize - 1, Mathf.FloorToInt(local.z) + 2);
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
            texels[z * PaintLatticeSize + x] = true;
    }

    private static float NativeSurfaceLocalY(ChunkRenderData data, Vector3 local) {
        float lx = Mathf.Clamp(local.x, 0f, VoxelChunk.SIZE);
        float lz = Mathf.Clamp(local.z, 0f, VoxelChunk.SIZE);
        int x0 = Mathf.Min((int)lx, VoxelChunk.SIZE - 1);
        int z0 = Mathf.Min((int)lz, VoxelChunk.SIZE - 1);
        float fx = lx - x0, fz = lz - z0;
        var h = data.SurfaceLocalY;
        float y0 = Mathf.Lerp(h[z0 * PaintLatticeSize + x0], h[z0 * PaintLatticeSize + x0 + 1], fx);
        float y1 = Mathf.Lerp(h[(z0 + 1) * PaintLatticeSize + x0], h[(z0 + 1) * PaintLatticeSize + x0 + 1], fx);
        return Mathf.Lerp(y0, y1, fz);
    }

    private static Texture2D NewUndergroundMask(string name) =>
        new(PaintLatticeSize, PaintLatticeSize) { name = name, wrapMode = TextureWrapMode.Clamp };

    private static void WriteMask(Texture2D mask, Color32[] pixels) {
        mask.SetPixels32(pixels);
        mask.Apply();
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
            int i = z * PaintLatticeSize + x;
            var dirt = _undergroundBasePixels[i];
            float w = weight / 255f;
            _maskPixels[i] = new Color32(
                (byte)Mathf.RoundToInt(dirt.r * (1f - w)), 0,
                (byte)Mathf.RoundToInt(Mathf.Lerp(dirt.b, 255f, w)), UndergroundMaskAlpha);
        }
        WriteMask(mask, _maskPixels);
    }

    private void SyncUndergroundMasks(ChunkRenderData data, bool[] paintedBands) {
        for (int b = 0; b < PaintBandCount; b++) {
            bool has = data.PaintBandMasks.TryGetValue(b, out var mask);
            if (paintedBands[b] && !has) data.PaintBandMasks[b] = NewUndergroundMask($"TerraformUndergroundPaint_{b}");
            if (paintedBands[b] || !has) continue;
            Destroy(mask);
            data.PaintBandMasks.Remove(b);
        }
        RefreshUndergroundMasks(data);
    }

    private static void DestroyUndergroundMasks(ChunkRenderData data) {
        if (data.UndergroundMask != null) Destroy(data.UndergroundMask);
        data.UndergroundMask = null;
        foreach (var mask in data.PaintBandMasks.Values) Destroy(mask);
        data.PaintBandMasks.Clear();
    }

    private const int SurfaceSubmesh     = 0;
    private const int UndergroundSubmesh = 1;
    private const int FirstPaintBandSubmesh = 2;

    private sealed class SubmeshSorter {
        private readonly ChunkRenderData _data;
        private readonly List<int>[] _bandIndices = new List<int>[PaintBandCount];

        public readonly List<int> Surface;
        public readonly List<int> Underground;
        public readonly bool[] PaintedBands;

        public SubmeshSorter(ChunkRenderData data, bool[] paintedBands, int capacity) {
            _data = data;
            PaintedBands = paintedBands;
            Surface = new List<int>(capacity);
            Underground = new List<int>(capacity);
        }

        public int UndergroundTriangleCount {
            get {
                int count = Underground.Count;
                foreach (var band in _bandIndices) count += band?.Count ?? 0;
                return count / 3;
            }
        }

        public bool IsEmpty => Surface.Count == 0 && UndergroundTriangleCount == 0;

        public IEnumerable<List<int>> UndergroundLists() {
            yield return Underground;
            foreach (var band in _bandIndices)
                if (band != null) yield return band;
        }

        public List<int> For(Vector3 localCentroid, Vector3 normal) {
            if (localCentroid.y > NativeSurfaceLocalY(_data, localCentroid) - UndergroundEnterMarginY) return Surface;
            if (!HasSolidAbove(_data, localCentroid + normal * UndergroundAirProbe)) return Surface;
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
    }

    private void SetUndergroundPaint(long chunkKey, Dictionary<int, byte> paved) {
        if (paved == null || paved.Count == 0) _undergroundPaved.Remove(chunkKey);
        else _undergroundPaved[chunkKey] = paved;
    }
}
