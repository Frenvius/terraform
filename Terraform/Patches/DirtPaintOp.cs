using UnityEngine;

namespace Terraform.Patches;

public static class DirtPaintOp {
    public const string PrefabName = "tfm_dirt_paint";
    public const float Radius = 1f;
    private const string ApplyOperationRpc = "RPC_ApplyOperation";

    private static TerrainOp _prefab;

    public static void Register(ObjectDB db) {
        if (_prefab == null) {
            var root = new GameObject("TerraformTerrainOpPrefabs");
            root.SetActive(false);
            Object.DontDestroyOnLoad(root);
            var go = new GameObject(PrefabName);
            go.transform.SetParent(root.transform, false);
            _prefab = go.AddComponent<TerrainOp>();
            _prefab.m_settings = new TerrainOp.Settings {
                m_level = false, m_raise = false, m_smooth = false,
                m_paintCleared = true, m_paintHeightCheck = false,
                m_paintType = TerrainModifier.PaintType.Dirt, m_paintRadius = Radius,
            };
        }
        if (!db.m_terrainOps.Contains(_prefab)) db.m_terrainOps.Add(_prefab);
    }

    public static void Request(TerrainComp tc, Vector3 worldPos) {
        if (_prefab == null) return;
        var view = tc.GetComponent<ZNetView>();
        if (view == null || !view.IsValid()) return;
        var pkg = new ZPackage();
        pkg.Write(worldPos);
        pkg.Write(false);
        _prefab.m_settings.Serialize(pkg, _prefab.gameObject);
        view.InvokeRPC(ApplyOperationRpc, pkg);
    }
}
