using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using Terraform.Assets;
using Terraform.Water;

namespace Terraform.Patches;

public partial class TerraformManager {
    private readonly WaterStreamSolver _streamSolver = new();
    private readonly List<Vector3> _leakOpenings = new();
    private readonly Dictionary<int, List<Vector3>> _chunkLeakOpenings = new();
    private readonly Dictionary<long, GameObject> _leakTopVfx = new();
    private readonly Dictionary<long, GameObject> _landingAnchors = new();
    private readonly Dictionary<long, ParticleSystem> _fallEmitters = new();
    private readonly HashSet<long> _aliveFallKeys = new();
    private readonly List<WaterStreamFall> _audibleFalls = new();
    private AudioSource _streamAudio;
    private float _wetCheckTimer;
    private readonly HashSet<long> _firedSplashKeys = new();
    private readonly HashSet<long> _aliveVfxKeys = new();
    private readonly List<long> _staleVfxKeys = new();
    private GameObject _streamObject;
    private Mesh _streamMesh;
    private MeshRenderer _streamRenderer;
    private GameObject _vfxWaterSurfacePrefab;
    private GameObject _fxWaterImpactPrefab;
    private AudioClip _waterfallClip;
    private Material _fallParticleMaterial;
    private AudioMixerGroup _sfxMixerGroup;
    private bool _prefabLookupAttempted;
    private bool _waterStreamsDirty;
    private int _leakSignature;

    private const float LeakSplashScale = 0.55f;
    private const float LandingVfxMinFall = 1.5f;
    private const float LandingVfxSpacing = 2f;
    private const float WaterfallAudioMaxDistance = 60f;
    private const string WaterfallClipName = "rain_waterfall";
    private const float FallStartSpeed = 1f;
    private const float FallParticleRate = 300f;
    private const int FallMaxParticles = 2000;
    private const float FallWidth = WaterStreamSolver.CellSize * 1.8f;
    private const float WetCheckInterval = 0.5f;
    private const float PlayerWetRadius = 0.3f;
    private const float PoolWetHeight = 0.5f;
    private const float LeakRoofThickness = 1f;
    private const float LeakCavityMargin = 1f;
    private const float LeakColumnSpacing = 0.5f;
    private const float LeakProbeStep = 0.25f;

    private void RefreshWaterLeaks() {
        float waterY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
        CollectLeakOpenings(waterY);
        int signature = LeakSignature();
        if (!_waterStreamsDirty && signature == _leakSignature) return;
        _waterStreamsDirty = false;
        _leakSignature = signature;

        _streamSolver.Clear();
        foreach (var opening in _leakOpenings) _streamSolver.AddSource(opening);
        _streamSolver.Solve();
        RebuildStreamMesh();
        RefreshFallEmitters();
        RefreshLeakTopVfx(waterY);
        RefreshLandingAnchors();
        RefreshStreamAudio();

        if (VerboseLogs) DebugLog($"[water-stream] openings={_leakOpenings.Count} cells={_streamSolver.Cells.Count} falls={_streamSolver.Falls.Count}");
    }

    private void CollectLeakOpenings(float waterY) {
        _leakOpenings.Clear();
        if (_waterStreamsDirty) _chunkLeakOpenings.Clear();
        foreach (var pair in _chunks) {
            var data = pair.Value;
            if (data?.MeshObject == null || data.Heightmap == null) continue;
            if (!_chunkLeakOpenings.TryGetValue(pair.Key, out var openings)) {
                openings = FindChunkLeakOpenings(data, waterY);
                _chunkLeakOpenings[pair.Key] = openings;
            }
            _leakOpenings.AddRange(openings);
        }
    }

