using System.Collections.Generic;
using Terraform.Assets;
using UnityEngine;
using UnityEngine.Rendering;

namespace Terraform.Water;

public static class PlacedWaterDepth {
    private const string ShaderName = "tfm_water_depth";
    private const int CopyPass = 0;
    private const int WaterPass = 1;
    private const CameraEvent Event = CameraEvent.BeforeForwardAlpha;
    private const float ShaderShoreCut = 0.15f;
    public const float MinVisibleDepth = 0.05f;
    private static readonly int DepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
    private static readonly int ShoreOffsetId = Shader.PropertyToID("_ShoreOffset");
    private static Material _depthMaterial;
    private static bool _loadFailed;
    private static CommandBuffer _commands;
    private static Camera _camera;
    private static RenderTexture _target;
    private static Material _boundMaterial;

    public static bool Update(Material waterMaterial, IReadOnlyList<Renderer> renderers) {
        if (waterMaterial == null || !EnsureMaterial()) return false;
        var camera = Utils.GetMainCamera();
        if (camera == null) return false;
        AttachTo(camera);
        EnsureTarget(camera, waterMaterial);
        _commands.Clear();
        if (renderers.Count == 0) return true;
        _commands.SetRenderTarget(_target);
        _commands.DrawProcedural(Matrix4x4.identity, _depthMaterial, CopyPass, MeshTopology.Triangles, 3);
        foreach (var renderer in renderers) _commands.DrawRenderer(renderer, _depthMaterial, 0, WaterPass);
        return true;
    }

    private static bool EnsureMaterial() {
        if (_depthMaterial != null) return true;
        if (_loadFailed) return false;
        var shader = TerraformBundle.Load<Shader>(ShaderName);
        if (shader == null || !shader.isSupported) {
            _loadFailed = true;
            TerraformPlugin.Log.LogError($"[water] Depth shader unavailable (supported={shader != null && shader.isSupported}); placed water keeps the camera-dependent shoreline.");
            return false;
        }
        _depthMaterial = new Material(shader) { name = "TerraformPlacedWaterDepth" };
        _depthMaterial.SetFloat(ShoreOffsetId, ShaderShoreCut - MinVisibleDepth);
        _commands = new CommandBuffer { name = "Terraform placed water depth" };
        return true;
    }

    private static void AttachTo(Camera camera) {
        if (_camera == camera) return;
        if (_camera != null) _camera.RemoveCommandBuffer(Event, _commands);
        _camera = camera;
        _camera.depthTextureMode |= DepthTextureMode.Depth;
        _camera.AddCommandBuffer(Event, _commands);
    }

    private static void EnsureTarget(Camera camera, Material waterMaterial) {
        int width = camera.pixelWidth, height = camera.pixelHeight;
        if (_target == null || _target.width != width || _target.height != height) {
            if (_target != null) _target.Release();
            _target = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) {
                name = "TerraformPlacedWaterDepth",
                filterMode = FilterMode.Point
            };
            _target.Create();
            _boundMaterial = null;
        }
        if (_boundMaterial == waterMaterial) return;
        waterMaterial.SetTexture(DepthTextureId, _target);
        _boundMaterial = waterMaterial;
    }
}
