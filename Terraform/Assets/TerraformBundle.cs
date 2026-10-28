using System.Reflection;
using UnityEngine;

namespace Terraform.Assets;

public static class TerraformBundle {
    private const string EmbeddedResourceName = "Terraform.Resources.terraform";

    private static AssetBundle _bundle;
    private static bool _loadAttempted;

    public static T Load<T>(string name) where T : Object {
        if (!_loadAttempted) TryLoad();
        if (_bundle == null) return null;
        var asset = _bundle.LoadAsset<T>(name);
        if (asset == null) TerraformPlugin.Log.LogError($"[bundle] {typeof(T).Name} '{name}' not found in bundle");
        return asset;
    }

    private static void TryLoad() {
        _loadAttempted = true;
        try {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null) {
                TerraformPlugin.Log.LogError($"[bundle] embedded resource '{EmbeddedResourceName}' not found - rebuild bundle in Unity then rebuild DLL");
                return;
            }
            _bundle = AssetBundle.LoadFromStream(stream);
            if (_bundle == null) {
                TerraformPlugin.Log.LogError("[bundle] AssetBundle.LoadFromStream returned null");
                return;
            }
            TerraformPlugin.Log.LogInfo($"[bundle] loaded embedded bundle ({stream.Length:N0} bytes)");
        }
        catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"[bundle] load failed: {ex.Message}");
        }
    }
}
