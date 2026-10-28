using System.Collections.Generic;
using UnityEngine;
using Terraform.Persistence;

namespace Terraform.Patches;

public partial class TerraformManager : MonoBehaviour {
    public static TerraformManager Instance;

    private Dictionary<int, ChunkRenderData> _chunks;
    private Dictionary<long, List<DigRecord>> _chunkDigHistory = new();
    private Dictionary<long, ChunkPersistence> _pendingPersistence = new();
    private readonly Dictionary<int, long> _pendingBuildHash = new();
    private readonly Dictionary<int, float> _pendingBuildSince = new();
    private readonly Dictionary<int, long> _postLoadHeightsHash = new();
    private readonly Dictionary<int, float> _postLoadWatchUntil = new();
    private const float PostLoadWatchDuration = 20f;
    private bool _persistenceLoaded;
    private bool _saveSubscribed;
    private float _scanTimer;
    private const float ScanInterval = 1f;
    private const float UndergroundEnterMarginY = 1.0f;

    private const float NoiseFrequency        = 0.12f;
    private const float NoiseAmplitude        = 0.03f;
    private const float NoiseOffset           = 137.42f;
    private const float OuterRadiusMultiplier = 1.55f;
    private const float BowlRadiusMultiplier  = 0.65f;
    private const float ShelfDepthMultiplier  = 0.4f;
    private const float RimFeatherExtra       = 1.0f;
    private const float BowlDepthMultiplier   = 1.0f;
    private const float BowlFalloffExponent   = 2f;
    private const float SeamInset             = 0.04f;
    private const float ShallowAboveKeep      = 0.35f;
    private const float SeamSnapDepth         = 0.85f;
    private const float SeamProbeDepth        = 0.4f;
    private const float MinCeilingThickness   = 1.0f;
    private const float RaiseRadiusMultiplier = 0.5f;

    private const float PickaxeRadius           = 1.1f;
    private const float PickaxeNoiseFrequency   = 0.6f;
    private const float PickaxeNoiseAmplitude   = 0.35f;
    private const float PickaxeHeightMultiplier = 1.5f;

    private const float SmoothRadius   = 3f;
    private const float SmoothPower    = 3f;
    private const float SmoothMaxStep  = 0.5f;

    private const float ExtendedPunchBleed     = 2f;
    private const float CrossChunkPaintBleed   = 1.5f;

    private const int FloorSentinelLayers = 1;
    private const int GlobalBedrockY     = 0;

    private static int ProtectedYLocalExclusive(int chunkOriginY) =>
        Mathf.Max(FloorSentinelLayers, GlobalBedrockY + 1 - chunkOriginY);

    private static bool VerboseLogs =>
        Configs.ModConfig.DebugMode != null && Configs.ModConfig.DebugMode.Value;

    private static void DebugLog(string message) {
        if (VerboseLogs) TerraformPlugin.Log.LogInfo(message);
    }

