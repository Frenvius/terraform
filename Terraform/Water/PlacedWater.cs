using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Terraform.Configs;
using Terraform.Patches;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Terraform.Water;

public sealed class PlacedWater : MonoBehaviour {
    public const string PrefabName = "tfm_placed_water";
    public const float NoWater = -10000f;
    private const int Width = 32;
    private const int Size = Width + 1;
    public const float Scale = 0.5f;
    private const float HalfSize = Width * Scale * 0.5f;
    private const float Pitch = (Width - 1) * Scale;
    private const float Range = BucketWaterMath.MaxDrop;
    private const float BlockedFloor = Range * 4f;
    private const float LandingTolerance = 1f;
    private const float ExchangeInterval = 0.05f;
    private const float EmptyLifetime = 10f;
    private const int MaxFloorLayers = 8;
    private static readonly int WaterTimeId = Shader.PropertyToID("_WaterTime");
    private static readonly int DepthFadeId = Shader.PropertyToID("_DepthFade");
    private static readonly (int x, int z)[] Directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly ConditionalWeakTable<LiquidVolume, PlacedWater> Volumes = new();
    private static readonly List<PlacedWater> Instances = new();
    private static readonly AccessTools.FieldRef<LiquidVolume, List<float>> Depths = AccessTools.FieldRefAccess<LiquidVolume, List<float>>("m_depths");
    private static readonly AccessTools.FieldRef<LiquidVolume, List<float>> Heights = AccessTools.FieldRefAccess<LiquidVolume, List<float>>("m_heights");
    private static readonly AccessTools.FieldRef<LiquidVolume, object> DataLock = AccessTools.FieldRefAccess<LiquidVolume, object>("m_meshDataLock");
    private static readonly AccessTools.FieldRef<LiquidVolume, bool> Dirty = AccessTools.FieldRefAccess<LiquidVolume, bool>("m_dirty");
    private static readonly AccessTools.FieldRef<LiquidVolume, bool> DirtyMesh = AccessTools.FieldRefAccess<LiquidVolume, bool>("m_dirtyMesh");
    private static readonly AccessTools.FieldRef<LiquidVolume, bool> NeedsSaving = AccessTools.FieldRefAccess<LiquidVolume, bool>("m_needsSaving");
    private static readonly AccessTools.FieldRef<LiquidVolume, uint> Revision = AccessTools.FieldRefAccess<LiquidVolume, uint>("m_lastDataRevision");
    private static readonly AccessTools.FieldRef<LiquidVolume, Mesh> MeshField = AccessTools.FieldRefAccess<LiquidVolume, Mesh>("m_mesh");
    private static readonly AccessTools.FieldRef<LiquidVolume, float> UpdateDelay = AccessTools.FieldRefAccess<LiquidVolume, float>("m_updateDelayTimer");
    private static GameObject _prefab;
    private static GameObject _prefabRoot;
    private static Material _material;
    private static int _groundMask;
    private static int _tickFrame;
    private static float _exchangeTimer;
    private LiquidVolume _volume;
    private ZNetView _view;
    private MeshRenderer _renderer;
    private Vector3 _origin;
    private bool _loadFailed;
    private bool _haveGround;
    private int _row;
    private float _probeY;
    private float _keepAlive;
    private readonly bool[] _blockedRight = new bool[Size * Size];
    private readonly bool[] _blockedForward = new bool[Size * Size];
    private readonly bool[] _nearWater = new bool[Size * Size];
    private readonly List<Vector3> _vertices = new(Size * Size);
    private readonly List<Vector2> _uvs = new(Size * Size);
    private readonly List<int> _triangles = new(Width * Width * 6);

    public static bool TryGet(LiquidVolume volume, out PlacedWater water) => Volumes.TryGetValue(volume, out water);

    public static int GroundMask {
        get {
            if (_groundMask == 0) _groundMask = VoxelGroundProbe.TerrainAndPieceMask
                | LayerMask.GetMask("static_solid", "Default", "Default_small");
            return _groundMask;
        }
    }

