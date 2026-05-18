using System.Reflection;
using UnityEngine;

namespace Terraform.Assets;

public static class WaterfallAssets {
    private const string EmbeddedResourceName = "Terraform.Resources.terraform_waterfall";
    private const string PrefabName = "alpha_waterfall";

    private static AssetBundle _bundle;
    private static GameObject _alphaWaterfallPrefab;
    private static bool _loadAttempted;

    public static GameObject AlphaWaterfall {
        get {
            if (!_loadAttempted) TryLoad();
            return _alphaWaterfallPrefab;
        }
    }

    private static void TryLoad() {
        _loadAttempted = true;
        try {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null) {
                TerraformPlugin.Log.LogError($"[waterfall-assets] embedded resource '{EmbeddedResourceName}' not found — rebuild bundle in Unity then rebuild DLL");
                return;
            }
            _bundle = AssetBundle.LoadFromStream(stream);
            if (_bundle == null) {
                TerraformPlugin.Log.LogError("[waterfall-assets] AssetBundle.LoadFromStream returned null");
                return;
            }
            _alphaWaterfallPrefab = _bundle.LoadAsset<GameObject>(PrefabName);
            if (_alphaWaterfallPrefab == null) {
                TerraformPlugin.Log.LogError($"[waterfall-assets] prefab '{PrefabName}' not found in bundle");
                return;
            }
            TerraformPlugin.Log.LogInfo($"[waterfall-assets] loaded embedded bundle ({stream.Length:N0} bytes)");
        }
        catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"[waterfall-assets] load failed: {ex.Message}");
        }
    }
}
