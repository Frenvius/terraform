using System.Collections.Generic;
using UnityEngine;
using Terraform.Assets;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private readonly Dictionary<int, GameObject> _waterLeaks = new();
    private readonly Dictionary<int, List<GameObject>> _leakTopVfx = new();
    private readonly Dictionary<int, List<LidCircle>> _leakCircleCache = new();
    private readonly Dictionary<long, GameObject> _waterfallInstances = new();
    private readonly Dictionary<long, GameObject> _waterfallBottomAnchors = new();
    private readonly HashSet<long> _firedSplashKeys = new();
    private GameObject _vfxWaterSurfacePrefab;
    private GameObject _fxWaterImpactPrefab;
    private bool _prefabLookupAttempted;

    private const float LeakSplashScale = 0.55f;
    private const float WaterfallScaleXZ = 1.5f;
    private const float WaterfallUVScrollSpeed = 0.95f;
    private const float WaterfallAudioMaxDistance = 60f;
    private float _prefabIntrinsicHeight;
    private float _prefabOriginToTopOffset;
    private bool _prefabBoundsMeasured;

    private struct LidCircle {
        public float CenterX;
        public float CenterZ;
        public float TopY;
        public float BottomY;
        public float MeshBottomY;
    }

    private void RefreshWaterLeaks() {
        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        var alive = new HashSet<int>();
        var aliveWaterfallKeys = new HashSet<long>();

        foreach (var kvChunk in _chunks) {
            int chunkId = kvChunk.Key;
            var chunkData = kvChunk.Value;
            if (chunkData?.MeshObject == null) continue;
            if (chunkData.Heightmap == null) continue;

            var circles = CollectLidCirclesForChunk(chunkData, waterY);
            if (circles == null || circles.Count == 0) continue;

            alive.Add(chunkId);
            foreach (var c in circles) aliveWaterfallKeys.Add(WaterfallKey(c.CenterX, c.CenterZ));

            EnsureWaterfallObjects(chunkId, circles);
            EnsureLeakTopVfx(chunkId, circles);
            FireNewLeakSplashes(circles);
            _leakCircleCache[chunkId] = circles;
        }

        DestroyStaleWaterfalls(aliveWaterfallKeys);

        List<int> dead = null;
        foreach (var kv in _waterLeaks) {
            if (!alive.Contains(kv.Key) || kv.Value == null) {
                (dead ??= new List<int>()).Add(kv.Key);
            }
        }
        if (dead != null) {
            foreach (var key in dead) {
                if (_waterLeaks.TryGetValue(key, out var go) && go != null) Destroy(go);
                _waterLeaks.Remove(key);
                DestroyTopVfx(key);
                _leakCircleCache.Remove(key);
            }
        }

        DebugLog($"[water-leak] active={_waterLeaks.Count} waterfalls={_waterfallInstances.Count}");
    }

    private List<LidCircle> CollectLidCirclesForChunk(ChunkRenderData chunkData, float waterY) {
        Vector3 chunkOrigin = chunkData.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(chunkOrigin.x);
        int chunkOriginY = Mathf.RoundToInt(chunkOrigin.y);
        int chunkOriginZ = Mathf.RoundToInt(chunkOrigin.z);
        long chunkKey = ChunkKey(chunkOriginX, chunkOriginZ);

        if (!_chunkDigHistory.TryGetValue(chunkKey, out var digs) || digs.Count == 0) return null;

        float pickaxeReach = PickaxeRadius + PickaxeNoiseAmplitude;
        List<LidCircle> circles = null;

        foreach (var d in digs) {
            if (d.Mode == DigMode.Surface) continue;
            if (!Heightmap.GetHeight(d.WorldPos, out float terrainY)) continue;
            if (terrainY >= waterY) continue;
            if (Mathf.Abs(d.WorldPos.y - terrainY) > pickaxeReach + 0.5f) continue;

            int localX = Mathf.RoundToInt(d.WorldPos.x) - chunkOriginX;
            int localZ = Mathf.RoundToInt(d.WorldPos.z) - chunkOriginZ;
            if (localX < 0 || localX > VoxelChunk.SIZE) continue;
            if (localZ < 0 || localZ > VoxelChunk.SIZE) continue;

            int deepest = SampleDeepestAirAround(chunkData, localX, localZ);
            if (deepest < 0) continue;

            float bottomY = chunkOriginY + deepest;
            float topY = terrainY + 0.1f;
            if (topY <= bottomY) continue;

            (circles ??= new List<LidCircle>()).Add(new LidCircle {
                CenterX = d.WorldPos.x,
                CenterZ = d.WorldPos.z,
                TopY = topY,
                BottomY = bottomY,
                MeshBottomY = chunkOriginY,
            });
        }

        return circles;
    }

    private static int SampleDeepestAirAround(ChunkRenderData chunkData, int localX, int localZ) {
        if (chunkData.Chunk == null) return -1;
        int deepest = -1;
        for (int dz = -1; dz <= 1; dz++) {
            for (int dx = -1; dx <= 1; dx++) {
                int sx = localX + dx;
                int sz = localZ + dz;
                if (sx < 0 || sx > VoxelChunk.SIZE) continue;
                if (sz < 0 || sz > VoxelChunk.SIZE) continue;
                for (int yl = 0; yl <= VoxelChunk.SIZE; yl++) {
                    if (chunkData.Chunk.Get(sx, yl, sz).Value > 0f) {
                        if (deepest < 0 || yl < deepest) deepest = yl;
                        break;
                    }
                }
            }
        }
        return deepest;
    }

    private void EnsureWaterfallObjects(int chunkId, List<LidCircle> circles) {
        var prefab = WaterfallAssets.AlphaWaterfall;
        if (prefab == null) {
            if (_waterLeaks.TryGetValue(chunkId, out var stale) && stale != null) Destroy(stale);
            _waterLeaks.Remove(chunkId);
            return;
        }

        if (!_waterLeaks.TryGetValue(chunkId, out var parent) || parent == null) {
            parent = new GameObject("TerraformWaterfalls");
            _waterLeaks[chunkId] = parent;
        }

        MeasurePrefabBoundsIfNeeded(prefab);
        if (_prefabIntrinsicHeight <= 0f) return;

        foreach (var c in circles) {
            long key = WaterfallKey(c.CenterX, c.CenterZ);
            float height = Mathf.Max(0.5f, c.TopY - c.MeshBottomY);
            float scaleY = height / _prefabIntrinsicHeight;
            float anchorY = c.TopY - _prefabOriginToTopOffset * scaleY;
            var pos = new Vector3(c.CenterX, anchorY, c.CenterZ);
            var scale = new Vector3(WaterfallScaleXZ, scaleY, WaterfallScaleXZ);

            if (_waterfallInstances.TryGetValue(key, out var container) && container != null) {
                container.transform.position = pos;
                foreach (Transform child in container.transform) {
                    child.localScale = scale;
                    UpdateScrollerSpeeds(child.gameObject, scaleY);
                }
                UpdateBottomAnchor(key, c);
                continue;
            }

            container = new GameObject($"WaterfallContainer_{key}");
            container.transform.SetParent(parent.transform, worldPositionStays: false);
            container.transform.position = pos;

            GameObject firstInstance = null;
            for (int rot = 0; rot < 2; rot++) {
                var rotation = Quaternion.Euler(0f, rot * 90f, 0f);
                var inst = Instantiate(prefab, container.transform);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = rotation;
                inst.transform.localScale = scale;
                inst.name = $"WaterfallSheet_{rot}";
                AttachUVScrollers(inst);
                UpdateScrollerSpeeds(inst, scaleY);
                StripAudioFromInstance(inst);
                if (rot == 0) firstInstance = inst;
            }

            _waterfallInstances[key] = container;
            SetupBottomAnchor(key, c, parent, firstInstance);
            LogSpawnedBounds(container, c, pos, scaleY);
        }
    }

    private void MeasurePrefabBoundsIfNeeded(GameObject prefab) {
        if (_prefabBoundsMeasured) return;
        _prefabBoundsMeasured = true;

        var temp = Instantiate(prefab);
        temp.SetActive(true);
        var renderers = temp.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0) {
            TerraformPlugin.Log.LogError("[waterfall-bounds] prefab has no MeshRenderers");
            Destroy(temp);
            return;
        }
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        _prefabIntrinsicHeight = b.size.y;
        _prefabOriginToTopOffset = b.max.y - temp.transform.position.y;
        TerraformPlugin.Log.LogInfo($"[waterfall-bounds] mesh intrinsic height={_prefabIntrinsicHeight:F2}, originToTop={_prefabOriginToTopOffset:F2}, bounds.min.y={b.min.y:F2}, bounds.max.y={b.max.y:F2}, pos.y={temp.transform.position.y:F2}, meshRenderers={renderers.Length}");
        Destroy(temp);
    }

    private static void LogSpawnedBounds(GameObject inst, LidCircle c, Vector3 spawnPos, float scaleY) {
        var renderers = inst.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0) return;
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        TerraformPlugin.Log.LogInfo($"[waterfall-spawn] pos.y={spawnPos.y:F2} scaleY={scaleY:F2} | expected[bottomY={c.BottomY:F2} topY={c.TopY:F2}] | actual[min.y={b.min.y:F2} max.y={b.max.y:F2}]");
    }

    private void DestroyStaleWaterfalls(HashSet<long> aliveKeys) {
        List<long> stale = null;
        foreach (var kv in _waterfallInstances) {
            if (!aliveKeys.Contains(kv.Key) || kv.Value == null) {
                (stale ??= new List<long>()).Add(kv.Key);
            }
        }
        if (stale == null) return;
        foreach (var key in stale) {
            if (_waterfallInstances.TryGetValue(key, out var go) && go != null) Destroy(go);
            _waterfallInstances.Remove(key);
            if (_waterfallBottomAnchors.TryGetValue(key, out var anchor) && anchor != null) Destroy(anchor);
            _waterfallBottomAnchors.Remove(key);
        }
    }

    private static long WaterfallKey(float worldX, float worldZ) {
        int x = Mathf.RoundToInt(worldX * 4f);
        int z = Mathf.RoundToInt(worldZ * 4f);
        return ((long)x << 32) | (uint)z;
    }

    private static void AttachUVScrollers(GameObject root) {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++) {
            var t = transforms[i];
            if (!t.name.StartsWith("waterfall_meash")) continue;
            if (t.gameObject.GetComponent<UVScroller>() != null) continue;
            var scroller = t.gameObject.AddComponent<UVScroller>();
            scroller.SpeedX = 0f;
        }
    }

    private static void UpdateScrollerSpeeds(GameObject root, float scaleY) {
        float compensatedSpeed = WaterfallUVScrollSpeed * Mathf.Max(1f, scaleY);
        foreach (var scroller in root.GetComponentsInChildren<UVScroller>(true)) {
            scroller.SpeedY = compensatedSpeed;
        }
    }

    private static bool _audioLogged;

    private static void StripAudioFromInstance(GameObject root) {
        foreach (var src in root.GetComponentsInChildren<AudioSource>(true)) {
            if (src != null) Destroy(src);
        }
    }

    private void SetupBottomAnchor(long key, LidCircle c, GameObject parent, GameObject sourceInstance) {
        if (sourceInstance == null) return;

        var anchor = new GameObject($"WaterfallBottomAnchor_{key}");
        anchor.transform.SetParent(parent.transform);
        anchor.transform.position = new Vector3(c.CenterX, c.BottomY, c.CenterZ);
        anchor.transform.localScale = Vector3.one;

        AudioClip clip = null;
        var originalSrc = sourceInstance.GetComponentInChildren<AudioSource>(true);
        if (originalSrc != null) {
            clip = originalSrc.clip;
            Destroy(originalSrc);
        }
        if (clip != null) {
            var newSrc = anchor.AddComponent<AudioSource>();
            newSrc.clip = clip;
            newSrc.loop = true;
            newSrc.playOnAwake = true;
            newSrc.volume = 1f;
            newSrc.spatialBlend = 1f;
            newSrc.rolloffMode = AudioRolloffMode.Logarithmic;
            newSrc.minDistance = 5f;
            newSrc.maxDistance = WaterfallAudioMaxDistance;
            newSrc.priority = 64;
            newSrc.mute = false;
            newSrc.enabled = true;
            newSrc.Stop();
            newSrc.Play();
            if (!_audioLogged) {
                _audioLogged = true;
                TerraformPlugin.Log.LogInfo($"[waterfall-audio] clip={clip.name} loadState={clip.loadState} playing={newSrc.isPlaying} volume={newSrc.volume} enabled={newSrc.enabled} active={anchor.activeInHierarchy}");
            }
        }

        var foamPrefab = GetVfxWaterSurfacePrefab();
        if (foamPrefab != null) {
            var foam = Instantiate(foamPrefab, anchor.transform);
            foam.transform.localPosition = Vector3.zero;
            foam.transform.localRotation = Quaternion.identity;
            foam.transform.localScale = Vector3.one;
            foam.name = "BottomFoam";
            StripNetworkComponents(foam);
        }

        _waterfallBottomAnchors[key] = anchor;
    }

    private void UpdateBottomAnchor(long key, LidCircle c) {
        if (!_waterfallBottomAnchors.TryGetValue(key, out var anchor) || anchor == null) return;
        anchor.transform.position = new Vector3(c.CenterX, c.BottomY, c.CenterZ);
    }

    private void EnsureLeakTopVfx(int chunkId, List<LidCircle> circles) {
        if (!_leakTopVfx.TryGetValue(chunkId, out var list)) {
            list = new List<GameObject>();
            _leakTopVfx[chunkId] = list;
        }
        foreach (var v in list) if (v != null) Destroy(v);
        list.Clear();

        var prefab = GetVfxWaterSurfacePrefab();
        if (prefab == null) return;

        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        foreach (var c in circles) {
            var pos = new Vector3(c.CenterX, waterY + 0.02f, c.CenterZ);
            var inst = Instantiate(prefab, pos, Quaternion.identity);
            inst.name = "TerraformLeakTopVfx";
            StripNetworkComponents(inst);
            list.Add(inst);
        }
    }

    private void DestroyTopVfx(int chunkId) {
        if (!_leakTopVfx.TryGetValue(chunkId, out var list)) return;
        foreach (var v in list) if (v != null) Destroy(v);
        _leakTopVfx.Remove(chunkId);
    }

    private void FireNewLeakSplashes(List<LidCircle> circles) {
        var prefab = GetFxWaterImpactBigPrefab();
        if (prefab == null) return;
        foreach (var c in circles) {
            long key = WaterfallKey(c.CenterX, c.CenterZ);
            if (!_firedSplashKeys.Add(key)) continue;
            var pos = new Vector3(c.CenterX, c.BottomY + 0.1f, c.CenterZ);
            var inst = Instantiate(prefab, pos, Quaternion.identity);
            inst.name = "TerraformLeakSplash";
            inst.transform.localScale = Vector3.one * LeakSplashScale;
            StripNetworkComponents(inst);
        }
    }

    private static void StripNetworkComponents(GameObject go) {
        var znv = go.GetComponent<ZNetView>();
        if (znv != null) Destroy(znv);
        foreach (var child in go.GetComponentsInChildren<ZNetView>(true)) {
            if (child != null) Destroy(child);
        }
    }

    private GameObject GetVfxWaterSurfacePrefab() {
        TryLoadPrefabs();
        return _vfxWaterSurfacePrefab;
    }

    private GameObject GetFxWaterImpactBigPrefab() {
        TryLoadPrefabs();
        return _fxWaterImpactPrefab;
    }

    private void TryLoadPrefabs() {
        if (_prefabLookupAttempted) return;
        _prefabLookupAttempted = true;
        _vfxWaterSurfacePrefab = FindPrefabByName("vfx_water_surface");
        _fxWaterImpactPrefab = FindPrefabByName("fx_WaterImpact_Big");
        TerraformPlugin.Log.LogInfo($"[leak-vfx] surface={(_vfxWaterSurfacePrefab != null)} impact={(_fxWaterImpactPrefab != null)} waterfall={(WaterfallAssets.AlphaWaterfall != null)}");
    }

    private static GameObject FindPrefabByName(string name) {
        if (ZNetScene.instance != null) {
            var p = ZNetScene.instance.GetPrefab(name);
            if (p != null) return p;
        }
        var all = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (var go in all) {
            if (go.name == name && go.transform.parent == null && !go.scene.IsValid()) {
                return go;
            }
        }
        return null;
    }

    private void DestroyAllWaterLeaks() {
        foreach (var kv in _waterLeaks) {
            if (kv.Value != null) Destroy(kv.Value);
        }
        _waterLeaks.Clear();
        foreach (var kv in _leakTopVfx) {
            foreach (var v in kv.Value) if (v != null) Destroy(v);
        }
        _leakTopVfx.Clear();
        foreach (var kv in _waterfallInstances) {
            if (kv.Value != null) Destroy(kv.Value);
        }
        _waterfallInstances.Clear();
        foreach (var kv in _waterfallBottomAnchors) {
            if (kv.Value != null) Destroy(kv.Value);
        }
        _waterfallBottomAnchors.Clear();
        _leakCircleCache.Clear();
    }
}