    private bool Usable => !_loadFailed && _volume != null && _view != null && _view.IsValid() && _view.IsOwner();

    public static void Register(ZNetScene scene) {
        if (_prefab == null) {
            var source = scene.m_prefabs.Find(p => p != null && p.name == "TarLiquid");
            if (source == null) {
                TerraformPlugin.Log.LogError("[water] TarLiquid is unavailable; bucket pouring is disabled.");
                return;
            }
            _prefabRoot = new GameObject("TerraformWaterPrefabs");
            _prefabRoot.SetActive(false);
            Object.DontDestroyOnLoad(_prefabRoot);
            _prefab = Object.Instantiate(source, _prefabRoot.transform);
            _prefab.name = PrefabName;
            _prefab.SetActive(true);
            _prefab.GetComponent<ZNetView>().m_persistent = true;
            var liquid = _prefab.GetComponent<LiquidVolume>();
            liquid.m_width = Width;
            liquid.m_scale = Scale;
            liquid.m_maxDepth = Range;
            liquid.m_liquidType = LiquidType.Water;
            liquid.m_initialVolume = 0f;
            liquid.m_initialArea = 1;
            liquid.m_viscocity = 1f;
            liquid.m_noiseHeight = 0f;
            liquid.m_collider = null;
            liquid.m_randomEffectInterval = float.MaxValue;
            liquid.m_randomEffectList = new EffectList { m_effectPrefabs = Array.Empty<EffectList.EffectData>() };
            foreach (var effect in _prefab.GetComponentsInChildren<Aoe>(true)) effect.enabled = false;
            foreach (var collider in _prefab.GetComponentsInChildren<Collider>(true)) {
                if (collider is BoxCollider box && collider.GetComponent<LiquidSurface>() != null) {
                    box.center = Vector3.zero;
                    box.size = new Vector3(Width * Scale, Range * 2f, Width * Scale);
                } else collider.enabled = false;
            }
            var renderer = _prefab.GetComponent<MeshRenderer>();
            renderer.enabled = false;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            _prefab.AddComponent<PlacedWater>();
        }
        _prefab.SetActive(true);
        if (!scene.m_prefabs.Contains(_prefab)) scene.m_prefabs.Add(_prefab);
    }

    public void Attach(LiquidVolume volume) {
        _volume = volume;
        _view = GetComponent<ZNetView>();
        _renderer = GetComponent<MeshRenderer>();
        _origin = transform.position;
        _keepAlive = Time.time;
        Volumes.Add(volume, this);
        Instances.Add(this);
    }

    private void OnDestroy() => Instances.Remove(this);

    private void Update() {
        Tick();
        if (_volume == null) return;
        if (!_renderer.enabled && _material != null) {
            _renderer.sharedMaterial = _material;
            _renderer.enabled = true;
        }
        if (_loadFailed || !Monitor.TryEnter(DataLock(_volume))) return;
        try {
            ProbeRows(1);
        } finally {
            Monitor.Exit(DataLock(_volume));
        }
    }

    private static void Tick() {
        if (_tickFrame == Time.frameCount) return;
        _tickFrame = Time.frameCount;
        UpdateMaterial();
        _exchangeTimer += Time.deltaTime;
        if (_exchangeTimer < ExchangeInterval) return;
        _exchangeTimer = 0f;
        for (int i = Instances.Count - 1; i >= 0; i--) Instances[i].Exchange();
    }

