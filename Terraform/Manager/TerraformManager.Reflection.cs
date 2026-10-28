using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static MethodInfo _saveMethod;
    private static FieldInfo  _heightsField;
    private static MethodInfo _tcPaintClearedMethod;

    private static FieldInfo  _materialInstanceField;
    private static FieldInfo  _paintMaskField;
    private static FieldInfo  _cornerBiomesField;

    private static FieldInfo _renderMeshField2;

    private static readonly TerrainOp.Settings DirtPaintSettings = new() { m_paintType = TerrainModifier.PaintType.Dirt };
    private static readonly object[] PaintClearedArgs = new object[3];
    private static readonly object[] SaveArgs = { false };

    private static void EnsureTerrainCompReflection() {
        if (_saveMethod != null) return;
        var t     = typeof(TerrainComp);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _saveMethod            = t.GetMethod("Save",            flags);
        _tcPaintClearedMethod  = AccessTools.Method(typeof(TerrainComp), "PaintCleared",
            new[] { typeof(Vector3), typeof(Vector3), typeof(TerrainOp.Settings) });
        if (_saveMethod == null || _tcPaintClearedMethod == null)
            TerraformPlugin.Log.LogError("[reflection] TerrainComp.Save/PaintCleared not found - dig paint disabled");
    }

    private static void PaintDirtCleared(TerrainComp tc, Vector3 worldPos, float radius) {
        DirtPaintSettings.m_paintRadius = radius;
        PaintClearedArgs[0] = worldPos;
        PaintClearedArgs[1] = Vector3.zero;
        PaintClearedArgs[2] = DirtPaintSettings;
        _tcPaintClearedMethod.Invoke(tc, PaintClearedArgs);
    }

    private static void SaveTerrainComp(TerrainComp tc) {
        _saveMethod?.Invoke(tc, SaveArgs);
    }

    private static void EnsureHeightmapReflection() {
        if (_materialInstanceField != null) return;
        _materialInstanceField = AccessTools.Field(typeof(Heightmap), "m_materialInstance");
        _paintMaskField        = AccessTools.Field(typeof(Heightmap), "m_paintMask");
    }

    private static List<float> GetHeightsArray(Heightmap hm) {
        if (_heightsField == null) {
            _heightsField = typeof(Heightmap).GetField("m_heights",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        return _heightsField?.GetValue(hm) as List<float>;
    }

    private static BiomeSector[] GetCornerBiomes(Heightmap hm) {
        if (_cornerBiomesField == null) {
            _cornerBiomesField = AccessTools.Field(typeof(Heightmap), "m_cornerBiomes");
        }
        return _cornerBiomesField?.GetValue(hm) as BiomeSector[];
    }

    private static Color32 CornerBiomeColor(BiomeSector[] cornerBiomes, int corner) {
        var sector = cornerBiomes?[corner];
        return sector != null ? Heightmap.GetBiomeColor(sector) : new Color32(0, 0, 0, 0);
    }
}
