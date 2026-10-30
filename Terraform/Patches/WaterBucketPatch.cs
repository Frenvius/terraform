using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Terraform.Water;
using UnityEngine;

namespace Terraform.Patches;

internal static class WaterBucket {
    public const string ItemName = "tfm_water_bucket";
    private const string AmountKey = "Terraform.WaterAmount";
    private const float EquipDuration = 0.2f;
    private const float Reach = 8f;
    private const float AimDistance = 50f;
    private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
    private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> Animation = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
    private static readonly System.Reflection.MethodInfo InventoryChanged = AccessTools.Method(typeof(Inventory), "Changed");
    private const string CapacityKey = "Terraform.WaterCapacity";
    private const string IdKey = "Terraform.BucketId";
    private const float SyncInterval = 0.5f;
    private const float HelloInterval = 2f;
    private static readonly HashSet<long> Confirmed = new();
    private static readonly Dictionary<long, float> LastHello = new();
    private static readonly Dictionary<long, Vector3> LastTarget = new();
    private static long _session;
    private static float _nextUse;
    private static float _nextSync;

    private const string EmptyIconName = "bucketEmptySprite";
    private const string WaterSurfaceName = "WaterSurface";
    private const int FullVariant = 1;
    private static readonly int HeldWaterHash = "Terraform.HeldBucketWater".GetStableHashCode();
    private static bool _hasEmptyIcon;

    public static bool IsBucket(ItemDrop.ItemData item) => item?.m_dropPrefab != null && item.m_dropPrefab.name == ItemName;

    public static bool HasWater(ItemDrop.ItemData item) => Amount(item) > BucketWaterMath.DryDepth;

    public static bool HeldBucketHasWater(ZDO zdo) => zdo.GetBool(HeldWaterHash);

    public static void Configure(GameObject prefab) {
        if (prefab.name != ItemName) return;
        var shared = prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
        shared.m_itemType = ItemDrop.ItemData.ItemType.Tool;
        shared.m_maxStackSize = 1;
        shared.m_useDurability = false;
        shared.m_equipDuration = EquipDuration;
        AddEmptyIcon(shared);
        AddHangingHold(prefab);
        AddWaterVisual(prefab);
        shared.m_name = "Water bucket";
        shared.m_description = "Primary attack fills the bucket from water. Secondary attack pours it out; the water falls to the ground below the spot you aim at.";
    }

    private static void AddEmptyIcon(ItemDrop.ItemData.SharedData shared) {
        if (shared.m_icons == null || shared.m_icons.Length == 0) return;
        var empty = Assets.TerraformBundle.Load<Sprite>(EmptyIconName);
        if (empty == null) return;
        shared.m_icons = new[] { empty, shared.m_icons[0] };
        _hasEmptyIcon = true;
    }

    private static void AddWaterVisual(GameObject prefab) {
        var attach = prefab.transform.Find("attach");
        if (attach == null || attach.GetComponent<BucketWaterVisual>() != null) return;
        var water = FindChild(attach, WaterSurfaceName);
        var renderer = water != null ? water.GetComponent<Renderer>() : null;
        if (renderer == null) {
            TerraformPlugin.Log.LogError($"[items] {prefab.name} has no '{WaterSurfaceName}' renderer under 'attach'; water visual skipped");
            return;
        }
        attach.gameObject.AddComponent<BucketWaterVisual>().Water = renderer;
    }

    private static Transform FindChild(Transform root, string name) {
        foreach (var child in root.GetComponentsInChildren<Transform>(true))
            if (child.name == name) return child;
        return null;
    }

    private static bool ApplyVariant(ItemDrop.ItemData item) {
        int variant = _hasEmptyIcon && HasWater(item) ? FullVariant : 0;
        if (item.m_variant == variant) return false;
        item.m_variant = variant;
        return true;
    }

    private static void PublishHeldWater(Player player) {
        var item = RightItem(player);
        var view = player.GetComponent<ZNetView>();
        if (!IsBucket(item) || view == null || !view.IsValid()) return;
        view.GetZDO().Set(HeldWaterHash, HasWater(item));
    }