    private static void UpdateMaterial() {
        if (_material == null) {
            foreach (var water in WaterVolume.Instances) {
                if (water == null || water.m_waterSurface == null || water.m_waterSurface.sharedMaterial == null) continue;
                _material = new Material(water.m_waterSurface.sharedMaterial) { name = "TerraformPlacedWater" };
                _material.SetFloat("_UseGlobalWind", 0f);
                _material.SetFloatArray("_depth", new[] { 1f, 1f, 1f, 1f });
                _material.SetColor("_ColorTop", _material.GetColor("_ColorBottom"));
                _material.SetColor("_FoamColor", Color.clear);
                _material.SetFloat("_ShoreFade", 0.03f);
                _material.SetFloat("_IsLod", 0f);
                _material.SetFloat("_VisibleMaxDistance", 100000f);
                _material.SetFloat("_WaterEdge", 100000f);
                break;
            }
            if (_material == null) return;
        }
        _material.SetFloat(WaterTimeId, Time.time);
        _material.SetFloat(DepthFadeId, ModConfig.WaterOpaqueDepth.Value);
    }

    public static void SaveAll() {
        foreach (var water in Instances) if (water != null) water.Save();
    }

    public void Save() {
        if (!Usable) return;
        try {
            lock (DataLock(_volume)) {
                var bytes = Utils.Compress(BucketWaterMath.Encode(Depths(_volume)));
                _view.GetZDO().Set(ZDOVars.s_liquidData, bytes);
                Revision(_volume) = _view.GetZDO().DataRevision;
            }
        } catch (Exception ex) {
            NeedsSaving(_volume) = true;
            TerraformPlugin.Log.LogError($"[water] Saving placed water failed: {ex.Message}");
        }
    }

    public void Load() {
        if (_view == null || !_view.IsValid()) return;
        Revision(_volume) = _view.GetZDO().DataRevision;
        var bytes = _view.GetZDO().GetByteArray(ZDOVars.s_liquidData);
        if (bytes == null) return;
        try {
            var restored = BucketWaterMath.Decode(Utils.Decompress(bytes), Size * Size);
            lock (DataLock(_volume)) {
                var depths = Depths(_volume);
                for (int i = 0; i < depths.Count; i++) depths[i] = restored[i];
                Dirty(_volume) = true;
                NeedsSaving(_volume) = false;
            }
            _loadFailed = false;
        } catch (Exception ex) {
            _loadFailed = true;
            TerraformPlugin.Log.LogError($"[water] Cannot load placed water; its saved data will not be overwritten: {ex.Message}");
        }
    }

    private Vector3 SamplePoint(int index, float y) =>
        new(_origin.x + index % Size * Scale - HalfSize, y, _origin.z + index / Size * Scale - HalfSize);

    private void ProbeRows(int count) {
        var heights = Heights(_volume);
        var depths = Depths(_volume);
        for (; count > 0; count--) {
            if (_row == 0) {
                if (Heightmap.FindHeightmap(_origin) == null) return;
                _probeY = _haveGround
                    ? BucketWaterMath.ProbeHeight(heights, depths, _origin.y, BlockedFloor)
                    : _origin.y + 0.05f;
            }
            bool changed = false;
            for (int x = 0; x <= Width; x++) {
                int index = _row * Size + x;
                float floor = ProbeFloor(x, _row, heights[index], depths[index] > 0f);
                changed |= Mathf.Abs(heights[index] - floor) > 0.001f;
                heights[index] = floor;
                if (depths[index] <= BucketWaterMath.DryDepth) continue;
                var ground = SamplePoint(index, _origin.y + floor);
                if (GetNaturalLevel(ground) <= ground.y) continue;
                depths[index] = 0f;
                NeedsSaving(_volume) = true;
                changed = true;
            }
            for (int x = 0; _row > 0 && x < Width; x++) {
                int index = (_row - 1) * Size + x;
                _blockedRight[index] = IsEdgeBlocked(index, index + 1);
                _blockedForward[index] = IsEdgeBlocked(index, index + Size);
            }
            if (changed) Dirty(_volume) = true;
            if (++_row <= Width) continue;
            _row = 0;
            _haveGround = true;
        }
    }

