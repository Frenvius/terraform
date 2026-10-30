using System.Collections.Generic;
using HarmonyLib;
using Terraform.Patches;
using UnityEngine;

namespace Terraform.Water;

public enum BucketAction {
    Pour,
    Collect,
    NaturalPour,
    NaturalFill,
}

public enum BucketOutcome {
    Synced,
    Poured,
    Filled,
    Empty,
    Full,
    NowhereToSettle,
    NoWater,
}

public static class BucketLedger {
    public const string HelloRpc = "tfm_BucketHello";
    public const string BeginRpc = "tfm_BucketBegin";
    public const string StateRpc = "tfm_BucketState";
    private const int FormatVersion = 1;
    private const string LedgerPrefab = "tfm_bucket_ledger";
    private const float TickInterval = 0.25f;
    private const float ResendInterval = 2f;
    private const float MissingAreaGrace = 5f;
    private static readonly Vector3 LedgerPosition = new(0f, 0f, -100000f);
    private static readonly int LedgerKey = "tfm_bucket_ledger_data".GetStableHashCode();
    private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
        AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

    private static readonly Dictionary<long, float> Buckets = new();
    private static readonly List<Pending> Transfers = new();
    private static readonly List<Settled> Acks = new();
    private static readonly Dictionary<long, long> Holders = new();
    private static readonly Dictionary<long, float> LastSent = new();
    private static readonly Dictionary<long, float> MissingSince = new();
    private static ZRoutedRpc _registeredOn;
    private static ZDOMan _loadedFor;
    private static ZDO _ledger;
    private static bool _dirty;
    private static float _nextTick;

    private readonly struct Pending {
        public readonly long Nonce;
        public readonly long Bucket;
        public readonly bool Pour;
        public readonly float Amount;
        public readonly ZDOID Area;
        public readonly Vector3 Point;

        public Pending(long nonce, long bucket, bool pour, float amount, ZDOID area, Vector3 point) {
            Nonce = nonce;
            Bucket = bucket;
            Pour = pour;
            Amount = amount;
            Area = area;
            Point = point;
        }
    }

    private readonly struct Settled {
        public readonly long Nonce;
        public readonly ZDOID Area;

        public Settled(long nonce, ZDOID area) {
            Nonce = nonce;
            Area = area;
        }
    }

    private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

    public static void EnsureRegistered() {
        var rpc = ZRoutedRpc.instance;
        if (rpc == null || rpc == _registeredOn) return;
        _registeredOn = rpc;
        rpc.Register<long, float>(HelloRpc, RPC_Hello);
        rpc.Register<long, int, ZDOID, Vector3>(BeginRpc, RPC_Begin);
        rpc.Register<long, float, float, int>(StateRpc, WaterBucket.RPC_State);
    }

    public static void Tick() {
        EnsureRegistered();
        if (!IsServer || ZDOMan.instance == null || string.IsNullOrEmpty(ZNet.instance.GetWorldName())) return;
        EnsureLoaded();
        if (Time.time < _nextTick) return;
        _nextTick = Time.time + TickInterval;
        Transfers.RemoveAll(TryResolve);
        Acks.RemoveAll(TryCloseAck);
        if (_dirty) Save();
    }

    private static void EnsureLoaded() {
        if (_loadedFor == ZDOMan.instance) return;
        _loadedFor = ZDOMan.instance;
        Buckets.Clear();
        Transfers.Clear();
        Acks.Clear();
        Holders.Clear();
        LastSent.Clear();
        MissingSince.Clear();
        _ledger = FindLedger() ?? CreateLedger();
        if (!_ledger.HasOwner()) _ledger.SetOwner(ZDOMan.GetSessionID());
        Read(_ledger.GetByteArray(LedgerKey));
    }

    private static ZDO FindLedger() {
        int hash = LedgerPrefab.GetStableHashCode();
        foreach (var zdo in ObjectsById(ZDOMan.instance).Values)
            if (zdo.GetPrefab() == hash) return zdo;
        return null;
    }

    private static ZDO CreateLedger() {
        var zdo = ZDOMan.instance.CreateNewZDO(LedgerPosition, LedgerPrefab.GetStableHashCode());
        zdo.Persistent = true;
        zdo.SetPrefab(LedgerPrefab.GetStableHashCode());
        return zdo;
    }

    private static void Read(byte[] bytes) {
        if (bytes == null) return;
        var pkg = new ZPackage(Utils.Decompress(bytes));
        int version = pkg.ReadInt();
        if (version != FormatVersion) {
            TerraformPlugin.Log.LogError($"[water] Bucket ledger version {version} is not supported; starting empty");
            return;
        }
        for (int i = pkg.ReadInt(); i > 0; i--) Buckets[pkg.ReadLong()] = pkg.ReadSingle();
        for (int i = pkg.ReadInt(); i > 0; i--)
            Transfers.Add(new Pending(pkg.ReadLong(), pkg.ReadLong(), pkg.ReadBool(), pkg.ReadSingle(), pkg.ReadZDOID(), pkg.ReadVector3()));
        for (int i = pkg.ReadInt(); i > 0; i--) Acks.Add(new Settled(pkg.ReadLong(), pkg.ReadZDOID()));
    }