    private static long ChunkKey(int chunkOriginX, int chunkOriginZ) =>
        ((long)chunkOriginX << 32) ^ (uint)chunkOriginZ;

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(Instance.gameObject);
        }
        Instance         = this;
        _chunks          = new Dictionary<int, ChunkRenderData>();
        _waters          = new Dictionary<int, WaterRenderData>();
        _debugGrids      = new Dictionary<int, GameObject>();
        _debugChunkWalls = new Dictionary<int, GameObject>();
    }

    private void OnDestroy() {
        DestroyAllWaterLeaks();
        RemoveAllRimLinks();
        UnsubscribeWorldSave();
        if (Instance == this) Instance = null;
    }

    private void TryLoadPersistence() {
        if (_persistenceLoaded) return;
        if (ZNet.instance == null) return;
        if (string.IsNullOrEmpty(ZNet.instance.GetWorldName())) return;

        var loaded = TerraformPersistence.Load();
        if (loaded != null) {
            _pendingPersistence = loaded;
            _chunkDigHistory = new Dictionary<long, List<DigRecord>>(loaded.Count);
            foreach (var kv in loaded)
                _chunkDigHistory[kv.Key] = kv.Value.Digs;
        }
        _persistenceLoaded = true;
    }

    private void TrySubscribeWorldSave() {
        if (_saveSubscribed) return;
        ZNet.WorldSaveStarted += OnWorldSaveStarted;
        _saveSubscribed = true;
    }

    private void UnsubscribeWorldSave() {
        if (!_saveSubscribed) return;
        ZNet.WorldSaveStarted -= OnWorldSaveStarted;
        _saveSubscribed = false;
    }

    private void OnWorldSaveStarted() {
        Water.PlacedWater.SaveAll();
        var snapshot = BuildPersistenceSnapshot();
        TerraformPersistence.Save(snapshot);
    }

    private Dictionary<long, ChunkPersistence> BuildPersistenceSnapshot() {
        var snapshot = new Dictionary<long, ChunkPersistence>(_chunkDigHistory.Count);
        foreach (var kv in _chunkDigHistory) {
            var entry = new ChunkPersistence { Digs = new List<DigRecord>(kv.Value) };
            snapshot[kv.Key] = entry;
        }
        if (_chunks != null) {
            foreach (var data in _chunks.Values) {
                if (data == null || data.MeshObject == null) continue;
                Vector3 origin = data.MeshObject.transform.position;
                long chunkKey = ChunkKey(Mathf.RoundToInt(origin.x), Mathf.RoundToInt(origin.z));
                if (!snapshot.TryGetValue(chunkKey, out var entry)) continue;
                entry.TerrainHeight = data.TerrainHeight;
                if (data.Chunk != null && data.Chunk.HasAnyNonDefault()) {
                    entry.VoxelData = data.Chunk.SerializeFull();
                    entry.VoxelDataIsFull = true;
                }
            }
        }
        return snapshot;
    }

    public bool HasDigsAround(Vector3 worldPos) {
        if (_chunkDigHistory.Count == 0) return false;
        const int zoneSize = Terraform.Voxel.Core.VoxelChunk.SIZE;
        const int halfZone = zoneSize / 2;
        int originX = Mathf.FloorToInt((worldPos.x + halfZone) / zoneSize) * zoneSize - halfZone;
        int originZ = Mathf.FloorToInt((worldPos.z + halfZone) / zoneSize) * zoneSize - halfZone;
        for (int dz = -zoneSize; dz <= zoneSize; dz += zoneSize)
        for (int dx = -zoneSize; dx <= zoneSize; dx += zoneSize) {
            if (_chunkDigHistory.ContainsKey(ChunkKey(originX + dx, originZ + dz))) return true;
        }
        return false;
    }

    public bool IsPlayerInVoxelCavity(Vector3 worldPos) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;
        if (!Heightmap.GetHeight(worldPos, out float terrainY)) return false;
        return IsPlayerInVoxelCavity(worldPos, hm, terrainY);
    }

    public bool ShouldSuppressUnderWorldCheck(Vector3 worldPos) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;

        if (!Heightmap.GetHeight(worldPos, out float terrainY)) return false;
        if (worldPos.y >= terrainY - UndergroundEnterMarginY) return true;
        return IsPlayerInVoxelCavity(worldPos, hm, terrainY);
    }

    private bool IsPlayerInVoxelCavity(Vector3 worldPos, Heightmap hm, float terrainY) {
        if (worldPos.y >= terrainY - UndergroundEnterMarginY) return false;

        if (_chunks.TryGetValue(hm.GetInstanceID(), out var data)
            && data.Renderer != null
            && data.Renderer.enabled)
            return true;

        return IsPositionInKnownDig(worldPos);
    }

    private const float KnownDigXZSearchRadius = 4f;
    private const float KnownDigYSearchRadius  = 24f;

    public bool HasDigNearXZ(float worldX, float worldZ, float searchRadius = KnownDigXZSearchRadius) {
        float r2 = searchRadius * searchRadius;
        foreach (var list in _chunkDigHistory.Values) {
            for (int i = 0; i < list.Count; i++) {
                float dx = list[i].WorldPos.x - worldX;
                float dz = list[i].WorldPos.z - worldZ;
                if (dx * dx + dz * dz < r2) return true;
            }
        }
        return false;
    }

    public bool IsPositionInKnownDig(Vector3 worldPos) {
        float xzR2 = KnownDigXZSearchRadius * KnownDigXZSearchRadius;
        foreach (var list in _chunkDigHistory.Values) {
            for (int i = 0; i < list.Count; i++) {
                var d = list[i];
                float dx = d.WorldPos.x - worldPos.x;
                float dz = d.WorldPos.z - worldPos.z;
                if (dx * dx + dz * dz >= xzR2) continue;
                if (Mathf.Abs(d.WorldPos.y - worldPos.y) > KnownDigYSearchRadius) continue;
                return true;
            }
        }
        return false;
    }

    private void Update() {
        TryLoadPersistence();
        TrySubscribeWorldSave();

        _scanTimer += Time.deltaTime;
        if (_scanTimer >= ScanInterval) {
            _scanTimer = 0f;
            ScanHeightmaps();
        }
        FlushNavDirty();
        UpdateWaterStreams();
        UpdateUndergroundCollision();
        HandleInput();
    }
}
