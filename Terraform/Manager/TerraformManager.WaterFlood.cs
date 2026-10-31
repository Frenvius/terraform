using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private enum PlaneSample : byte { Solid, Lake, Dug }

    private const float PlaneStep = 0.5f;
    private const int PlaneCells = (int)(VoxelChunk.SIZE / PlaneStep);
    private const int PlaneSize = PlaneCells + 1;
    private const int FloodSearchLimit = 65536;
    private const float FloodColumnStep = 0.5f;

    private readonly HashSet<long> _floodedSamples = new();
    private readonly HashSet<int> _floodChangedZones = new();
    private readonly Dictionary<long, PlaneSample> _foreignSamples = new();
    private readonly HashSet<long> _floodVisited = new();
    private readonly List<long> _floodComponent = new();
    private readonly Queue<long> _floodQueue = new();
    private readonly PlaneSample[] _planeGrid = new PlaneSample[PlaneSize * PlaneSize];
    private readonly bool[] _planeWet = new bool[PlaneSize * PlaneSize];

    public bool IsInOpenWater(Vector3 point) {
        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        if (point.y >= waterY || !TryGetChunkAt(point, out var data) || data.Heightmap == null) return false;
        int ix = Mathf.RoundToInt(point.x / PlaneStep), iz = Mathf.RoundToInt(point.z / PlaneStep);
        if (!_floodedSamples.Contains(PlaneKey(ix, iz))
            && ClassifyPlanePoint(data, GetHeightsArray(data.Heightmap), ix * PlaneStep, iz * PlaneStep, waterY) != PlaneSample.Lake)
            return false;
        for (float y = point.y; y < waterY; y += FloodColumnStep) {
            if (!IsVoxelAir(new Vector3(point.x, y, point.z))) return false;
        }
        return true;
    }

    private void RefreshWaterPlane(ChunkRenderData chunk) {
        chunk.WaterPlaneWet = null;
        chunk.DryWaterCells = null;
        if (chunk.MeshObject == null || chunk.Heightmap == null || chunk.Chunk == null
            || !_chunkDigHistory.ContainsKey(chunk.ChunkKey)) return;

        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        Vector3 origin = chunk.MeshObject.transform.position;
        int originX = Mathf.RoundToInt(origin.x / PlaneStep);
        int originZ = Mathf.RoundToInt(origin.z / PlaneStep);
        ClassifyZonePlane(chunk, originX, originZ, waterY);
        ResolveZoneFlood(originX, originZ, waterY);
        chunk.WaterPlaneWet = (bool[])_planeWet.Clone();
        chunk.DryWaterCells = CollectDryWaterCells();
    }

    private static bool IsUnderWaterPlane(ChunkRenderData chunk, Vector3 local) {
        var wet = chunk.WaterPlaneWet;
        if (wet == null) return false;
        int x = Mathf.Clamp(Mathf.RoundToInt(local.x / PlaneStep), 0, PlaneCells);
        int z = Mathf.Clamp(Mathf.RoundToInt(local.z / PlaneStep), 0, PlaneCells);
        return wet[z * PlaneSize + x];
    }

    private HashSet<(int x, int y)> CollectDryWaterCells() {
        var dry = new HashSet<(int x, int y)>();
        for (int z = 0; z < PlaneCells; z++)
        for (int x = 0; x < PlaneCells; x++) {
            bool hasDry = false, hasWet = false;
            for (int c = 0; c < 4; c++) {
                int index = (z + (c >> 1)) * PlaneSize + x + (c & 1);
                if (_planeWet[index]) hasWet = true;
                else if (_planeGrid[index] == PlaneSample.Dug) hasDry = true;
            }
            if (hasDry && !hasWet) dry.Add((x, z));
        }
        return dry;
    }

    private void ClassifyZonePlane(ChunkRenderData chunk, int originX, int originZ, float waterY) {
        var heights = GetHeightsArray(chunk.Heightmap);
        for (int z = 0; z < PlaneSize; z++)
        for (int x = 0; x < PlaneSize; x++) {
            int index = z * PlaneSize + x;
            _planeGrid[index] = ClassifyPlanePoint(chunk, heights, (originX + x) * PlaneStep, (originZ + z) * PlaneStep, waterY);
            _planeWet[index] = _planeGrid[index] == PlaneSample.Lake;
        }
    }

    private void ResolveZoneFlood(int originX, int originZ, float waterY) {
        _foreignSamples.Clear();
        _floodVisited.Clear();
        for (int z = 0; z < PlaneSize; z++)
        for (int x = 0; x < PlaneSize; x++) {
            long key = PlaneKey(originX + x, originZ + z);
            if (_planeGrid[z * PlaneSize + x] != PlaneSample.Dug) {
                _floodedSamples.Remove(key);
                continue;
            }
            if (_floodVisited.Contains(key)) continue;
            bool wet = FloodComponent(key, originX, originZ, waterY);
            foreach (long member in _floodComponent) StoreFlooded(member, wet, originX, originZ);
        }
    }

    private bool FloodComponent(long start, int originX, int originZ, float waterY) {
        _floodComponent.Clear();
        _floodQueue.Clear();
        _floodVisited.Add(start);
        _floodQueue.Enqueue(start);
        bool wet = false;
        while (_floodQueue.Count > 0) {
            long key = _floodQueue.Dequeue();
            _floodComponent.Add(key);
            if (_floodComponent.Count >= FloodSearchLimit) return false;
            int ix = (int)(key >> 32), iz = (int)key;
            wet |= Visit(ix + 1, iz) | Visit(ix - 1, iz) | Visit(ix, iz + 1) | Visit(ix, iz - 1);
        }
        return wet;

        bool Visit(int ix, int iz) {
            long key = PlaneKey(ix, iz);
            var sample = SampleAt(ix, iz, originX, originZ, waterY);
            if (sample == PlaneSample.Lake) return true;
            if (sample == PlaneSample.Dug && _floodVisited.Add(key)) _floodQueue.Enqueue(key);
            return false;
        }
    }

    private void StoreFlooded(long key, bool wet, int originX, int originZ) {
        int ix = (int)(key >> 32), iz = (int)key;
        int x = ix - originX, z = iz - originZ;
        bool inZone = x >= 0 && z >= 0 && x < PlaneSize && z < PlaneSize;
        if (inZone) _planeWet[z * PlaneSize + x] = wet;
        bool changed = wet ? _floodedSamples.Add(key) : _floodedSamples.Remove(key);
        if (!changed || inZone) return;
        if (TryGetChunkAt(new Vector3(ix * PlaneStep, 0f, iz * PlaneStep), out var other))
            _floodChangedZones.Add(other.Heightmap.GetInstanceID());
    }

    private PlaneSample SampleAt(int ix, int iz, int originX, int originZ, float waterY) {
        int x = ix - originX, z = iz - originZ;
        if (x >= 0 && z >= 0 && x < PlaneSize && z < PlaneSize) return _planeGrid[z * PlaneSize + x];
        long key = PlaneKey(ix, iz);
        if (_foreignSamples.TryGetValue(key, out var cached)) return cached;
        var world = new Vector3(ix * PlaneStep, waterY, iz * PlaneStep);
        PlaneSample sample;
        if (TryGetChunkAt(world, out var data) && data.Heightmap != null)
            sample = ClassifyPlanePoint(data, GetHeightsArray(data.Heightmap), world.x, world.z, waterY);
        else
            sample = OriginalHeight(world, out float h) && h < waterY ? PlaneSample.Lake : PlaneSample.Solid;
        _foreignSamples[key] = sample;
        return sample;
    }

    private static PlaneSample ClassifyPlanePoint(ChunkRenderData data, List<float> heights, float x, float z, float waterY) {
        bool lakeBed = SampleHeightmapMeshYCached(data.Heightmap, heights, new Vector3(x, 0f, z), waterY) < waterY;
        if (data.Chunk == null) return lakeBed ? PlaneSample.Lake : PlaneSample.Solid;
        Vector3 origin = data.MeshObject.transform.position;
        float lx = x - origin.x, ly = waterY - origin.y, lz = z - origin.z;
        if (ly < 0f || ly > VoxelChunk.SIZE) return lakeBed ? PlaneSample.Lake : PlaneSample.Solid;
        if (SampleSdfTri(data.Chunk, lx, ly, lz) <= 0f) return PlaneSample.Solid;
        return lakeBed || !HasAlteredNear(data.Chunk, lx, ly, lz) ? PlaneSample.Lake : PlaneSample.Dug;
    }

    private static bool HasAlteredNear(VoxelChunk chunk, float lx, float ly, float lz) {
        int x0 = Mathf.Clamp(Mathf.FloorToInt(lx), 0, VoxelChunk.SIZE), x1 = Mathf.Clamp(Mathf.CeilToInt(lx), 0, VoxelChunk.SIZE);
        int z0 = Mathf.Clamp(Mathf.FloorToInt(lz), 0, VoxelChunk.SIZE), z1 = Mathf.Clamp(Mathf.CeilToInt(lz), 0, VoxelChunk.SIZE);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(ly) - 1, 0, VoxelChunk.SIZE), y1 = Mathf.Clamp(Mathf.CeilToInt(ly) + 1, 0, VoxelChunk.SIZE);
        for (int y = y0; y <= y1; y++) {
            if (chunk.IsAltered(x0, y, z0) || chunk.IsAltered(x1, y, z0)
                || chunk.IsAltered(x0, y, z1) || chunk.IsAltered(x1, y, z1)) return true;
        }
        return false;
    }

    private static long PlaneKey(int ix, int iz) => ((long)ix << 32) | (uint)iz;
}