    private float ProbeFloor(int x, int z, float current, bool wet) {
        if (x == 0 || z == 0 || x == Width || z == Width) return BlockedFloor;
        var start = SamplePoint(z * Size + x, _origin.y + Range);
        float bottom = _origin.y - Range;
        float floor = BlockedFloor;
        for (int layer = 0; layer < MaxFloorLayers && start.y > bottom; layer++) {
            if (!Physics.Raycast(start, Vector3.down, out var hit, start.y - bottom, GroundMask, QueryTriggerInteraction.Ignore)) break;
            floor = hit.point.y - _origin.y;
            if (hit.point.y <= _probeY) break;
            start.y = hit.point.y - 0.1f;
        }
        return wet && floor >= BlockedFloor ? current : floor;
    }

    private bool IsEdgeBlocked(int first, int second) {
        var heights = Heights(_volume);
        var depths = Depths(_volume);
        if (heights[first] >= BlockedFloor || heights[second] >= BlockedFloor) return true;
        float y = _origin.y + Mathf.Max(heights[first] + depths[first], heights[second] + depths[second]) + 0.02f;
        return Physics.Linecast(SamplePoint(first, y), SamplePoint(second, y), GroundMask, QueryTriggerInteraction.Ignore);
    }

    private bool CanFlow(int first, int second) {
        if (_loadFailed || !_haveGround) return false;
        return second == first + 1 ? !_blockedRight[first] : !_blockedForward[first];
    }

    private bool CanFlowEitherWay(int first, int second) => first < second ? CanFlow(first, second) : CanFlow(second, first);

    public void Flow(int first, int second) {
        if (!CanFlow(first, second)) return;
        var heights = Heights(_volume);
        var depths = Depths(_volume);
        float moved = BucketWaterMath.Transfer(heights[first], depths[first], heights[second], depths[second]);
        if (moved <= 0f) moved = -BucketWaterMath.Transfer(heights[second], depths[second], heights[first], depths[first]);
        if (moved == 0f) return;
        depths[first] -= moved;
        depths[second] += moved;
        Touch();
    }

    private void Touch() {
        Dirty(_volume) = true;
        NeedsSaving(_volume) = true;
    }

    private void Changed() {
        _keepAlive = Time.time;
        Touch();
        Save();
    }

    private void Exchange() {
        if (!Usable || !_haveGround || !Monitor.TryEnter(DataLock(_volume))) return;
        try {
            foreach (var direction in Directions) Push(direction.x, direction.z);
            foreach (float depth in Depths(_volume)) {
                if (depth <= BucketWaterMath.DryDepth) continue;
                _keepAlive = Time.time;
                break;
            }
        } finally {
            Monitor.Exit(DataLock(_volume));
        }
        if (Time.time - _keepAlive > EmptyLifetime) _view.Destroy();
    }

    private static int EdgeIndex(int dx, int dz, int i, bool mine) {
        int edge = (dx + dz > 0) == mine ? Width - 1 : 1;
        return dx != 0 ? i * Size + edge : edge * Size + i;
    }

    private void Push(int dx, int dz) {
        var heights = Heights(_volume);
        var depths = Depths(_volume);
        int first = 1;
        while (first < Width && depths[EdgeIndex(dx, dz, first, true)] <= BucketWaterMath.DryDepth) first++;
        if (first == Width) return;
        int wet = EdgeIndex(dx, dz, first, true);
        var center = new Vector3(_origin.x + dx * Pitch, _origin.y + heights[wet] + depths[wet], _origin.z + dz * Pitch);
        var neighbor = FindOwner(center);
        if (neighbor == null) {
            Spawn(center);
            return;
        }
        neighbor._keepAlive = Time.time;
        if (!neighbor.Usable || !neighbor._haveGround || !Monitor.TryEnter(DataLock(neighbor._volume))) return;
        try {
            var theirHeights = Heights(neighbor._volume);
            var theirDepths = Depths(neighbor._volume);
            float lift = _origin.y - neighbor._origin.y;
            bool moved = false;
            for (int i = first; i < Width; i++) {
                int mine = EdgeIndex(dx, dz, i, true);
                int theirs = EdgeIndex(dx, dz, i, false);
                if (heights[mine] >= BlockedFloor || theirHeights[theirs] >= BlockedFloor) continue;
                float amount = BucketWaterMath.Transfer(heights[mine] + lift, depths[mine], theirHeights[theirs], theirDepths[theirs]);
                if (amount <= 0f) continue;
                float y = _origin.y + heights[mine] + depths[mine] + 0.02f;
                if (Physics.Linecast(SamplePoint(mine, y), neighbor.SamplePoint(theirs, y), GroundMask, QueryTriggerInteraction.Ignore)) continue;
                depths[mine] -= amount;
                theirDepths[theirs] += amount;
                moved = true;
            }
            if (!moved) return;
            Touch();
            neighbor.Touch();
        } finally {
            Monitor.Exit(DataLock(neighbor._volume));
        }
    }

