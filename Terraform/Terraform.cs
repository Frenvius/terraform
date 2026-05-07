using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Terraform.Configs;

namespace Terraform;

[BepInPlugin(ModGuid, ModName, Version)]
public class TerraformPlugin : BaseUnityPlugin {
    public const string ModGuid    = "com.frenvius.Terraform";
    public const string ModName    = "Terraform";
    public const string Version    = "0.1.0";

    internal static ManualLogSource Log;
    private Harmony _harmony;

    private void Awake() {
        Log = Logger;
        ModConfig.Init(Config);

        _harmony = new Harmony(ModGuid);
        _harmony.PatchAll();

        Log.LogInfo($"{ModName} {Version} loaded.");
    }

    private void OnDestroy() {
        _harmony?.UnpatchSelf();
    }
}
