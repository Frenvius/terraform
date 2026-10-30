using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager : MonoBehaviour {
    public static TerraformManager Instance;

    private Dictionary<int, ChunkRenderData> _chunks;
    private readonly Dictionary<long, List<DigRecord>> _chunkDigHistory = new();
    private readonly Dictionary<int, long> _pendingBuildHash = new();
    private readonly Dictionary<int, float> _pendingBuildSince = new();
    private bool _persistenceLoaded;
    private bool _saveSubscribed;
    private float _scanTimer;
    private const float ScanInterval = 1f;
    internal const float UndergroundEnterMarginY = 1.0f;

    private const float NoiseFrequency        = 0.12f;
    private const float NoiseAmplitude        = 0.03f;
    private const float NoiseOffset           = 137.42f;
    private const float OuterRadiusMultiplier = 1.55f;
    private const float BowlRadiusMultiplier  = 0.65f;
    private const float ShelfDepthMultiplier  = 0.2f;
    private const float RimFeatherExtra       = 1.0f;
    private const float BowlDepthMultiplier   = 0.5f;
    private const float BowlFalloffExponent   = 2f;
    private const float SeamProbeDepth        = 0.4f;
    private const float RaiseRadiusMultiplier = 0.5f;

    private const float PickaxeRadius           = 1.1f;
    private const float PickaxeNoiseFrequency   = 0.6f;
    private const float PickaxeNoiseAmplitude   = 0.35f;
    private const float PickaxeHeightMultiplier = 1.0f;

    private const float SmoothRadius   = 3f;
    private const float SmoothPower    = 3f;
    private const float SmoothMaxStep  = 0.5f;
    private const float LevelMaxStep   = 8f;
    private const float SdfBias        = 0.05f;
    private const float GroundOpNormalMinY = 0.5f;

    private const float ExtendedPunchBleed     = 2f;
    private const float OpenedCellPaintRadius  = DirtPaintOp.Radius;

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

    public bool IsPlayerInVoxelCavity(Vector3 worldPos) =>
        IsInDugAir(worldPos) || IsInDugAir(worldPos + Vector3.up * UndergroundProbeUp);

    public bool ShouldSuppressUnderWorldCheck(Vector3 worldPos) {
        if (!OriginalHeight(worldPos, out float terrainY)) return false;
        if (worldPos.y >= terrainY - UndergroundEnterMarginY) return true;
        if (IsVoxelAir(worldPos)) return true;
        bool chunkReady = TryGetChunkAt(worldPos, out var data) && data.Chunk != null;
        return !chunkReady && IsPositionInKnownDig(worldPos);
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
        TryImportSidecar();
        TrySubscribeWorldSave();

        _scanTimer += Time.deltaTime;
        if (_scanTimer >= ScanInterval) {
            _scanTimer = 0f;
            ScanHeightmaps();
        }
        ReseedDirtyChunks();
        FlushNavDirty();
        UpdateWaterStreams();
        Water.BucketLedger.Tick();
        WaterBucket.SyncPeriodically();
        UpdateUndergroundCollision();
        HandleInput();
    }
}
