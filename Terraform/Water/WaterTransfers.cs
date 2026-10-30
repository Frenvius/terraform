using System;
using System.Collections.Generic;

namespace Terraform.Water;

public static class WaterTransfers {
    private const int MaxReceipts = 256;
    private static readonly int ReceiptsKey = "tfm_water_receipts".GetStableHashCode();
    private static readonly Random NonceSource = new();

    public static long NewNonce() {
        var bytes = new byte[8];
        lock (NonceSource) NonceSource.NextBytes(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    public static bool TryGetReceipt(ZDO zdo, long nonce, out float moved) {
        var receipts = ReadReceipts(zdo);
        for (int i = 0; i < receipts.Count; i++) {
            if (receipts[i].nonce != nonce) continue;
            moved = receipts[i].moved;
            return true;
        }
        moved = 0f;
        return false;
    }

    public static void AddReceipt(ZDO zdo, long nonce, float moved) {
        var receipts = ReadReceipts(zdo);
        if (receipts.Count >= MaxReceipts) {
            TerraformPlugin.Log.LogError($"[water] Receipt log full on {zdo.m_uid}; dropping the oldest unacknowledged receipt");
            receipts.RemoveAt(0);
        }
        receipts.Add((nonce, moved));
        WriteReceipts(zdo, receipts);
    }

    public static void RemoveReceipt(ZDO zdo, long nonce) {
        var receipts = ReadReceipts(zdo);
        if (receipts.RemoveAll(r => r.nonce == nonce) > 0) WriteReceipts(zdo, receipts);
    }

    public static bool HasReceipts(ZDO zdo) {
        var bytes = zdo?.GetByteArray(ReceiptsKey);
        return bytes != null && bytes.Length > 4;
    }

    private static List<(long nonce, float moved)> ReadReceipts(ZDO zdo) {
        var receipts = new List<(long nonce, float moved)>();
        var bytes = zdo?.GetByteArray(ReceiptsKey);
        if (bytes == null || bytes.Length == 0) return receipts;
        var pkg = new ZPackage(bytes);
        int count = pkg.ReadInt();
        for (int i = 0; i < count; i++) receipts.Add((pkg.ReadLong(), pkg.ReadSingle()));
        return receipts;
    }

    private static void WriteReceipts(ZDO zdo, List<(long nonce, float moved)> receipts) {
        var pkg = new ZPackage();
        pkg.Write(receipts.Count);
        foreach (var receipt in receipts) {
            pkg.Write(receipt.nonce);
            pkg.Write(receipt.moved);
        }
        zdo.Set(ReceiptsKey, pkg.GetArray());
    }
}
