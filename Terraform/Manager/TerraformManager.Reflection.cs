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
    private static MethodInfo _getBiomeColorByEnum;

    private static FieldInfo _renderMeshField2;

    private static void EnsureTerrainCompReflection() {
        if (_saveMethod != null) return;
        var t     = typeof(TerrainComp);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _saveMethod            = t.GetMethod("Save",            flags);
        _tcPaintClearedMethod  = AccessTools.Method(typeof(TerrainComp), "PaintCleared",
            new[] { typeof(Vector3), typeof(float), typeof(TerrainModifier.PaintType), typeof(bool), typeof(bool) });
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

    private static System.Array GetCornerBiomes(Heightmap hm) {
        if (_cornerBiomesField == null) {
            _cornerBiomesField = AccessTools.Field(typeof(Heightmap), "m_cornerBiomes");
        }
        return _cornerBiomesField?.GetValue(hm) as System.Array;
    }

    private static Color32 GetBiomeColorReflected(object biomeEnumValue) {
        if (_getBiomeColorByEnum == null) {
            foreach (var m in typeof(Heightmap).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
                if (m.Name != "GetBiomeColor") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsEnum) {
                    _getBiomeColorByEnum = m;
                    break;
                }
            }
        }
        if (_getBiomeColorByEnum == null) return new Color32(0, 0, 0, 0);
        return (Color32)_getBiomeColorByEnum.Invoke(null, new[] { biomeEnumValue });
    }
}
