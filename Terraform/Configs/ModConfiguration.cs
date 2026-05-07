using BepInEx.Configuration;

namespace Terraform.Configs;

public static class ModConfig {
    public static ConfigEntry<float> BrushSize { get; private set; }
    public static ConfigEntry<float> BrushStrength { get; private set; }
    public static ConfigEntry<bool> DebugMode { get; private set; }

    public static void Init(ConfigFile config) {
        BrushSize = config.Bind("Digging", "BrushSize", 2f,
            new ConfigDescription("Radius of the dig brush in world units.", new AcceptableValueRange<float>(0.5f, 8f)));

        BrushStrength = config.Bind("Digging", "BrushStrength", 0.5f,
            new ConfigDescription("How fast terrain is removed per swing.", new AcceptableValueRange<float>(0.1f, 1f)));

        DebugMode = config.Bind("Debug", "DebugMode", false,
            "Log extra info to the BepInEx console.");
    }
}
