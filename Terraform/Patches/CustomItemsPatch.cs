using System.Collections.Generic;
using HarmonyLib;
using Terraform.Assets;
using Terraform.Water;
using UnityEngine;

namespace Terraform.Patches;

public static class CustomItems {
    private const string MockMaterialPrefix = "JVLmock_";
    private static readonly string[] ItemPrefabNames = { "tfm_water_bucket" };

    private static List<GameObject> _prefabs;
    private static bool _mocksResolved;

    public static List<GameObject> Prefabs {
        get {
            if (_prefabs != null) return _prefabs;
            _prefabs = new List<GameObject>();
            foreach (var name in ItemPrefabNames) {
                var prefab = TerraformBundle.Load<GameObject>(name);
                if (prefab == null) continue;
                if (!HasItemData(prefab)) {
                    TerraformPlugin.Log.LogError($"[items] '{name}' has no usable ItemDrop data in the bundle (scripts missing or stale at bundle build); not registered");
                    continue;
                }
                WaterBucket.Configure(prefab);
                _prefabs.Add(prefab);
            }
            return _prefabs;
        }
    }

    private static bool HasItemData(GameObject prefab) {
        var itemDrop = prefab.GetComponent<ItemDrop>();
        return itemDrop != null && itemDrop.m_itemData?.m_shared != null;
    }

    public static void AddMissing(List<GameObject> target) {
        foreach (var prefab in Prefabs)
            if (!target.Contains(prefab)) target.Add(prefab);
    }

    public static void ResolveMockMaterials() {
        if (_mocksResolved) return;
        _mocksResolved = true;

        var vanilla = new Dictionary<string, Material>();
        foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            if (!material.name.StartsWith(MockMaterialPrefix)) vanilla[material.name] = material;

        foreach (var prefab in Prefabs)
        foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true)) {
            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++) {
                if (materials[i] == null || !materials[i].name.StartsWith(MockMaterialPrefix)) continue;
                var vanillaName = materials[i].name.Substring(MockMaterialPrefix.Length);
                if (vanilla.TryGetValue(vanillaName, out var replacement)) materials[i] = replacement;
                else TerraformPlugin.Log.LogWarning($"[items] no vanilla material '{vanillaName}' for {prefab.name}/{renderer.name}");
            }
            renderer.sharedMaterials = materials;
        }
    }
}

[HarmonyPatch(typeof(ZNetScene), "Awake")]
public static class ZNetScene_Awake_RegisterItems_Patch {
    static void Prefix(ZNetScene __instance) {
        CustomItems.ResolveMockMaterials();
        CustomItems.AddMissing(__instance.m_prefabs);
        PlacedWater.Register(__instance);
    }
}

[HarmonyPatch(typeof(ObjectDB), "Awake")]
public static class ObjectDB_Awake_RegisterItems_Patch {
    static void Prefix(ObjectDB __instance) => CustomItems.AddMissing(__instance.m_items);
}