    private static void Save() {
        _dirty = false;
        var pkg = new ZPackage();
        pkg.Write(FormatVersion);
        pkg.Write(Buckets.Count);
        foreach (var bucket in Buckets) {
            pkg.Write(bucket.Key);
            pkg.Write(bucket.Value);
        }
        pkg.Write(Transfers.Count);
        foreach (var t in Transfers) {
            pkg.Write(t.Nonce);
            pkg.Write(t.Bucket);
            pkg.Write(t.Pour);
            pkg.Write(t.Amount);
            pkg.Write(t.Area);
            pkg.Write(t.Point);
        }
        pkg.Write(Acks.Count);
        foreach (var ack in Acks) {
            pkg.Write(ack.Nonce);
            pkg.Write(ack.Area);
        }
        _ledger.Set(LedgerKey, Utils.Compress(pkg.GetArray()));
    }

    private static float Reserved(long bucket, bool pour) {
        float reserved = 0f;
        foreach (var t in Transfers)
            if (t.Bucket == bucket && t.Pour == pour) reserved += t.Amount;
        return reserved;
    }

    private static void RPC_Hello(long sender, long bucket, float clientAmount) {
        if (!IsServer) return;
        EnsureLoaded();
        Holders[bucket] = sender;
        if (!Buckets.ContainsKey(bucket)) {
            Buckets[bucket] = Mathf.Clamp(clientAmount, 0f, BucketWaterMath.Capacity);
            _dirty = true;
        }
        Notify(bucket, BucketOutcome.Synced);
    }

    private static void RPC_Begin(long sender, long bucket, int action, ZDOID area, Vector3 point) {
        if (!IsServer) return;
        EnsureLoaded();
        Holders[bucket] = sender;
        if (!Buckets.TryGetValue(bucket, out float amount)) {
            Notify(bucket, BucketOutcome.Synced);
            return;
        }
        var kind = (BucketAction)action;
        bool pour = kind == BucketAction.Pour || kind == BucketAction.NaturalPour;
        float available = pour
            ? amount - Reserved(bucket, true)
            : BucketWaterMath.Capacity - amount - Reserved(bucket, false);
        if (available <= BucketWaterMath.DryDepth) {
            Notify(bucket, pour ? BucketOutcome.Empty : BucketOutcome.Full);
            return;
        }
        switch (kind) {
            case BucketAction.NaturalPour:
                Change(bucket, -available, BucketOutcome.Poured);
                return;
            case BucketAction.NaturalFill:
                Change(bucket, available, BucketOutcome.Filled);
                return;
        }
        var transfer = new Pending(WaterTransfers.NewNonce(), bucket, pour, available, area, point);
        Transfers.Add(transfer);
        _dirty = true;
        Send(transfer);
    }

    private static void Change(long bucket, float delta, BucketOutcome outcome) {
        Buckets[bucket] = Mathf.Clamp(Buckets[bucket] + delta, 0f, BucketWaterMath.Capacity);
        _dirty = true;
        Notify(bucket, outcome);
    }

    private static void Send(Pending transfer) {
        if (PlacedWater.SendTransfer(transfer.Area, transfer.Nonce, transfer.Pour, transfer.Point, transfer.Amount))
            LastSent[transfer.Nonce] = Time.time;
    }

    private static void SendAck(Settled ack) {
        if (PlacedWater.SendAck(ack.Area, ack.Nonce)) LastSent[ack.Nonce] = Time.time;
    }

    private static bool TryResolve(Pending transfer) {
        var zdo = ZDOMan.instance.GetZDO(transfer.Area);
        if (zdo == null) {
            if (!MissingSince.TryGetValue(transfer.Nonce, out float since)) {
                MissingSince[transfer.Nonce] = Time.time;
                return false;
            }
            if (Time.time - since < MissingAreaGrace) return false;
            Settle(transfer, 0f);
            return true;
        }
        MissingSince.Remove(transfer.Nonce);
        if (WaterTransfers.TryGetReceipt(zdo, transfer.Nonce, out float moved)) {
            Settle(transfer, moved);
            var ack = new Settled(transfer.Nonce, transfer.Area);
            Acks.Add(ack);
            SendAck(ack);
            return true;
        }
        if (!LastSent.TryGetValue(transfer.Nonce, out float sent) || Time.time - sent >= ResendInterval) Send(transfer);
        return false;
    }

    private static void Settle(Pending transfer, float moved) {
        MissingSince.Remove(transfer.Nonce);
        LastSent.Remove(transfer.Nonce);
        var outcome = transfer.Pour
            ? moved > 0f ? BucketOutcome.Poured : BucketOutcome.NowhereToSettle
            : moved > 0f ? BucketOutcome.Filled : BucketOutcome.NoWater;
        if (Buckets.ContainsKey(transfer.Bucket)) Change(transfer.Bucket, transfer.Pour ? -moved : moved, outcome);
        _dirty = true;
    }

    private static bool TryCloseAck(Settled ack) {
        var zdo = ZDOMan.instance.GetZDO(ack.Area);
        if (zdo == null || !WaterTransfers.TryGetReceipt(zdo, ack.Nonce, out _)) {
            LastSent.Remove(ack.Nonce);
            _dirty = true;
            return true;
        }
        if (!LastSent.TryGetValue(ack.Nonce, out float sent) || Time.time - sent >= ResendInterval) SendAck(ack);
        return false;
    }

    private static void Notify(long bucket, BucketOutcome outcome) {
        if (!Holders.TryGetValue(bucket, out long holder) || !Buckets.TryGetValue(bucket, out float amount)) return;
        ZRoutedRpc.instance.InvokeRoutedRPC(holder, StateRpc, bucket, amount, BucketWaterMath.Capacity, (int)outcome);
    }

    public static bool IsFromServer(long sender) {
        if (ZNet.instance == null) return false;
        if (ZNet.instance.IsServer()) return sender == ZDOMan.GetSessionID();
        var server = ZNet.instance.GetServerPeer();
        return server != null && sender == server.m_uid;
    }
}
