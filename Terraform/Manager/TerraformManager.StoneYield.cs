using UnityEngine;
using Terraform.Digging;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const string StonePrefabName  = "Stone";
    private const string StoneBalanceKey  = "Terraform.StoneBalance";
    private const float  StoneDropScatter = 0.3f;

    private void SettleStone(DigRecord record, float solidRemoved) {
        var player = Player.m_localPlayer;
        if (player == null) return;

        player.m_customData.TryGetValue(StoneBalanceKey, out string stored);
        float balance = StoneYield.Accrue(
            StoneYield.ReadBalance(stored), record.Mode, solidRemoved, Network.ServerSync.StonePerCubicMeter);
        int stones = StoneYield.TakeWholeStones(ref balance);
        player.m_customData[StoneBalanceKey] = StoneYield.WriteBalance(balance);

        if (VerboseLogs) DebugLog($"[stone] mode={record.Mode} removed={solidRemoved:F2}m3 stones={stones} balance={balance:F2}");
        if (stones > 0) DropStone(record.WorldPos, stones);
    }

    private static void DropStone(Vector3 position, int amount) {
        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(StonePrefabName) : null;
        if (prefab == null) {
            TerraformPlugin.Log.LogWarning($"Stone drop skipped: prefab '{StonePrefabName}' not found.");
            return;
        }

        var spawned = Instantiate(prefab, position + Random.insideUnitSphere * StoneDropScatter, Quaternion.identity);
        var itemDrop = spawned.GetComponent<ItemDrop>();
        if (itemDrop == null) return;
        ItemDrop.OnCreateNew(itemDrop);
        itemDrop.SetStack(amount);
    }
}
