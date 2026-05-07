using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Terraform.Patches;

public partial class TerraformManager {
    private Dictionary<int, GameObject> _debugGrids;
    private bool _showDebugGrid;

    private Dictionary<int, GameObject> _debugChunkWalls;
    private Material _debugChunkWallMaterial;

    private bool _debugMagenta = false;
    private Material _debugMaterial;

    private bool _debugNoNormalMap;
    private bool _debugHideHeightmaps;

    private Material GetOrCreateDebugMaterial() {
        if (_debugMaterial != null) return _debugMaterial;
        var shader = Shader.Find("Unlit/Color")
                  ?? Shader.Find("Legacy Shaders/Diffuse")
                  ?? Shader.Find("Diffuse")
                  ?? Shader.Find("Hidden/Internal-Colored");
        if (shader == null) return null;
        _debugMaterial = new Material(shader) { name = "TerraformVoxelDebugMagenta", color = Color.magenta };
        if (_debugMaterial.HasProperty("_Color")) _debugMaterial.SetColor("_Color", Color.magenta);
        return _debugMaterial;
    }

    private void InspectTriangleAtCrosshair() {
        if (GameCamera.instance == null) {
            TerraformPlugin.Log.LogInfo("[inspect] no camera");
            return;
        }
        var cam  = GameCamera.instance.transform;
        int mask = LayerMask.GetMask("piece", "terrain");
        if (!Physics.Raycast(cam.position, cam.forward, out var hit, 200f, mask)) {
            TerraformPlugin.Log.LogInfo("[inspect] no hit");
            return;
        }

        var col = hit.collider as MeshCollider;
        if (col == null || col.sharedMesh == null) {
            TerraformPlugin.Log.LogInfo($"[inspect] hit {hit.collider?.gameObject.name} (not a MeshCollider)");
            return;
        }
        var go   = col.gameObject;
        var mesh = col.sharedMesh;
        int t    = hit.triangleIndex;
        if (t < 0) {
            TerraformPlugin.Log.LogInfo($"[inspect] hit {go.name} but no triangleIndex");
            return;
        }

        var tris = mesh.triangles;
        if (t * 3 + 2 >= tris.Length) { TerraformPlugin.Log.LogWarning("[inspect] tri index out of range"); return; }
        int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];

        var verts   = mesh.vertices;
        var normals = mesh.normals;
        var uvs     = mesh.uv;
        var colors  = mesh.colors32;
        var tangs   = mesh.tangents;

        Vector3 v0 = verts[i0], v1 = verts[i1], v2 = verts[i2];
        Vector3 wv0 = go.transform.TransformPoint(v0);
        Vector3 wv1 = go.transform.TransformPoint(v1);
        Vector3 wv2 = go.transform.TransformPoint(v2);
        Vector3 faceN = Vector3.Cross(v1 - v0, v2 - v0).normalized;
        Vector3 worldFaceN = go.transform.TransformDirection(faceN);
        Vector3 viewDir = (cam.position - hit.point).normalized;
        float facing = Vector3.Dot(worldFaceN, viewDir);

        TerraformPlugin.Log.LogInfo($"[inspect] hit {go.name} tri#{t} mat={col.GetComponent<MeshRenderer>()?.sharedMaterial?.shader?.name}");
        TerraformPlugin.Log.LogInfo($"[inspect]   hitWorld={hit.point.ToString("F2")}  hitNormal={hit.normal.ToString("F2")}");
        TerraformPlugin.Log.LogInfo($"[inspect]   v0 local={v0.ToString("F2")} world={wv0.ToString("F2")}");
        TerraformPlugin.Log.LogInfo($"[inspect]   v1 local={v1.ToString("F2")} world={wv1.ToString("F2")}");
        TerraformPlugin.Log.LogInfo($"[inspect]   v2 local={v2.ToString("F2")} world={wv2.ToString("F2")}");
        if (i0 < normals.Length) TerraformPlugin.Log.LogInfo($"[inspect]   n0={normals[i0].ToString("F3")} |n0|²={normals[i0].sqrMagnitude:F4}");
        if (i1 < normals.Length) TerraformPlugin.Log.LogInfo($"[inspect]   n1={normals[i1].ToString("F3")} |n1|²={normals[i1].sqrMagnitude:F4}");
        if (i2 < normals.Length) TerraformPlugin.Log.LogInfo($"[inspect]   n2={normals[i2].ToString("F3")} |n2|²={normals[i2].sqrMagnitude:F4}");
        if (i0 < uvs.Length)     TerraformPlugin.Log.LogInfo($"[inspect]   uv0={uvs[i0]} uv1={uvs[i1]} uv2={uvs[i2]}");
        if (i0 < colors.Length)  TerraformPlugin.Log.LogInfo($"[inspect]   c0={colors[i0]} c1={colors[i1]} c2={colors[i2]}");
        if (i0 < tangs.Length)   TerraformPlugin.Log.LogInfo($"[inspect]   t0={tangs[i0].ToString("F3")} t1={tangs[i1].ToString("F3")} t2={tangs[i2].ToString("F3")}");
        TerraformPlugin.Log.LogInfo($"[inspect]   faceN(local)={faceN.ToString("F3")} faceN(world)={worldFaceN.ToString("F3")}");
        TerraformPlugin.Log.LogInfo($"[inspect]   dot(faceN, viewToCam)={facing:F3}  → {(facing > 0 ? "FRONT face (visible)" : "BACK face (would be culled if Cull Back)")}");