    private static void AddHangingHold(GameObject prefab) {
        var attach = prefab.transform.Find("attach");
        if (attach == null || attach.GetComponent<BucketHangingHold>() != null) return;
        if (!TryGetModelBounds(attach, out var bounds)) {
            TerraformPlugin.Log.LogError($"[items] {prefab.name} has no mesh under 'attach'; hanging hold skipped");
            return;
        }
        attach.gameObject.AddComponent<BucketHangingHold>().Rim = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
    }

    private static bool TryGetModelBounds(Transform root, out Bounds bounds) {
        bounds = default;
        bool found = false;
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) {
            if (filter.sharedMesh == null) continue;
            var toRoot = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var mesh = filter.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++) {
                var sign = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
                var point = toRoot.MultiplyPoint3x4(mesh.center + Vector3.Scale(mesh.extents, sign));
                if (found) bounds.Encapsulate(point);
                else bounds = new Bounds(point, Vector3.zero);
                found = true;
            }
        }
        return found;
    }

    private static float Amount(ItemDrop.ItemData item) => ReadNumber(item, AmountKey, 0f);

    public static float Fill(ItemDrop.ItemData item) {
        float capacity = ReadNumber(item, CapacityKey, BucketWaterMath.Capacity);
        return capacity > 0f ? Mathf.Clamp01(Amount(item) / capacity) : 0f;
    }

    private static float ReadNumber(ItemDrop.ItemData item, string key, float fallback) =>
        item.m_customData.TryGetValue(key, out var value)
        && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
        && !float.IsNaN(number) && !float.IsInfinity(number) ? Mathf.Max(0f, number) : fallback;

    private static void WriteNumber(ItemDrop.ItemData item, string key, float value) =>
        item.m_customData[key] = value.ToString("R", CultureInfo.InvariantCulture);

    public static bool HandleAttack(Humanoid humanoid, bool pour) {
        if (humanoid != Player.m_localPlayer || !IsBucket(RightItem(humanoid))) return false;
        var player = Player.m_localPlayer;
        if (Time.time < _nextUse) return true;
        _nextUse = Time.time + 0.5f;
        if (player.IsDead() || player.InDodge() || player.InAttack() || player.IsTeleporting()
            || player.IsKnockedBack() || player.IsStaggering() || player.InMinorAction()) return true;
        if (!Network.ServerSync.ServerRunsTerraform) {
            Tell(player, "Water buckets need Terraform on the server.");
            return true;
        }
        var item = RightItem(player);
        long id = EnsureId(item);
        if (!Confirmed.Contains(id)) {
            SendHello(item, id);
            Tell(player, "The bucket is syncing with the server.");
            return true;
        }
        if (pour ? Amount(item) <= BucketWaterMath.DryDepth : Fill(item) >= 1f) {
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
        bool planned = pour
            ? PlacedWater.TryPlanPour(target, out var action, out var area)
            : PlacedWater.TryPlanCollect(target, out action, out area);
        if (!planned) {
            Tell(player, pour ? "The water has nowhere to settle here." : "No water is available here.");
            return true;
        }
        LastTarget[id] = target;
        ZRoutedRpc.instance.InvokeRoutedRPC(BucketLedger.BeginRpc, id, (int)action, area, target);
        return true;
    }

    public static void SyncPeriodically() {
        if (Time.time < _nextSync) return;
        _nextSync = Time.time + SyncInterval;
        if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
        long session = ZDOMan.GetSessionID();
        if (session != _session) {
            _session = session;
            Confirmed.Clear();
            LastHello.Clear();
        }
        var player = Player.m_localPlayer;
        if (player == null) return;
        RefreshVisuals(player);
        if (!Network.ServerSync.ServerRunsTerraform) return;
        foreach (var item in player.GetInventory().GetAllItems()) {
            if (!IsBucket(item)) continue;
            long id = EnsureId(item);
            if (Confirmed.Contains(id)) continue;
            if (!LastHello.TryGetValue(id, out float sent) || Time.time - sent >= HelloInterval) SendHello(item, id);
        }
    }

    private static void RefreshVisuals(Player player) {
        bool changed = false;
        foreach (var item in player.GetInventory().GetAllItems())
            if (IsBucket(item)) changed |= ApplyVariant(item);
        if (changed) InventoryChanged.Invoke(player.GetInventory(), new object[] { true, false });
        PublishHeldWater(player);
    }

    private static long EnsureId(ItemDrop.ItemData item) {
        if (item.m_customData.TryGetValue(IdKey, out var value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id != 0L) return id;
        do id = WaterTransfers.NewNonce(); while (id == 0L);
        item.m_customData[IdKey] = id.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    private static void SendHello(ItemDrop.ItemData item, long id) {
        LastHello[id] = Time.time;
        ZRoutedRpc.instance.InvokeRoutedRPC(BucketLedger.HelloRpc, id, Amount(item));
    }

    private static ItemDrop.ItemData FindBucket(Player player, long id) {
        foreach (var item in player.GetInventory().GetAllItems()) {
            if (!IsBucket(item) || !item.m_customData.TryGetValue(IdKey, out var value)) continue;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long itemId) && itemId == id) return item;
        }
        return null;
    }

    public static void RPC_State(long sender, long id, float amount, float capacity, int outcome) {
        if (!BucketLedger.IsFromServer(sender)) return;
        var player = Player.m_localPlayer;
        var item = player != null ? FindBucket(player, id) : null;
        if (item == null) return;
        Confirmed.Add(id);
        WriteNumber(item, AmountKey, amount);
        WriteNumber(item, CapacityKey, capacity);
        ApplyVariant(item);
        InventoryChanged.Invoke(player.GetInventory(), new object[] { true, false });
        PublishHeldWater(player);
        switch ((BucketOutcome)outcome) {
            case BucketOutcome.Empty:
                Tell(player, "The bucket is empty.");
                break;
            case BucketOutcome.Full:
                Tell(player, "The bucket is full.");
                break;
            case BucketOutcome.NowhereToSettle:
                Tell(player, "The water has nowhere to settle here.");
                break;
            case BucketOutcome.NoWater:
                Tell(player, "No water is available here.");
                break;
            case BucketOutcome.Poured:
            case BucketOutcome.Filled:
                ShowTransfer(player, id, (BucketOutcome)outcome == BucketOutcome.Poured, Fill(item));
                break;
        }
    }

    private static void ShowTransfer(Player player, long id, bool poured, float fill) {
        if (LastTarget.TryGetValue(id, out var point)) {
            point.y = Mathf.Max(point.y, PlacedWater.GetCombinedLevel(point));
            PlayEffect("fx_float_hitwater", point);
            if (poured) PlayEffect("sfx_land_water", point);
        }
        Animation(player).SetTrigger("interact");
        Tell(player, $"Bucket: {Mathf.RoundToInt(fill * 100f)}% full");
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

internal sealed class BucketHangingHold : MonoBehaviour {
    private const float GripDrop = 0.04f;
    public Vector3 Rim;
    private Transform _hand;
    private Transform _body;

    private void Start() {
        if (GetComponentInParent<ItemDrop>() != null || transform.parent == null) {
            enabled = false;
            return;
        }
        _hand = transform.parent;
        var character = GetComponentInParent<Character>();
        _body = character != null ? character.transform : transform.root;
    }

    private void LateUpdate() {
        var forward = Vector3.ProjectOnPlane(_body.forward, Vector3.up);
        var rotation = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
        transform.SetPositionAndRotation(_hand.position + Vector3.down * GripDrop - rotation * Rim, rotation);
    }
}

internal sealed class BucketWaterVisual : MonoBehaviour {
    public Renderer Water;
    private ZNetView _holder;

    private void Start() {
        var itemDrop = GetComponentInParent<ItemDrop>();
        if (itemDrop != null) {
            Water.enabled = WaterBucket.HasWater(itemDrop.m_itemData);
            enabled = false;
            return;
        }
        var character = GetComponentInParent<Character>();
        _holder = character != null ? character.GetComponent<ZNetView>() : null;
        if (_holder == null) enabled = false;
    }

    private void Update() {
        if (!_holder.IsValid()) return;
        bool hasWater = WaterBucket.HeldBucketHasWater(_holder.GetZDO());
        if (Water.enabled != hasWater) Water.enabled = hasWater;
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
            __result += $"\nWater: {Mathf.RoundToInt(WaterBucket.Fill(__instance) * 100f)}%";
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
