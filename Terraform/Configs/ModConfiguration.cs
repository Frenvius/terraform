using BepInEx.Configuration;

namespace Terraform.Configs;

public static class ModConfig {
    public static ConfigEntry<float> BrushSize { get; private set; }
    public static ConfigEntry<float> BrushStrength { get; private set; }
    public static ConfigEntry<bool> RespectBuildPieces { get; private set; }
    public static ConfigEntry<float> BuildPieceMargin { get; private set; }
    public static ConfigEntry<bool> DebugMode { get; private set; }

    public static void Init(ConfigFile config) {
        BrushSize = config.Bind("Digging", "BrushSize", 2f,
            new ConfigDescription("Radius of the dig brush in world units.", new AcceptableValueRange<float>(0.5f, 8f)));

        BrushStrength = config.Bind("Digging", "BrushStrength", 0.5f,
            new ConfigDescription("How fast terrain is removed per swing.", new AcceptableValueRange<float>(0.1f, 1f)));

        RespectBuildPieces = config.Bind("Digging", "RespectBuildPieces", true,
            "When digging near player-built pieces, carve the terrain out of their volume so the voxel surface stops at walls and foundations instead of clipping through them.");

        BuildPieceMargin = config.Bind("Digging", "BuildPieceMargin", 0.1f,
            new ConfigDescription("Extra distance (world units) around a build piece that is also carved away, so terrain pulls slightly clear of the piece face.",
                new AcceptableValueRange<float>(0f, 1f)));

        DebugMode = config.Bind("Debug", "DebugMode", false,
            "Log extra info to the BepInEx console.");
    }
}
