using BepInEx.Configuration;
using Terraform.Water;

namespace Terraform.Configs;

public static class ModConfig {
    public static ConfigEntry<float> BrushSize { get; private set; }
    public static ConfigEntry<float> StonePerCubicMeter { get; private set; }
    public static ConfigEntry<bool> DebugMode { get; private set; }
    public static ConfigEntry<float> BucketCapacity { get; private set; }

    private static void ApplyBucketCapacity() =>
        BucketWaterMath.Capacity = BucketCapacity.Value / (PlacedWater.Scale * PlacedWater.Scale);

    public static void Init(ConfigFile config) {
        BucketCapacity = config.Bind("Water", "BucketCapacity", 4f,
            new ConfigDescription("Water the bucket holds, in cubic metres. Applied live.",
                new AcceptableValueRange<float>(0.25f, 50f)));
        BucketCapacity.SettingChanged += (_, _) => ApplyBucketCapacity();
        ApplyBucketCapacity();

        BrushSize = config.Bind("Digging", "BrushSize", 2f,
            new ConfigDescription("Radius of the dig brush in world units.", new AcceptableValueRange<float>(0.5f, 8f)));

        StonePerCubicMeter = config.Bind("Digging", "StonePerCubicMeter", 0.4f,
            new ConfigDescription("Stone dropped per cubic metre of ground the pickaxe removes. Hoe level / smooth that fills ground is paid back from later digs. Applied live.",
                new AcceptableValueRange<float>(0f, 5f)));

        DebugMode = config.Bind("Debug", "DebugMode", false,
            "Log extra info to the BepInEx console and enable the debug keys (F1 smooth, F3 raise, F4 dig, F10-F12 diagnostics).");
    }
}
