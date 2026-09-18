using UnityEngine;

namespace Terraform.Assets;

public static class WaterfallAssets {
    private const string PrefabName = "alpha_waterfall";

    private static GameObject _alphaWaterfallPrefab;
    private static bool _loadAttempted;

    public static GameObject AlphaWaterfall {
        get {
            if (!_loadAttempted) {
                _loadAttempted = true;
                _alphaWaterfallPrefab = TerraformBundle.LoadPrefab(PrefabName);
            }
            return _alphaWaterfallPrefab;
        }
    }
}