    public void BuildMeshData() {
        if (!_haveGround) return;
        var heights = Heights(_volume);
        var depths = Depths(_volume);
        _vertices.Clear();
        _uvs.Clear();
        _triangles.Clear();
        Array.Clear(_nearWater, 0, _nearWater.Length);
        for (int z = 1; z < Width; z++)
        for (int x = 1; x < Width; x++) {
            if (depths[z * Size + x] <= BucketWaterMath.DryDepth) continue;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++) _nearWater[(z + dz) * Size + x + dx] = true;
        }
        for (int z = 0; z <= Width; z++)
        for (int x = 0; x <= Width; x++) {
            int index = z * Size + x;
            float y = BucketWaterMath.Surface(heights, depths, index, Width);
            _vertices.Add(new Vector3(x * Scale - HalfSize, y, z * Scale - HalfSize));
            _uvs.Add(new Vector2(x / (float)Width, z / (float)Width));
        }
        for (int z = 0; z < Width; z++)
        for (int x = 0; x < Width; x++) {
            int a = z * Size + x, b = a + 1, c = a + Size, d = c + 1;
            AddTriangle(a, c, b);
            AddTriangle(b, c, d);
        }
        DirtyMesh(_volume) = true;

        void AddTriangle(int a, int b, int c) {
            if (!_nearWater[a] || !_nearWater[b] || !_nearWater[c]) return;
            _triangles.Add(a);
            _triangles.Add(b);
            _triangles.Add(c);
        }
    }

    public void ApplyMesh() {
        var mesh = MeshField(_volume);
        mesh.Clear();
        mesh.SetVertices(_vertices);
        mesh.SetUVs(0, _uvs);
        mesh.SetTriangles(_triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    private static float Snap(float value) => Mathf.Round(value / Pitch) * Pitch;

    private bool ContainsXZ(Vector3 point) => Mathf.Abs(point.x - _origin.x) < HalfSize
        && Mathf.Abs(point.z - _origin.z) < HalfSize;

    private bool Owns(Vector3 point) => Mathf.Abs(point.x - _origin.x) <= Pitch * 0.5f
        && Mathf.Abs(point.z - _origin.z) <= Pitch * 0.5f && Mathf.Abs(point.y - _origin.y) <= Range;

    private static PlacedWater FindOwner(Vector3 point) {
        PlacedWater best = null;
        foreach (var water in Instances) {
            if (!water.Owns(point)) continue;
            if (best == null || Mathf.Abs(water._origin.y - point.y) < Mathf.Abs(best._origin.y - point.y)) best = water;
        }
        return best;
    }

    private int IndexAt(Vector3 point) {
        int x = Mathf.Clamp(Mathf.RoundToInt((point.x - _origin.x + HalfSize) / Scale), 1, Width - 1);
        int z = Mathf.Clamp(Mathf.RoundToInt((point.z - _origin.z + HalfSize) / Scale), 1, Width - 1);
        return z * Size + x;
    }

    private int LandingIndex(Vector3 landing) {
        if (!Usable) return -1;
        if (!_haveGround) {
            lock (DataLock(_volume)) {
                _row = 0;
                ProbeRows(Size);
            }
        }
        if (!_haveGround) return -1;
        var heights = Heights(_volume);
        int nearest = IndexAt(landing);
        int best = -1;
        float bestError = LandingTolerance;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++) {
            int index = nearest + dz * Size + dx;
            float error = Mathf.Abs(_origin.y + heights[index] - landing.y);
            if (error > bestError || (error == bestError && index != nearest)) continue;
            best = index;
            bestError = error;
        }
        return best;
    }

    public float Sample(Vector3 point) {
        if (_loadFailed || !_haveGround || !ContainsXZ(point)) return NoWater;
        lock (DataLock(_volume)) {
            float x = (point.x - _origin.x + HalfSize) / Scale;
            float z = (point.z - _origin.z + HalfSize) / Scale;
            int ix = (int)x, iz = (int)z;
            int a = iz * Size + ix, b = a + 1, c = a + Size, d = c + 1;
            var depths = Depths(_volume);
            var heights = Heights(_volume);
            float tx = x - ix, tz = z - iz;
            float depth = Interpolate(depths[a], depths[b], depths[c], depths[d]);
            if (depth <= BucketWaterMath.DryDepth) return NoWater;
            float level = _origin.y + Interpolate(BucketWaterMath.Surface(heights, depths, a, Width),
                BucketWaterMath.Surface(heights, depths, b, Width), BucketWaterMath.Surface(heights, depths, c, Width),
                BucketWaterMath.Surface(heights, depths, d, Width));
            return point.y < level - depth - 0.5f ? NoWater : level;

            float Interpolate(float va, float vb, float vc, float vd) => tx + tz <= 1f
                ? va + (vb - va) * tx + (vc - va) * tz
                : vd + (vc - vd) * (1f - tx) + (vb - vd) * (1f - tz);
        }
    }

    public static float GetLevel(Component target) => target == null || Instances.Count == 0
        ? NoWater : GetLevel(target.transform.position);

    public static float GetLevel(Vector3 point) {
        float level = NoWater;
        foreach (var water in Instances) if (water != null) level = Mathf.Max(level, water.Sample(point));
        return level;
    }

    public static float GetCombinedLevel(Vector3 point) => Mathf.Max(GetLevel(point), GetNaturalLevel(point));

    private static float GetNaturalLevel(Vector3 point) {
        float level = NoWater;
        foreach (var water in WaterVolume.Instances) {
            if (water == null) continue;
            var collider = water.GetComponent<Collider>();
            if (collider != null && collider.bounds.Contains(point)) level = Mathf.Max(level, water.GetWaterSurface(point));
        }
        return level;
    }

    public static bool TryCollect(Vector3 point, float requested, out float moved) {
        moved = 0f;
        foreach (var water in Instances) {
            if (water == null || !water.Usable) continue;
            float level = water.Sample(point);
            if (level <= NoWater || point.y > level + 0.1f) continue;
            lock (DataLock(water._volume)) {
                moved = BucketWaterMath.Withdraw(Depths(water._volume), water.IndexAt(point), requested, water.CanFlowEitherWay);
                if (moved > 0f) water.Changed();
            }
            if (moved > 0f) return true;
        }
        foreach (var water in WaterVolume.Instances) {
            if (water == null) continue;
            var collider = water.GetComponent<Collider>();
            if (collider == null || !collider.bounds.Contains(point)) continue;
            float level = water.GetWaterSurface(point);
            if (level <= NoWater || point.y > level + 0.1f) continue;
            moved = requested;
            return true;
        }
        return false;
    }

    private static PlacedWater Spawn(Vector3 position) {
        _prefab.SetActive(true);
        var spawned = Object.Instantiate(_prefab, position, Quaternion.identity);
        spawned.SetActive(true);
        var water = spawned.GetComponent<PlacedWater>();
        UpdateDelay(water._volume) = 2f;
        return water;
    }

    public static float Pour(Vector3 landing, float amount) {
        if (_prefab == null) return 0f;
        if (GetNaturalLevel(landing) > landing.y) return amount;
        PlacedWater target = null;
        int index = -1;
        foreach (var water in Instances) {
            if (water == null || !water.Owns(landing)) continue;
            index = water.LandingIndex(landing);
            if (index < 0) continue;
            target = water;
            break;
        }
        if (target == null) {
            target = Spawn(new Vector3(Snap(landing.x), landing.y + 0.05f, Snap(landing.z)));
            index = target.LandingIndex(landing);
        }
        if (index < 0) {
            TerraformPlugin.Log.LogWarning($"[water-place] No floor sample matches landing={landing:F3} origin={target._origin:F3} usable={target.Usable} ground={target._haveGround}");
            return 0f;
        }
        lock (DataLock(target._volume)) {
            float moved = BucketWaterMath.Deposit(Depths(target._volume), index, amount, target.CanFlowEitherWay,
                Heights(target._volume));
            if (moved > 0f) target.Changed();
            return moved;
        }
    }
}

