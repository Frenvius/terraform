using System.Globalization;
using HarmonyLib;
using Terraform.Water;
using UnityEngine;

namespace Terraform.Patches;

internal static class WaterBucket {
    public const string ItemName = "tfm_water_bucket";
    private const string AmountKey = "Terraform.WaterAmount";
    private const float Reach = 8f;
    private const float AimDistance = 50f;
    private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
    private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> Animation = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
    private static readonly System.Reflection.MethodInfo InventoryChanged = AccessTools.Method(typeof(Inventory), "Changed");
    private static float _nextUse;

    public static bool IsBucket(ItemDrop.ItemData item) => item?.m_dropPrefab != null && item.m_dropPrefab.name == ItemName;

    public static void Configure(GameObject prefab) {
        if (prefab.name != ItemName) return;
        var shared = prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
        shared.m_itemType = ItemDrop.ItemData.ItemType.Tool;
        shared.m_maxStackSize = 1;
        shared.m_useDurability = false;
        shared.m_name = "Water bucket";
        shared.m_description = "Primary attack fills the bucket from water. Secondary attack pours it out; the water falls to the ground below the spot you aim at.";
    }

    public static float Amount(ItemDrop.ItemData item) => item.m_customData.TryGetValue(AmountKey, out var value)
        ? BucketWaterMath.ReadAmount(value) : 0f;

    public static bool HandleAttack(Humanoid humanoid, bool pour) {
        if (humanoid != Player.m_localPlayer || !IsBucket(RightItem(humanoid))) return false;
        var player = Player.m_localPlayer;
        if (Time.time < _nextUse) return true;
        _nextUse = Time.time + 0.5f;
        if (player.IsDead() || player.InDodge() || player.InAttack() || player.IsTeleporting()
            || player.IsKnockedBack() || player.IsStaggering() || player.InMinorAction()) return true;
        if (ZNet.instance == null || !ZNet.instance.IsServer()) {
            Tell(player, "Water buckets are not supported on multiplayer clients.");
            return true;
        }
        var item = RightItem(player);
        float amount = Amount(item);
        if (pour ? amount <= 0f : amount >= BucketWaterMath.Capacity) {
            Tell(player, pour ? "The bucket is empty." : "The bucket is full.");
            return true;
        }
        if (!TryTarget(player, pour, out var target)) {
            Tell(player, pour ? "Aim at nearby ground to pour." : "Aim at nearby water to fill the bucket.");
            return true;
        }
        if (!PrivateArea.CheckAccess(target) || Location.IsInsideNoBuildLocation(target)) {
            Tell(player, "You cannot move water in this area.");
            return true;
        }
        float moved;
        if (pour) {
            moved = PlacedWater.Pour(target, amount);
            if (moved <= 0f) {
                Tell(player, "The water has nowhere to settle here.");
                return true;
            }
            amount -= moved;
        } else {
            if (!PlacedWater.TryCollect(target, BucketWaterMath.Capacity - amount, out moved)) {
                Tell(player, "No water is available here.");
                return true;
            }
            amount += moved;
        }
        target.y = Mathf.Max(target.y, PlacedWater.GetCombinedLevel(target));
        PlayEffect("fx_float_hitwater", target);
        if (pour) PlayEffect("sfx_land_water", target);
        item.m_customData[AmountKey] = Mathf.Clamp(amount, 0f, BucketWaterMath.Capacity).ToString("R", CultureInfo.InvariantCulture);
        InventoryChanged.Invoke(player.GetInventory(), new object[] { true, false });
        Animation(player).SetTrigger("interact");
        Tell(player, $"Bucket: {Mathf.RoundToInt(amount / BucketWaterMath.Capacity * 100f)}% full");
        return true;
    }

    private static void Tell(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);

    private static void PlayEffect(string prefabName, Vector3 point) {
        var prefab = ZNetScene.instance.GetPrefab(prefabName);
        if (prefab != null) Object.Instantiate(prefab, point, Quaternion.identity);
    }

