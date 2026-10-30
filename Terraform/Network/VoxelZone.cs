using System.Collections.Generic;
using System.IO;
using Terraform.Patches;
using UnityEngine;

namespace Terraform.Network;

public sealed class VoxelZone : MonoBehaviour {
    public const string PrefabName = "tfm_voxel_zone";
    private const string DigRpc   = "tfm_ApplyDig";
    private const string PaintRpc = "tfm_PaintUnderground";
    private const float SaveDelay = 0.25f;

    public static readonly int StateKey = "tfm_zone_state".GetStableHashCode();
    public static int PrefabHash => PrefabName.GetStableHashCode();

    private static readonly Dictionary<long, VoxelZone> Zones = new();
    private static GameObject _prefab;

    private readonly List<DigRecord> _queuedDigs = new();
    private ZNetView _view;
    private uint _revision;
    private bool _dirty;
    private float _saveAt;
    private bool _retire;

    public long ChunkKey { get; private set; }

    public bool IsValid => _view != null && _view.IsValid();
    public bool IsOwner => IsValid && _view.IsOwner();

    public static void Register(ZNetScene scene) {
        if (_prefab == null) {
            var root = new GameObject("TerraformZonePrefabs");
            root.SetActive(false);
            DontDestroyOnLoad(root);
            _prefab = new GameObject(PrefabName);
            _prefab.transform.SetParent(root.transform, false);
            var view = _prefab.AddComponent<ZNetView>();
            view.m_persistent = true;
            view.m_distant = true;
            _prefab.AddComponent<VoxelZone>();
        }
        if (!scene.m_prefabs.Contains(_prefab)) scene.m_prefabs.Add(_prefab);
    }

    public static bool TryGet(long chunkKey, out VoxelZone zone) =>
        Zones.TryGetValue(chunkKey, out zone) && zone != null && zone.IsValid;

    public static VoxelZone Create(Vector3 zoneCenter) {
        var go = Instantiate(_prefab, zoneCenter, Quaternion.identity);
        return go.GetComponent<VoxelZone>();
    }

    public static ZDO CreateDetached(Vector3 zoneCenter, byte[] state) {
        var zdo = ZDOMan.instance.CreateNewZDO(zoneCenter, PrefabHash);
        zdo.Persistent = true;
        zdo.Type = ZDO.ObjectType.Default;
        zdo.Distant = true;
        zdo.SetPrefab(PrefabHash);
        zdo.Set(StateKey, state);
        return zdo;
    }

    private void Awake() {
        _view = GetComponent<ZNetView>();
        if (!IsValid) return;
        var zone = ZoneSystem.GetZone(transform.position);
        ChunkKey = TerraformManager.ChunkKeyOfZone(zone.x, zone.y);
        _view.Register<ZPackage>(DigRpc, RPC_ApplyDig);
        _view.Register<ZPackage>(PaintRpc, RPC_PaintUnderground);
        Claim();
        _revision = _view.GetZDO().DataRevision;
        TerraformManager.Instance?.OnZoneStateChanged(this);
    }

    private void Claim() {
        if (!Zones.TryGetValue(ChunkKey, out var current) || current == null || !current.IsValid) {
            Zones[ChunkKey] = this;
            return;
        }
        var winner = Precedes(_view.GetZDO().m_uid, current._view.GetZDO().m_uid) ? this : current;
        var loser  = winner == this ? current : this;
        Zones[ChunkKey] = winner;
        loser._retire = true;
    }

    internal static bool Precedes(ZDOID a, ZDOID b) =>
        a.UserID != b.UserID ? a.UserID < b.UserID : a.ID < b.ID;

    private void OnDestroy() {
        if (Zones.TryGetValue(ChunkKey, out var current) && current == this) Zones.Remove(ChunkKey);
    }

    private void Update() {
        if (!IsValid) return;
        if (_retire) {
            Retire();
            return;
        }

        var zdo = _view.GetZDO();
        if (zdo.DataRevision != _revision) {
            _revision = zdo.DataRevision;
            TerraformManager.Instance?.OnZoneStateChanged(this);
        }
        if (_dirty && Time.time >= _saveAt && _view.IsOwner()) Save();
    }

    private void Retire() {
        if (!_view.IsOwner()) return;
        var state = ReadState();
        if (TryGet(ChunkKey, out var winner) && winner != this && state != null) {
            foreach (var dig in state.Digs) winner.SendDig(dig);
        }
        ZNetScene.instance.Destroy(gameObject);
    }

    public ZoneState ReadState() {
        if (!IsValid) return null;
        var bytes = _view.GetZDO().GetByteArray(StateKey);
        if (bytes == null) return null;
        try {
            return ZoneState.Decode(bytes);
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"[net] zone {ChunkKey} state unreadable: {ex.Message}");
            return null;
        }
    }

    public void MarkDirty() {
        if (!_dirty) _saveAt = Time.time + SaveDelay;
        _dirty = true;
    }

    public void SaveIfDirty() {
        if (_dirty && IsOwner) Save();
    }

    private void Save() {
        _dirty = false;
        var state = TerraformManager.Instance?.CaptureZoneState(ChunkKey);
        if (state == null) return;
        var zdo = _view.GetZDO();
        zdo.Set(StateKey, state.Encode());
        _revision = zdo.DataRevision;
    }

    public void SendDig(DigRecord dig) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) ZoneState.WriteDig(writer, dig);
        _view.InvokeRPC(DigRpc, new ZPackage(stream.ToArray()));
    }

    public void SendUndergroundPaint(Vector3 worldPos, float radius, TerrainModifier.PaintType type) {
        var pkg = new ZPackage();
        pkg.Write(worldPos);
        pkg.Write(radius);
        pkg.Write((int)type);
        _view.InvokeRPC(PaintRpc, pkg);
    }

    private void RPC_ApplyDig(long sender, ZPackage pkg) {
        if (!_view.IsOwner()) return;
        DigRecord dig;
        using (var reader = new BinaryReader(new MemoryStream(pkg.GetArray()))) dig = ZoneState.ReadDig(reader);

        var manager = TerraformManager.Instance;
        if (manager == null || !manager.TryApplyRemoteDig(ChunkKey, dig)) {
            _queuedDigs.Add(dig);
            return;
        }
        MarkDirty();
    }

    private void RPC_PaintUnderground(long sender, ZPackage pkg) {
        if (!_view.IsOwner()) return;
        Vector3 worldPos = pkg.ReadVector3();
        float radius = pkg.ReadSingle();
        var type = (TerrainModifier.PaintType)pkg.ReadInt();
        var manager = TerraformManager.Instance;
        if (manager != null && manager.TryApplyRemoteUndergroundPaint(ChunkKey, worldPos, radius, type)) MarkDirty();
    }

    public void FlushQueuedDigs() {
        if (_queuedDigs.Count == 0 || !IsOwner) return;
        var manager = TerraformManager.Instance;
        if (manager == null) return;
        int applied = 0;
        while (applied < _queuedDigs.Count && manager.TryApplyRemoteDig(ChunkKey, _queuedDigs[applied])) applied++;
        _queuedDigs.RemoveRange(0, applied);
        if (applied > 0) MarkDirty();
    }

    public static void SaveAllDirty() {
        foreach (var zone in Zones.Values) zone?.SaveIfDirty();
    }
}
