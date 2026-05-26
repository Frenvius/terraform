using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch]
public static class Pathfinding_SnapToNavMesh_SeaLevel_Patch {
    static MethodBase TargetMethod() {
        return AccessTools.Method(typeof(Pathfinding), "SnapToNavMesh");
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        foreach (var ins in instructions) {
            if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f && Mathf.Approximately(f, 30f))
                yield return new CodeInstruction(OpCodes.Ldc_R4, -10000f);
            else
                yield return ins;
        }
    }
}