    private List<Vector3> FindChunkLeakOpenings(ChunkRenderData data, float waterY) {
        var openings = new List<Vector3>();
        Vector3 origin = data.MeshObject.transform.position;
        long chunkKey = ChunkKey(Mathf.RoundToInt(origin.x), Mathf.RoundToInt(origin.z));
        if (!_chunkDigHistory.TryGetValue(chunkKey, out var digs)) return openings;
        float pickaxeReach = PickaxeRadius + PickaxeNoiseAmplitude + 0.5f;
        foreach (var dig in digs) {
            if (dig.Mode != DigMode.UndergroundTunnel && dig.Mode != DigMode.OpenHoleDeepen) continue;
            if (!OriginalHeight(dig.WorldPos, out float terrainY) || terrainY >= waterY) continue;
            if (Mathf.Abs(dig.WorldPos.y - terrainY) > pickaxeReach) continue;
            if (!OpensPassageUnderWater(dig.WorldPos, pickaxeReach, waterY)) continue;
            openings.Add(new Vector3(dig.WorldPos.x, terrainY, dig.WorldPos.z));
        }
        return openings;
    }

    private bool OpensPassageUnderWater(Vector3 center, float reach, float waterY) {
        float radius = reach + LeakCavityMargin;
        float bottomY = center.y - radius;
        float breachBottom = float.PositiveInfinity;
        float cavityTop = float.NegativeInfinity;
        int steps = Mathf.CeilToInt(radius / LeakColumnSpacing);
        for (int iz = -steps; iz <= steps; iz++)
        for (int ix = -steps; ix <= steps; ix++) {
            float dx = ix * LeakColumnSpacing, dz = iz * LeakColumnSpacing;
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq > radius * radius) continue;
            var column = new Vector3(center.x + dx, center.y, center.z + dz);
            if (!OriginalHeight(column, out float bedY) || !TryGetChunkAt(column, out var data) || data.Chunk == null) continue;
            if (!TryFindAirRunBelowBed(data, column, bedY, bottomY, out int roofSteps, out float runTop, out float runBottom)) continue;
            if (roofSteps * LeakProbeStep >= LeakRoofThickness) cavityTop = Mathf.Max(cavityTop, runTop);
            else if (roofSteps == 0 && bedY < waterY && distanceSq <= reach * reach) breachBottom = Mathf.Min(breachBottom, runBottom);
        }
        return breachBottom <= cavityTop + LeakProbeStep;
    }

    private static bool TryFindAirRunBelowBed(ChunkRenderData data, Vector3 column, float bedY, float bottomY,
        out int roofSteps, out float runTop, out float runBottom) {
        roofSteps = 0;
        runTop = runBottom = float.NegativeInfinity;
        int count = Mathf.FloorToInt((bedY - bottomY) / LeakProbeStep);
        for (int i = 1; i <= count; i++) {
            column.y = bedY - i * LeakProbeStep;
            bool air = TryGetLocalVoxelPos(data, column, out Vector3 local)
                && SampleSdfTri(data.Chunk, local.x, local.y, local.z) > 0f;
            if (float.IsNegativeInfinity(runTop)) {
                if (!air) continue;
                roofSteps = i - 1;
                runTop = runBottom = column.y;
            } else if (air) {
                runBottom = column.y;
            } else {
                return true;
            }
        }
        return !float.IsNegativeInfinity(runTop);
    }

    private int LeakSignature() {
        unchecked {
            int hash = _leakOpenings.Count;
            foreach (var opening in _leakOpenings) {
                hash = hash * 31 + Mathf.RoundToInt(opening.x * 4f);
                hash = hash * 31 + Mathf.RoundToInt(opening.y * 4f);
                hash = hash * 31 + Mathf.RoundToInt(opening.z * 4f);
            }
            return hash;
        }
    }

    private void RebuildStreamMesh() {
        if (_streamSolver.Cells.Count == 0 && _streamSolver.Falls.Count == 0) {
            DestroyStreamObject();
            return;
        }
        EnsureStreamObject();
        var first = _streamSolver.Cells.Count > 0 ? _streamSolver.Cells[0] : default;
        var origin = WaterStreamSolver.Center(first.X, first.Z, Mathf.Floor(first.FloorY));
        _streamObject.transform.position = origin;
        WaterStreamMesh.Build(_streamSolver, origin, _streamMesh);
    }

    private void EnsureStreamObject() {
        if (_streamObject != null) return;
        _streamObject = new GameObject("TerraformWaterStreams");
        _streamMesh = new Mesh { name = "TerraformWaterStreams" };
        _streamObject.AddComponent<MeshFilter>().sharedMesh = _streamMesh;
        _streamRenderer = _streamObject.AddComponent<MeshRenderer>();
        _streamRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _streamRenderer.enabled = false;
    }

    private void UpdateWaterStreams() {
        UpdateWaterStreamMaterial();
        UpdateStreamAudioPosition();
        WetPlayerUnderFalls();
    }

    private void WetPlayerUnderFalls() {
        var player = Player.m_localPlayer;
        if (player == null || (_audibleFalls.Count == 0 && _streamSolver.Cells.Count == 0)) return;
        _wetCheckTimer += Time.deltaTime;
        if (_wetCheckTimer < WetCheckInterval) return;
        _wetCheckTimer = 0f;
        var position = player.transform.position;
        if (IsUnderFall(position) || IsInPool(position)) player.GetSEMan().AddStatusEffect(SEMan.s_statusEffectWet, resetTime: true);
    }

    private bool IsInPool(Vector3 point) {
        int index = _streamSolver.FindCell(WaterStreamSolver.CellIndex(point.x), WaterStreamSolver.CellIndex(point.z), point.y);
        return index >= 0 && point.y <= _streamSolver.Cells[index].FloorY + PoolWetHeight;
    }

    private bool IsUnderFall(Vector3 point) {
        float reach = FallWidth * 0.5f + PlayerWetRadius;
        foreach (var fall in _audibleFalls) {
            if (point.y > fall.TopY || point.y < fall.BottomY - PlayerWetRadius) continue;
            var center = WaterStreamSolver.Center(fall.X, fall.Z, point.y);
            if (Mathf.Abs(point.x - center.x) <= reach && Mathf.Abs(point.z - center.z) <= reach) return true;
        }
        return false;
    }

    private void UpdateWaterStreamMaterial() {
        if (_streamRenderer == null) return;
        var material = PlacedWater.UpdateSharedMaterial();
        if (material == null) return;
        if (_streamRenderer.sharedMaterial != material) _streamRenderer.sharedMaterial = material;
        if (!_streamRenderer.enabled) _streamRenderer.enabled = true;
    }

    private void DestroyStreamObject() {
        if (_streamObject != null) Destroy(_streamObject);
        if (_streamMesh != null) Destroy(_streamMesh);
        _streamObject = null;
        _streamMesh = null;
        _streamRenderer = null;
    }

    private void RefreshFallEmitters() {
        _aliveFallKeys.Clear();
        var material = GetFallParticleMaterial();
        foreach (var fall in _streamSolver.Falls) {
            long key = ((long)fall.X << 32) | (uint)fall.Z;
            if (material == null || !_aliveFallKeys.Add(key)) continue;
            if (!_fallEmitters.TryGetValue(key, out var emitter) || emitter == null) {
                emitter = CreateFallEmitter(material);
                _fallEmitters[key] = emitter;
            }
            emitter.transform.position = WaterStreamSolver.Center(fall.X, fall.Z, fall.TopY);
            var main = emitter.main;
            main.startLifetime = FallLifetime(fall.TopY - fall.BottomY);
        }

        _staleVfxKeys.Clear();
        foreach (var kv in _fallEmitters) {
            if (!_aliveFallKeys.Contains(kv.Key) || kv.Value == null) _staleVfxKeys.Add(kv.Key);
        }
        foreach (long key in _staleVfxKeys) {
            if (_fallEmitters[key] != null) Destroy(_fallEmitters[key].gameObject);
            _fallEmitters.Remove(key);
        }
    }

    private static ParticleSystem CreateFallEmitter(Material material) {
        var go = new GameObject("TerraformStreamFall");
        var emitter = go.AddComponent<ParticleSystem>();
        emitter.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = emitter.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed = FallStartSpeed;
        main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);
        main.startColor = new Color(1f, 1f, 1f, 0.8f);
        main.gravityModifier = 1f;
        main.maxParticles = FallMaxParticles;

        var emission = emitter.emission;
        emission.rateOverTime = FallParticleRate;

        var shape = emitter.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.rotation = new Vector3(90f, 0f, 0f);
        shape.scale = new Vector3(FallWidth, FallWidth, 0.01f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.08f;
        renderer.lengthScale = 1f;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;

        emitter.Play();
        return emitter;
    }

    private static float FallLifetime(float height) {
        float gravity = Physics.gravity.magnitude;
        return (-FallStartSpeed + Mathf.Sqrt(FallStartSpeed * FallStartSpeed + 2f * gravity * Mathf.Max(0f, height))) / gravity;
    }

    private void RefreshLeakTopVfx(float waterY) {
        var prefab = GetVfxWaterSurfacePrefab();
        _aliveVfxKeys.Clear();
        foreach (var opening in _leakOpenings) {
            long key = VfxKey(opening.x, opening.z, 1f);
            if (prefab == null || !_aliveVfxKeys.Add(key)) continue;
            var pos = new Vector3(opening.x, waterY + 0.02f, opening.z);
            if (_leakTopVfx.TryGetValue(key, out var existing) && existing != null) {
                existing.transform.position = pos;
                continue;
            }
            var inst = Instantiate(prefab, pos, Quaternion.identity);
            inst.name = "TerraformLeakTopVfx";
            StripNetworkComponents(inst);
            _leakTopVfx[key] = inst;
        }
        DestroyStaleVfx(_leakTopVfx);
    }

    private void RefreshLandingAnchors() {
        _aliveVfxKeys.Clear();
        bool splashed = false;
        foreach (var fall in _streamSolver.Falls) {
            if (fall.TopY - fall.BottomY < LandingVfxMinFall) continue;
            var landing = WaterStreamSolver.Center(fall.X, fall.Z, fall.BottomY);
            long key = VfxKey(landing.x, landing.z, LandingVfxSpacing);
            if (!_aliveVfxKeys.Add(key)) continue;
            if (_landingAnchors.TryGetValue(key, out var existing) && existing != null) {
                existing.transform.position = landing;
                continue;
            }
            _landingAnchors[key] = CreateLandingAnchor(landing);
            if (!_firedSplashKeys.Add(key) || splashed) continue;
            FireSplash(landing);
            splashed = true;
        }
        DestroyStaleVfx(_landingAnchors);
    }

    private GameObject CreateLandingAnchor(Vector3 landing) {
        var anchor = new GameObject("TerraformStreamLanding");
        anchor.transform.position = landing;

        var foamPrefab = GetVfxWaterSurfacePrefab();
        if (foamPrefab != null) {
            var foam = Instantiate(foamPrefab, anchor.transform);
            foam.transform.localPosition = Vector3.zero;
            foam.name = "LandingFoam";
            StripNetworkComponents(foam);
        }

        return anchor;
    }

    private void RefreshStreamAudio() {
        _audibleFalls.Clear();
        foreach (var fall in _streamSolver.Falls) {
            if (fall.TopY - fall.BottomY >= LandingVfxMinFall) _audibleFalls.Add(fall);
        }
        if (_audibleFalls.Count == 0) {
            if (_streamAudio != null) Destroy(_streamAudio.gameObject);
            _streamAudio = null;
            return;
        }
        if (_streamAudio != null) return;
        var clip = GetWaterfallClip();
        if (clip == null) return;
        _streamAudio = new GameObject("TerraformStreamAudio").AddComponent<AudioSource>();
        _streamAudio.clip = clip;
        _streamAudio.outputAudioMixerGroup = _sfxMixerGroup;
        _streamAudio.loop = true;
        _streamAudio.spatialBlend = 1f;
        _streamAudio.rolloffMode = AudioRolloffMode.Logarithmic;
        _streamAudio.minDistance = 5f;
        _streamAudio.maxDistance = WaterfallAudioMaxDistance;
        _streamAudio.priority = 64;
        UpdateStreamAudioPosition();
        _streamAudio.Play();
    }

    private void UpdateStreamAudioPosition() {
        var player = Player.m_localPlayer;
        if (_streamAudio == null || player == null) return;
        Vector3 listener = player.transform.position;
        Vector3 nearest = Vector3.zero;
        float nearestDistance = float.MaxValue;
        foreach (var fall in _audibleFalls) {
            var point = WaterStreamSolver.Center(fall.X, fall.Z, Mathf.Clamp(listener.y, fall.BottomY, fall.TopY));
            float distance = (point - listener).sqrMagnitude;
            if (distance >= nearestDistance) continue;
            nearest = point;
            nearestDistance = distance;
        }
        _streamAudio.transform.position = nearest;
    }

    private void FireSplash(Vector3 landing) {
        var prefab = GetFxWaterImpactBigPrefab();
        if (prefab == null) return;
        var inst = Instantiate(prefab, landing + Vector3.up * 0.1f, Quaternion.identity);
        inst.name = "TerraformLeakSplash";
        inst.transform.localScale = Vector3.one * LeakSplashScale;
        StripNetworkComponents(inst);
    }

    private void DestroyStaleVfx(Dictionary<long, GameObject> instances) {
        _staleVfxKeys.Clear();
        foreach (var kv in instances) {
            if (!_aliveVfxKeys.Contains(kv.Key) || kv.Value == null) _staleVfxKeys.Add(kv.Key);
        }
        foreach (long key in _staleVfxKeys) {
            if (instances[key] != null) Destroy(instances[key]);
            instances.Remove(key);
        }
    }

    private static long VfxKey(float worldX, float worldZ, float spacing) {
        int x = Mathf.RoundToInt(worldX / spacing);
        int z = Mathf.RoundToInt(worldZ / spacing);
        return ((long)x << 32) | (uint)z;
    }

    private static void StripNetworkComponents(GameObject go) {
        foreach (var view in go.GetComponentsInChildren<ZNetView>(true)) {
            if (view != null) Destroy(view);
        }
    }

    private AudioClip GetWaterfallClip() {
        TryLoadPrefabs();
        return _waterfallClip;
    }

    private Material GetFallParticleMaterial() {
        TryLoadPrefabs();
        return _fallParticleMaterial;
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
        _waterfallClip = TerraformBundle.Load<AudioClip>(WaterfallClipName);
        _sfxMixerGroup = FindSfxMixerGroup(_fxWaterImpactPrefab);
        if (_sfxMixerGroup == null) TerraformPlugin.Log.LogWarning("[leak-vfx] game SFX mixer group not found; stream audio ignores the volume settings");
        _fallParticleMaterial = FindSplashParticleMaterial(_fxWaterImpactPrefab);
        TerraformPlugin.Log.LogInfo($"[leak-vfx] surface={(_vfxWaterSurfacePrefab != null)} impact={(_fxWaterImpactPrefab != null)} audio={(_waterfallClip != null)} fallMaterial={(_fallParticleMaterial != null ? _fallParticleMaterial.name : "none")}");
    }

    private static AudioMixerGroup FindSfxMixerGroup(GameObject impactPrefab) {
        if (impactPrefab == null) return null;
        foreach (var source in impactPrefab.GetComponentsInChildren<AudioSource>(true)) {
            if (source.outputAudioMixerGroup != null) return source.outputAudioMixerGroup;
        }
        return null;
    }

    private static Material FindSplashParticleMaterial(GameObject impactPrefab) {
        if (impactPrefab == null) return null;
        Material fallback = null;
        foreach (var renderer in impactPrefab.GetComponentsInChildren<ParticleSystemRenderer>(true)) {
            var material = renderer.sharedMaterial;
            if (material == null) continue;
            TerraformPlugin.Log.LogInfo($"[leak-vfx] impact particle '{renderer.name}' material={material.name} shader={material.shader.name}");
            if (material.name.IndexOf("splash", System.StringComparison.OrdinalIgnoreCase) >= 0) return material;
            fallback ??= material;
        }
        return fallback;
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
        DestroyStreamObject();
        foreach (var kv in _leakTopVfx) {
            if (kv.Value != null) Destroy(kv.Value);
        }
        _leakTopVfx.Clear();
        foreach (var kv in _landingAnchors) {
            if (kv.Value != null) Destroy(kv.Value);
        }
        _landingAnchors.Clear();
        foreach (var kv in _fallEmitters) {
            if (kv.Value != null) Destroy(kv.Value.gameObject);
        }
        _fallEmitters.Clear();
        if (_streamAudio != null) Destroy(_streamAudio.gameObject);
        _streamAudio = null;
        _audibleFalls.Clear();
        _leakOpenings.Clear();
        _streamSolver.Clear();
    }
}
