using BepInEx.Configuration;
using Terraform.Water;

namespace Terraform.Configs;

public static class ModConfig {
    public static ConfigEntry<float> BrushSize { get; private set; }
    public static ConfigEntry<bool> RespectBuildPieces { get; private set; }
    public static ConfigEntry<float> BuildPieceMargin { get; private set; }
    public static ConfigEntry<bool> DebugMode { get; private set; }
    public static ConfigEntry<float> WaterOpaqueDepth { get; private set; }
    public static ConfigEntry<float> BucketCapacity { get; private set; }

    private static void ApplyBucketCapacity() =>
        BucketWaterMath.Capacity = BucketCapacity.Value / (PlacedWater.Scale * PlacedWater.Scale);

    public static void Init(ConfigFile config) {
        BucketCapacity = config.Bind("Water", "BucketCapacity", 4f,
            new ConfigDescription("Water the bucket holds, in cubic metres. Applied live.",
                new AcceptableValueRange<float>(0.25f, 50f)));
        BucketCapacity.SettingChanged += (_, _) => ApplyBucketCapacity();
        ApplyBucketCapacity();

        WaterOpaqueDepth = config.Bind("Water", "OpaqueDepth", 15f,
            new ConfigDescription("Depth (world units) at which poured water hides the ground below it. The ocean uses 15; lower values make pools murkier. Applied live.",
                new AcceptableValueRange<float>(0.5f, 50f)));

        BrushSize = config.Bind("Digging", "BrushSize", 2f,
            new ConfigDescription("Radius of the dig brush in world units.", new AcceptableValueRange<float>(0.5f, 8f)));

        RespectBuildPieces = config.Bind("Digging", "RespectBuildPieces", true,
            "When digging near player-built pieces, carve the terrain out of their volume so the voxel surface stops at walls and foundations instead of clipping through them.");

        BuildPieceMargin = config.Bind("Digging", "BuildPieceMargin", 0.1f,
            new ConfigDescription("Extra distance (world units) around a build piece that is also carved away, so terrain pulls slightly clear of the piece face.",
                new AcceptableValueRange<float>(0f, 1f)));

        DebugMode = config.Bind("Debug", "DebugMode", false,
            "Log extra info to the BepInEx console.");
    }
}