    private static bool TryTarget(Player player, bool pour, out Vector3 point) {
        point = default;
        if (GameCamera.instance == null) return false;
        var camera = GameCamera.instance.transform;
        var ray = new Ray(camera.position, camera.forward);
        bool hitGround = Physics.Raycast(ray, out var hit, AimDistance, PlacedWater.GroundMask, QueryTriggerInteraction.Ignore);
        var eye = player.GetEyePoint();
        bool inReach = hitGround && (hit.point - eye).sqrMagnitude <= Reach * Reach;
        if (pour) {
            if (!inReach || !Physics.Raycast(hit.point + hit.normal * 0.05f, Vector3.down, out var landing,
                    BucketWaterMath.MaxDrop, PlacedWater.GroundMask, QueryTriggerInteraction.Ignore)) return false;
            point = landing.point;
            if (Configs.ModConfig.DebugMode?.Value == true)
                TerraformPlugin.Log.LogInfo($"[water-target] surface={hit.collider.name} aim={hit.point:F3} landing={point:F3}");
            return true;
        }
        float end = hitGround ? hit.distance : AimDistance;
        for (float distance = 0f; distance <= end; distance += 0.1f) {
            var candidate = ray.GetPoint(distance);
            if ((candidate - eye).sqrMagnitude > Reach * Reach) continue;
            float level = PlacedWater.GetCombinedLevel(candidate);
            if (level <= PlacedWater.NoWater || candidate.y > level) continue;
            point = candidate;
            return true;
        }
        if (!inReach) return false;
        point = hit.point + hit.normal * 0.0002f;
        float groundLevel = PlacedWater.GetCombinedLevel(point);
        return groundLevel > PlacedWater.NoWater && point.y <= groundLevel;
    }
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
internal static class WaterBucketAttackPatch {
    private static bool Prefix(Humanoid __instance, bool secondaryAttack, ref bool __result) {
        if (!WaterBucket.HandleAttack(__instance, secondaryAttack)) return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new[] { typeof(int) })]
internal static class WaterBucketTooltipPatch {
    private static void Postfix(ItemDrop.ItemData __instance, ref string __result) {
        if (WaterBucket.IsBucket(__instance))
            __result += $"\nWater: {Mathf.RoundToInt(WaterBucket.Amount(__instance) / BucketWaterMath.Capacity * 100f)}%";
    }
}

[HarmonyPatch(typeof(Floating), nameof(Floating.GetWaterLevel))]
internal static class PlacedWaterFloatLevelPatch {
    private static void Postfix(Vector3 p, ref float __result) => __result = Mathf.Max(__result, PlacedWater.GetLevel(p));
}

[HarmonyPatch(typeof(Floating), nameof(Floating.GetLiquidLevel))]
internal static class PlacedWaterLiquidLevelPatch {
    private static void Postfix(Vector3 p, LiquidType type, ref float __result) {
        if (type == LiquidType.All || type == LiquidType.Water) __result = Mathf.Max(__result, PlacedWater.GetLevel(p));
    }
}

[HarmonyPatch(typeof(Floating), nameof(Floating.IsUnderWater))]
internal static class PlacedWaterUnderwaterPatch {
    private static void Postfix(Vector3 p, ref bool __result) => __result |= PlacedWater.GetLevel(p) > p.y;
}

[HarmonyPatch(typeof(Character), nameof(Character.SetLiquidLevel))]
internal static class PlacedWaterCharacterPatch {
    private static void Prefix(Character __instance, LiquidType type, ref float level) {
        if (type == LiquidType.Water) level = Mathf.Max(level, PlacedWater.GetLevel(__instance));
    }
}

[HarmonyPatch(typeof(Floating), nameof(Floating.SetLiquidLevel))]
internal static class PlacedWaterFloatingPatch {
    private static void Prefix(Floating __instance, LiquidType type, ref float level) {
        if (type == LiquidType.Water) level = Mathf.Max(level, PlacedWater.GetLevel(__instance));
    }
}