        var hmGo = go.transform.parent?.gameObject;
        var hm   = hmGo?.GetComponent<Heightmap>();
        if (hm != null) {
            EnsureHeightmapReflection();
            var paintMask = _paintMaskField?.GetValue(hm) as Texture2D;
            if (paintMask != null) {
                try {
                    Color p0 = paintMask.GetPixelBilinear(uvs[i0].x, uvs[i0].y);
                    Color p1 = paintMask.GetPixelBilinear(uvs[i1].x, uvs[i1].y);
                    Color p2 = paintMask.GetPixelBilinear(uvs[i2].x, uvs[i2].y);
                    TerraformPlugin.Log.LogInfo($"[inspect]   paintMask uv0={p0} uv1={p1} uv2={p2}  (R=Dirt G=Cultivate B=Paved)");
                } catch (System.Exception e) {
                    TerraformPlugin.Log.LogInfo($"[inspect]   paintMask not readable: {e.Message}");
                }
            } else {
                TerraformPlugin.Log.LogInfo("[inspect]   paintMask null on heightmap");
            }
        }
    }

    private void ToggleHideHeightmaps() {
        _debugHideHeightmaps = !_debugHideHeightmaps;
        if (_renderMeshField2 == null)
            _renderMeshField2 = AccessTools.Field(typeof(Heightmap), "m_renderMesh");

        int affected = 0;
        foreach (var hm in FindObjectsByType<Heightmap>(FindObjectsSortMode.None)) {
            if (_debugHideHeightmaps) {
                _renderMeshField2.SetValue(hm, null);
                affected++;
            } else {
                hm.Poke(false);
                affected++;
            }
        }
        TerraformPlugin.Log.LogInfo($"[debug] hide-heightmaps={_debugHideHeightmaps} ({affected} heightmaps)");
    }

    private void ToggleDebugNoNormalMap() {
        _debugNoNormalMap = !_debugNoNormalMap;
        EnsureHeightmapReflection();
        int swapped = 0;
        foreach (var kv in _chunks) {
            var data = kv.Value;
            if (data == null || data.Renderer == null || data.Heightmap == null) continue;
            var liveMat = _materialInstanceField?.GetValue(data.Heightmap) as Material;
            if (liveMat == null) continue;
            if (_debugNoNormalMap) {
                var clone = new Material(liveMat);
                clone.DisableKeyword("_NORMALMAP");
                if (clone.HasProperty("_Displacement")) clone.SetFloat("_Displacement", 0f);
                if (clone.HasProperty("_BumpScale"))    clone.SetFloat("_BumpScale", 0f);
                data.Renderer.sharedMaterial = clone;
            } else {
                data.Renderer.sharedMaterial = liveMat;
            }
            swapped++;
        }
        TerraformPlugin.Log.LogInfo($"[debug] no-normal-map={_debugNoNormalMap} ({swapped} chunks)");
    }

    private void ToggleDebugMagenta() {
        _debugMagenta = !_debugMagenta;
        int swapped = 0;
        foreach (var kv in _chunks) {
            var data = kv.Value;
            if (data == null || data.Renderer == null) continue;
            TryApplyTerrainMaterial(data);
            swapped++;
        }
        TerraformPlugin.Log.LogInfo($"[debug] magenta={_debugMagenta} ({swapped} chunks)");
    }

    private void RefreshDebugGrids() {
        ClearDebugGrids();
        foreach (var hm in FindObjectsByType<Heightmap>(FindObjectsSortMode.None)) {
            if (hm.gameObject.name.Contains("lodMesh")) continue;
            var heights = GetHeightsArray(hm);
            if (heights == null || heights.Count == 0) continue;
            _debugGrids[hm.GetInstanceID()] = BuildHeightmapGrid(hm, heights);
        }
    }

    private void ClearDebugGrids() {
        foreach (var go in _debugGrids.Values)
            if (go != null) Destroy(go);
        _debugGrids.Clear();
    }

    private GameObject BuildHeightmapGrid(Heightmap hm, List<float> heights) {
        int   width = hm.m_width + 1;
        float scale = hm.m_scale;
        float half  = hm.m_width * 0.5f;

        const float yOffset = 0.05f;
        float originY = hm.transform.position.y;

        var verts = new Vector3[width * width];
        for (int z = 0; z < width; z++)
        for (int x = 0; x < width; x++) {
            int i = z * width + x;
            float h = heights[i];
            verts[i] = new Vector3((x - half) * scale, h - originY + yOffset, (z - half) * scale);
        }

        var indices = new List<int>((width - 1) * width * 4);
        for (int z = 0; z < width; z++)
        for (int x = 0; x < width; x++) {
            int i = z * width + x;
            if (x < width - 1) { indices.Add(i); indices.Add(i + 1); }
            if (z < width - 1) { indices.Add(i); indices.Add(i + width); }
        }

        var mesh = new Mesh { name = "TerraformDebugGrid" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
        mesh.RecalculateBounds();

        var go = new GameObject("TerraformDebugGrid");
        go.transform.SetParent(hm.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        var shader = Shader.Find("Hidden/Internal-Colored")
                  ?? Shader.Find("Sprites/Default")
                  ?? Shader.Find("Unlit/Color");
        var mat = new Material(shader) { color = new Color(0.2f, 0.6f, 1f, 1f) };
        mat.SetInt("_ZWrite", 0);
        mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        mr.material = mat;
        return go;
    }

    private void RefreshDebugChunkWalls() {
        ClearDebugChunkWalls();
        foreach (var hm in FindObjectsByType<Heightmap>(FindObjectsSortMode.None)) {
            if (hm.gameObject.name.Contains("lodMesh")) continue;
            var heights = GetHeightsArray(hm);
            if (heights == null || heights.Count == 0) continue;
            _debugChunkWalls[hm.GetInstanceID()] = BuildChunkWall(hm, heights);
        }
    }

    private void ClearDebugChunkWalls() {
        foreach (var go in _debugChunkWalls.Values)
            if (go != null) Destroy(go);
        _debugChunkWalls.Clear();
    }

    private Material GetOrCreateChunkWallMaterial() {
        if (_debugChunkWallMaterial != null) return _debugChunkWallMaterial;
        var shader = Shader.Find("Sprites/Default")
                  ?? Shader.Find("UI/Default")
                  ?? Shader.Find("Hidden/Internal-Colored");
        _debugChunkWallMaterial = new Material(shader) { color = new Color(1f, 0f, 0f, 0.3f) };
        _debugChunkWallMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _debugChunkWallMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _debugChunkWallMaterial.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _debugChunkWallMaterial.SetInt("_ZWrite",   0);
        _debugChunkWallMaterial.renderQueue = 3000;
        return _debugChunkWallMaterial;
    }

    private GameObject BuildChunkWall(Heightmap hm, List<float> heights) {
        float scale  = hm.m_scale;
        float halfX  = hm.m_width * 0.5f * scale;
        float halfZ  = halfX;
        float originY = hm.transform.position.y;

        float minH = float.MaxValue, maxH = float.MinValue;
        for (int i = 0; i < heights.Count; i++) {
            float h = heights[i];
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
        }
        float yBottom = (minH - 32f) - originY;
        float yTop    = (maxH + 16f) - originY;

        var verts = new Vector3[16];
        verts[0]  = new Vector3(-halfX, yBottom, -halfZ);
        verts[1]  = new Vector3(-halfX, yTop,    -halfZ);
        verts[2]  = new Vector3(-halfX, yTop,    +halfZ);
        verts[3]  = new Vector3(-halfX, yBottom, +halfZ);
        verts[4]  = new Vector3(+halfX, yBottom, -halfZ);
        verts[5]  = new Vector3(+halfX, yTop,    -halfZ);
        verts[6]  = new Vector3(+halfX, yTop,    +halfZ);
        verts[7]  = new Vector3(+halfX, yBottom, +halfZ);
        verts[8]  = new Vector3(-halfX, yBottom, -halfZ);
        verts[9]  = new Vector3(-halfX, yTop,    -halfZ);
        verts[10] = new Vector3(+halfX, yTop,    -halfZ);
        verts[11] = new Vector3(+halfX, yBottom, -halfZ);
        verts[12] = new Vector3(-halfX, yBottom, +halfZ);
        verts[13] = new Vector3(-halfX, yTop,    +halfZ);
        verts[14] = new Vector3(+halfX, yTop,    +halfZ);
        verts[15] = new Vector3(+halfX, yBottom, +halfZ);

        var tris = new int[24];
        int t = 0;
        for (int q = 0; q < 4; q++) {
            int b = q * 4;
            tris[t++] = b + 0; tris[t++] = b + 1; tris[t++] = b + 2;
            tris[t++] = b + 0; tris[t++] = b + 2; tris[t++] = b + 3;
        }

        var mesh = new Mesh { name = "TerraformDebugChunkWall" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        var go = new GameObject("TerraformDebugChunkWall");
        go.transform.SetParent(hm.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateChunkWallMaterial();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
        return go;
    }
}
