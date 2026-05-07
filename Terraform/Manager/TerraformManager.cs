using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager : MonoBehaviour {
    public static TerraformManager Instance;

    private Dictionary<int, ChunkRenderData> _chunks;
    private readonly Dictionary<long, List<DigRecord>> _chunkDigHistory = new();
    private float _scanTimer;
    private const float ScanInterval = 1f;
    private const float UndergroundEnterMarginY = 1.0f;

    private const float NoiseFrequency        = 0.12f;
    private const float NoiseAmplitude        = 0.06f;
    private const float NoiseOffset           = 137.42f;
    private const int   SmoothingPasses       = 0;
    private const float OuterRadiusMultiplier = 1.55f;
    private const float BowlRadiusMultiplier  = 0.65f;
    private const float ShelfDepthMultiplier  = 0.4f;
    private const float RimFeatherExtra       = 1.0f;
    private const float BowlDepthMultiplier   = 1.0f;
    private const float BowlFalloffExponent   = 2f;
    private const float SeamInset             = 0.001f;
    private const float ShallowAboveKeep      = 0.35f;
    private const float MinCeilingThickness   = 1.0f;

    private const float PickaxeRadius          = 1.1f;
    private const float PickaxeNoiseFrequency  = 0.6f;
    private const float PickaxeNoiseAmplitude  = 0.35f;

    private const float ExtendedPunchBleed     = 2f;
    private const float CrossChunkPaintBleed   = 1.5f;

    private static bool VerboseLogs =>
        Configs.ModConfig.DebugMode != null && Configs.ModConfig.DebugMode.Value;

    private static void DebugLog(string message) {
        if (VerboseLogs) TerraformPlugin.Log.LogInfo(message);
    }

    private static long CellKey(int worldX, int worldZ) =>
        ((long)worldX << 32) ^ (uint)worldZ;

    private static long ChunkKey(int chunkOriginX, int chunkOriginZ) =>
        ((long)chunkOriginX << 32) ^ (uint)chunkOriginZ;

    private void Awake() {
        Instance         = this;
        _chunks          = new Dictionary<int, ChunkRenderData>();
        _debugGrids      = new Dictionary<int, GameObject>();
        _debugChunkWalls = new Dictionary<int, GameObject>();
    }

    private void OnDestroy() {
        if (Instance == this) Instance = null;
    }

    public bool IsPlayerInVoxelCavity(Vector3 worldPos) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return false;

        if (!Heightmap.GetHeight(worldPos, out float terrainY)) return false;
        if (worldPos.y >= terrainY - UndergroundEnterMarginY) return false;

        return _chunks.TryGetValue(hm.GetInstanceID(), out var data)
            && data.Renderer != null
            && data.Renderer.enabled;
    }

    private void Update() {
        _scanTimer += Time.deltaTime;
        if (_scanTimer >= ScanInterval) {
            _scanTimer = 0f;
            ScanHeightmaps();
        }
        UpdateUndergroundCollision();
        HandleInput();
    }
}