[HarmonyPatch(typeof(LiquidVolume), "Awake")]
internal static class PlacedWaterAwakePatch {
    private static void Prefix(LiquidVolume __instance) {
        var water = __instance.GetComponent<PlacedWater>();
        if (water != null) water.Attach(__instance);
    }
}

[HarmonyPatch(typeof(LiquidVolume), "UpdateHeights")]
internal static class PlacedWaterGroundPatch {
    private static bool Prefix(LiquidVolume __instance) => !PlacedWater.TryGet(__instance, out _);
}

[HarmonyPatch(typeof(LiquidVolume), "EvenDepth")]
internal static class PlacedWaterFlowPatch {
    private static bool Prefix(LiquidVolume __instance, int index0, int index1) {
        if (!PlacedWater.TryGet(__instance, out var water)) return true;
        water.Flow(index0, index1);
        return false;
    }
}

[HarmonyPatch(typeof(LiquidVolume), "PrebuildMesh")]
internal static class PlacedWaterMeshDataPatch {
    private static bool Prefix(LiquidVolume __instance) {
        if (!PlacedWater.TryGet(__instance, out var water)) return true;
        water.BuildMeshData();
        return false;
    }
}

[HarmonyPatch(typeof(LiquidVolume), "PostBuildMesh")]
internal static class PlacedWaterMeshPatch {
    private static bool Prefix(LiquidVolume __instance) {
        if (!PlacedWater.TryGet(__instance, out var water)) return true;
        water.ApplyMesh();
        return false;
    }
}

[HarmonyPatch(typeof(LiquidVolume), nameof(LiquidVolume.GetSurface))]
internal static class PlacedWaterSurfacePatch {
    private static bool Prefix(LiquidVolume __instance, Vector3 p, ref float __result) {
        if (!PlacedWater.TryGet(__instance, out _)) return true;
        __result = PlacedWater.GetCombinedLevel(p);
        return false;
    }
}

[HarmonyPatch(typeof(LiquidVolume), "Save")]
internal static class PlacedWaterSavePatch {
    private static bool Prefix(LiquidVolume __instance) {
        if (!PlacedWater.TryGet(__instance, out var water)) return true;
        water.Save();
        return false;
    }
}

[HarmonyPatch(typeof(LiquidVolume), "Load")]
internal static class PlacedWaterLoadPatch {
    private static bool Prefix(LiquidVolume __instance) {
        if (!PlacedWater.TryGet(__instance, out var water)) return true;
        water.Load();
        return false;
    }
}

[HarmonyPatch(typeof(LiquidVolume), "OnDestroy")]
internal static class PlacedWaterDestroyPatch {
    private static void Prefix(LiquidVolume __instance) {
        if (PlacedWater.TryGet(__instance, out var water)) water.Save();
    }
}
