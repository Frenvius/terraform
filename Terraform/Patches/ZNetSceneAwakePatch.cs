using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

[HarmonyPatch(typeof(ZNetScene), "Awake")]
public static class ZNetScene_Awake_Patch {
    static void Postfix() {
        var go = new GameObject("TerraformManager");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<TerraformManager>();
        TerraformPlugin.Log.LogInfo("TerraformManager spawned.");
    }
}
