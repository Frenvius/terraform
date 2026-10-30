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

    public static void Register(ObjectDB db) {
        if (db == null || db.m_items.Count == 0) return;
        AddMissing(db.m_items);
        AddWaterBucketRecipe(db);
        DirtPaintOp.Register(db);
    }

    private static void AddWaterBucketRecipe(ObjectDB db) {
        const string recipeName = "Recipe_" + WaterBucket.ItemName;
        if (db.m_recipes.Exists(recipe => recipe != null && recipe.name == recipeName)) return;

        var bucket = FindItem(db, WaterBucket.ItemName);
        if (bucket == null) return;
        var fineWood = FindItem(db, "FineWood");
        var tin      = FindItem(db, "Tin");
        var forge    = FindStation(db, "forge");
        if (fineWood == null || tin == null || forge == null) {
            TerraformPlugin.Log.LogError($"[items] water bucket recipe skipped: FineWood={fineWood != null} Tin={tin != null} forge={forge != null}");
            return;
        }

        var recipe = ScriptableObject.CreateInstance<Recipe>();
        recipe.name = recipeName;
        recipe.m_item = bucket;
        recipe.m_craftingStation = forge;
        recipe.m_repairStation = forge;
        recipe.m_minStationLevel = 1;
        recipe.m_resources = new[] {
            new Piece.Requirement { m_resItem = fineWood, m_amount = 6, m_amountPerLevel = 0 },
            new Piece.Requirement { m_resItem = tin,      m_amount = 2, m_amountPerLevel = 0 },
        };
        db.m_recipes.Add(recipe);
    }

    private static ItemDrop FindItem(ObjectDB db, string name) {
        var prefab = db.m_items.Find(item => item != null && item.name == name);
        return prefab != null ? prefab.GetComponent<ItemDrop>() : null;
    }

    private static CraftingStation FindStation(ObjectDB db, string name) {
        foreach (var recipe in db.m_recipes)
            if (recipe != null && recipe.m_craftingStation != null && recipe.m_craftingStation.name == name)
                return recipe.m_craftingStation;
        return null;
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
        Network.VoxelZone.Register(__instance);
    }
}

[HarmonyPatch(typeof(ObjectDB), "Awake")]
public static class ObjectDB_Awake_RegisterItems_Patch {
    static void Prefix(ObjectDB __instance) => CustomItems.Register(__instance);
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
public static class ObjectDB_CopyOtherDB_RegisterItems_Patch {
    static void Prefix(ObjectDB other) => CustomItems.Register(other);
}
