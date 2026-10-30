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

    private static byte[] ReadResource(string resourceName) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        var bytes = new byte[stream.Length];
        int read = 0;
        while (read < bytes.Length) {
            int chunk = stream.Read(bytes, read, bytes.Length - read);
            if (chunk <= 0) break;
            read += chunk;
        }
        return bytes;
    }

    private static void TryLoad() {
        _loadAttempted = true;
        try {
            var bytes = ReadResource(EmbeddedResourceName);
            if (bytes == null) {
                TerraformPlugin.Log.LogError($"[bundle] embedded resource '{EmbeddedResourceName}' not found - rebuild bundle in Unity then rebuild DLL");
                return;
            }
            _bundle = AssetBundle.LoadFromMemory(bytes);
            if (_bundle == null) {
                TerraformPlugin.Log.LogError("[bundle] AssetBundle.LoadFromMemory returned null");
                return;
            }
            TerraformPlugin.Log.LogInfo($"[bundle] loaded embedded bundle ({bytes.Length:N0} bytes)");
        }
        catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"[bundle] load failed: {ex.Message}");
        }
    }
}
