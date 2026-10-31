using System.Collections.Generic;
using UnityEngine;
using Terraform.Network;
using Terraform.Persistence;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private bool _sidecarImported;

    public static long ChunkKeyOfZone(int zoneX, int zoneZ) {
        const int half = VoxelChunk.SIZE / 2;
        return ChunkKey(zoneX * VoxelChunk.SIZE - half, zoneZ * VoxelChunk.SIZE - half);
    }

    private static Vector3 ZoneCenterOf(long chunkKey, float y) {
        const int half = VoxelChunk.SIZE / 2;
        return new Vector3((int)(chunkKey >> 32) + half, y, (int)chunkKey + half);
    }

    private ChunkRenderData FindChunk(long chunkKey) {
        foreach (var data in _chunks.Values)
            if (data?.Heightmap != null && data.MeshObject != null && data.ChunkKey == chunkKey) return data;
        return null;
    }

    private bool TryGetBuiltChunk(long chunkKey, out ChunkRenderData data) {
        data = FindChunk(chunkKey);
        return data != null && !_pendingBuildHash.ContainsKey(data.Heightmap.GetInstanceID());
    }

    private static bool TryGetZoneTerrainHeight(long chunkKey, out float terrainHeight) {
        terrainHeight = 0f;
        if (!VoxelZone.TryGet(chunkKey, out var zone)) return false;
        var state = zone.ReadState();
        if (state == null || state.TerrainHeight == 0f) return false;
        terrainHeight = state.TerrainHeight;
        return true;
    }

    private void BuildChunk(ChunkRenderData data) {
        VoxelZone.TryGet(data.ChunkKey, out var zone);
        var state = zone != null ? zone.ReadState() : null;
        if (state != null) {
            ResetToZoneState(data, state, zone);
        } else if (data.Chunk != null) {
            SeedIntactVoxels(data);
        } else {
            RebuildMesh(data);
            zone?.FlushQueuedDigs();
            return;
        }
        RefreshChunkAfterStateChange(data);
        RefreshWaterLeaks();
        zone?.FlushQueuedDigs();
    }

    public void OnZoneStateChanged(VoxelZone zone) {
        if (!VoxelZone.TryGet(zone.ChunkKey, out var current) || current != zone) return;
        var state = zone.ReadState();
        if (state == null) return;

        _chunkDigHistory[zone.ChunkKey] = state.Digs;
        if (!TryGetBuiltChunk(zone.ChunkKey, out var data)) return;

        _undergroundPaved.TryGetValue(data.ChunkKey, out var localPaint);
        if (WindowOf(state, data) == data.TerrainHeight && state.SameVoxelsAs(data.Chunk) && state.SamePaintAs(localPaint))
            return;

        ResetToZoneState(data, state, zone);
        RefreshChunkAfterStateChange(data);
        RefreshWaterLeaks();
    }

    private static float WindowOf(ZoneState state, ChunkRenderData data) =>
        state.TerrainHeight != 0f ? state.TerrainHeight : data.TerrainHeight;

    private void ResetToZoneState(ChunkRenderData data, ZoneState state, VoxelZone zone) {
        _chunkDigHistory[data.ChunkKey] = state.Digs;
        SetUndergroundPaint(data.ChunkKey, state.UndergroundPaved);
        PlaceChunkWindow(data, WindowOf(state, data));
        ResetChunkDigState(data);

        if (!state.NeedsReplay) {
            state.ApplyAltered(data.Chunk);
            return;
        }
        foreach (var dig in state.Digs) CarveCrater(data, dig);
        zone.MarkDirty();
        DebugLog($"[net] replayed {state.Digs.Count} digs for zone {data.ChunkKey}");
    }

    public ZoneState CaptureZoneState(long chunkKey) {
        var data = FindChunk(chunkKey);
        if (data == null) return null;
        EnsureChunkAllocated(data);
        _chunkDigHistory.TryGetValue(chunkKey, out var digs);
        _undergroundPaved.TryGetValue(chunkKey, out var paved);
        return new ZoneState {
            TerrainHeight    = data.TerrainHeight,
            Digs             = digs ?? new List<DigRecord>(),
            UndergroundPaved = paved,
            Altered          = ZoneState.EncodeAltered(data.Chunk),
        };
    }

    private VoxelZone ZoneFor(ChunkRenderData data) {
        if (VoxelZone.TryGet(data.ChunkKey, out var zone)) return zone;
        zone = VoxelZone.Create(ZoneCenterOf(data.ChunkKey, data.TerrainHeight));
        return zone != null && zone.IsValid ? zone : null;
    }

    private void SubmitDig(ChunkRenderData data, DigRecord record) {
        var zone = ZoneFor(data);
        if (zone == null) return;
        if (zone.IsOwner) zone.MarkDirty();
        else zone.SendDig(record);
    }

    private void SubmitUndergroundPaint(ChunkRenderData data, Vector3 worldPos, float radius, TerrainModifier.PaintType type) {
        var zone = ZoneFor(data);
        if (zone == null) return;
        if (zone.IsOwner) zone.MarkDirty();
        else zone.SendUndergroundPaint(worldPos, radius, type);
    }

    public bool TryApplyRemoteDig(long chunkKey, DigRecord record) {
        if (!TryGetBuiltChunk(chunkKey, out var data)) return false;
        ApplyDigToChunk(data, record);
        FinishDig(record);
        return true;
    }

    public bool TryApplyRemoteUndergroundPaint(long chunkKey, Vector3 worldPos, float radius, TerrainModifier.PaintType type) {
        if (!TryGetBuiltChunk(chunkKey, out var data)) return false;
        return PaintUndergroundInChunk(data, worldPos, radius, type);
    }

    private void TryImportSidecar() {
        if (_persistenceLoaded) return;
        if (ZNet.instance == null || ZDOMan.instance == null) return;
        if (string.IsNullOrEmpty(ZNet.instance.GetWorldName())) return;
        _persistenceLoaded = true;
        if (!ZNet.instance.IsServer()) return;

        var loaded = TerraformPersistence.Load();
        if (loaded == null) return;

        int imported = 0;
        foreach (var kv in loaded) {
            var state = ZoneStateFromSidecar(kv.Value);
            if (state == null) continue;
            VoxelZone.CreateDetached(ZoneCenterOf(kv.Key, state.TerrainHeight), state.Encode());
            imported++;
        }
        _sidecarImported = true;
        TerraformPlugin.Log.LogInfo($"Persistence: imported {imported} chunks from the sidecar save into world zones");
    }

    private static ZoneState ZoneStateFromSidecar(ChunkPersistence saved) {
        bool hasDigs = saved.Digs != null && saved.Digs.Count > 0;
        bool hasVoxels = saved.VoxelData != null && saved.VoxelDataIsFull && saved.AlteredBits != null;
        if (!hasDigs && !hasVoxels) return null;

        var state = new ZoneState {
            TerrainHeight    = saved.TerrainHeight,
            Digs             = saved.Digs ?? new List<DigRecord>(),
            UndergroundPaved = saved.UndergroundPaved,
        };
        if (hasVoxels) {
            var chunk = new VoxelChunk();
            chunk.RestoreAltered(VoxelChunk.DecodeFull(saved.VoxelData), saved.AlteredBits);
            state.Altered = ZoneState.EncodeAltered(chunk);
        }
        return state;
    }

    private void OnWorldSaveStarted() {
        Water.PlacedWater.SaveAll();
        VoxelZone.SaveAllDirty();
        if (!_sidecarImported) return;
        TerraformPersistence.RetireImported();
        _sidecarImported = false;
    }
}
