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
        ModConfig.Init(Config);
        Network.ServerSync.Init();
        Log = new ManualLogSource(ModName);
        Log.LogEvent += (_, e) => {
            if (ShouldShow(e.Level)) Logger.Log(e.Level, e.Data);
        };

        _harmony = new Harmony(ModGuid);
        _harmony.PatchAll();

        Log.LogInfo($"{ModName} {Version} loaded.");
    }

    private static bool ShouldShow(LogLevel level) =>
        ModConfig.DebugMode.Value || (level & (LogLevel.Error | LogLevel.Fatal)) != 0;

    private void OnDestroy() {
        _harmony?.UnpatchSelf();
    }
}
