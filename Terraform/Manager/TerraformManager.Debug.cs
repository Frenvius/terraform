using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

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

    private GameObject _navOverlay;
    private bool _navOverlayOn;
    private Material _navOverlayMat;

    private void ToggleNavOverlay() {
        _navOverlayOn = !_navOverlayOn;
        if (_navOverlayOn) RefreshNavOverlay();
        else if (_navOverlay != null) { Destroy(_navOverlay); _navOverlay = null; }
        TerraformPlugin.Log.LogInfo($"[navoverlay] {_navOverlayOn}");
    }

    private void RefreshNavOverlay() {
        if (!_navOverlayOn) return;
        var tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0) return;

        if (_navOverlay == null) {
            _navOverlay = new GameObject("TerraformNavOverlay");
            _navOverlay.AddComponent<MeshFilter>();
            var mr = _navOverlay.AddComponent<MeshRenderer>();
            mr.sharedMaterial      = GetNavOverlayMaterial();
            mr.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows      = false;
        }

        var verts = new Vector3[tri.vertices.Length];
        for (int i = 0; i < verts.Length; i++) verts[i] = tri.vertices[i] + Vector3.up * 0.15f;
        var mesh = new Mesh { name = "TerraformNavOverlay", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tri.indices, 0);
        mesh.RecalculateNormals();

        var mf = _navOverlay.GetComponent<MeshFilter>();
        if (mf.sharedMesh != null) Destroy(mf.sharedMesh);
        mf.sharedMesh = mesh;
    }

    private Material GetNavOverlayMaterial() {
        if (_navOverlayMat != null) return _navOverlayMat;
        var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored");
        _navOverlayMat = new Material(sh) { name = "TerraformNavOverlayMat", color = new Color(0.15f, 1f, 0.3f, 0.45f) };
        if (_navOverlayMat.HasProperty("_Color")) _navOverlayMat.SetColor("_Color", new Color(0.15f, 1f, 0.3f, 0.45f));
        return _navOverlayMat;
    }

    private static int Uf_Find(int[] parent, int i) {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    private static void Uf_Union(int[] parent, int a, int b) {
        int ra = Uf_Find(parent, a), rb = Uf_Find(parent, b);
        if (ra != rb) parent[ra] = rb;
    }

    private void MeasureNavMeshFragmentation() {
        var player = Player.m_localPlayer;
        if (player == null) { TerraformPlugin.Log.LogInfo("[navfrag] no player"); return; }
        Vector3 pp = player.transform.position;

        var tri   = NavMesh.CalculateTriangulation();
        var verts = tri.vertices;
        var idx   = tri.indices;

        const float R2 = 25f * 25f;
        int nextId = 0;
        var weld = new Dictionary<long, int>();
        int Canon(Vector3 v) {
            long qx = Mathf.RoundToInt(v.x / 0.05f) + 1048576L;
            long qy = Mathf.RoundToInt(v.y / 0.05f) + 1048576L;
            long qz = Mathf.RoundToInt(v.z / 0.05f) + 1048576L;
            long key = qx | (qy << 21) | (qz << 42);
            if (weld.TryGetValue(key, out int id)) return id;
            id = nextId++; weld[key] = id; return id;
        }

        var triCanon = new List<int>();
        var cent      = new List<Vector3>();
        for (int t = 0; t * 3 + 2 < idx.Length; t++) {
            int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
            Vector3 ce = (verts[a] + verts[b] + verts[c]) / 3f;
            float dx = ce.x - pp.x, dz = ce.z - pp.z;
            if (dx * dx + dz * dz > R2) continue;
            triCanon.Add(Canon(verts[a])); triCanon.Add(Canon(verts[b])); triCanon.Add(Canon(verts[c]));
            cent.Add(ce);
        }
        int n = cent.Count;
        if (n == 0) { TerraformPlugin.Log.LogInfo("[navfrag] no navmesh triangles within 25u"); return; }

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        var edge = new Dictionary<long, int>(n * 3);
        for (int i = 0; i < n; i++) {
            int a = triCanon[i * 3], b = triCanon[i * 3 + 1], c = triCanon[i * 3 + 2];
            LinkEdge(edge, parent, a, b, i);
            LinkEdge(edge, parent, b, c, i);
            LinkEdge(edge, parent, c, a, i);
        }

        var compCount = new Dictionary<int, int>();
        var compYmin  = new Dictionary<int, float>();
        var compYmax  = new Dictionary<int, float>();
        for (int i = 0; i < n; i++) {
            int r = Uf_Find(parent, i);
            float y = cent[i].y;
            if (!compCount.ContainsKey(r)) { compCount[r] = 1; compYmin[r] = y; compYmax[r] = y; }
            else { compCount[r]++; compYmin[r] = Mathf.Min(compYmin[r], y); compYmax[r] = Mathf.Max(compYmax[r], y); }
        }

        int pTri = NearestTri(cent, pp);
        int pRoot = pTri >= 0 ? Uf_Find(parent, pTri) : -1;

        Character nearest = null; float bestSq = float.MaxValue;
        var all = Character.GetAllCharacters();
        for (int i = 0; i < all.Count; i++) {
            var ch = all[i];
            if (ch == null || ch == player || ch is Player) continue;
            float dsq = (ch.transform.position - pp).sqrMagnitude;
            if (dsq < bestSq) { bestSq = dsq; nearest = ch; }
        }
        int cRoot = -1;
        if (nearest != null) {
            int cTri = NearestTri(cent, nearest.transform.position);
            cRoot = cTri >= 0 ? Uf_Find(parent, cTri) : -1;
        }

        TerraformPlugin.Log.LogInfo(
            $"[navfrag] within 25u: {n} triangles → {compCount.Count} ISLANDS  (playerIslandTris={(pRoot >= 0 ? compCount[pRoot] : 0)})");
        var roots = new List<int>(compCount.Keys);
        roots.Sort((x, y) => compCount[y].CompareTo(compCount[x]));
        for (int i = 0; i < roots.Count && i < 8; i++) {
            int r = roots[i];
            string tag = r == pRoot ? " [PLAYER]" : (r == cRoot ? " [CREATURE]" : "");
            TerraformPlugin.Log.LogInfo(
                $"[navfrag]   island {i}: {compCount[r]} tris  Y={compYmin[r]:F2}..{compYmax[r]:F2}{tag}");
        }
        if (nearest != null)
            TerraformPlugin.Log.LogInfo(
                $"[navfrag] player & creature same island? {(pRoot >= 0 && pRoot == cRoot ? "YES" : "NO")}  (creature dist={Mathf.Sqrt(bestSq):F1})");
    }

    private static void LinkEdge(Dictionary<long, int> edge, int[] parent, int a, int b, int tri) {
        long key = a < b ? (long)a * 1000000L + b : (long)b * 1000000L + a;
        if (edge.TryGetValue(key, out int other)) Uf_Union(parent, tri, other);
        else edge[key] = tri;
    }

    private static int NearestTri(List<Vector3> cent, Vector3 p) {
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < cent.Count; i++) {
            float d = (cent[i] - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void ProbeNavMeshAtPlayer() {
        var player = Player.m_localPlayer;
        if (player == null) { TerraformPlugin.Log.LogInfo("[navprobe] no player"); return; }
        Vector3 pp = player.transform.position;

        Heightmap.GetHeight(pp, out float terrainY);
        Vector3 origin = pp; origin.y = 6000f;
        float voxelY = VoxelGroundProbe.VoxelTopY(origin);
        TerraformPlugin.Log.LogInfo(
            $"[navprobe] playerY={pp.y:F2} terrainY={terrainY:F2} voxelTopY={voxelY:F2} " +
            $"belowTerrainBy={(terrainY - pp.y):F2}");

        var pf = Pathfinding.instance;
        if (pf == null) { TerraformPlugin.Log.LogInfo("[navprobe] no Pathfinding instance"); return; }

        Vector3 fwd = player.transform.forward; fwd.y = 0f; fwd = fwd.normalized;
        Vector3 target = pp + fwd * 4f;
        bool have = pf.HavePath(pp, target, Pathfinding.AgentType.Humanoid);
        TerraformPlugin.Log.LogInfo($"[navprobe] HavePath(pp→fwd4)={have}");

        Character nearest = null;
        float bestSq = float.MaxValue;
        var all = Character.GetAllCharacters();
        for (int i = 0; i < all.Count; i++) {
            var c = all[i];
            if (c == null || c == player || c is Player) continue;
            float dsq = (c.transform.position - pp).sqrMagnitude;
            if (dsq < bestSq) { bestSq = dsq; nearest = c; }
        }
        if (nearest != null) {
            Vector3 cpos = nearest.transform.position;
            var agent = Pathfinding.AgentType.Humanoid;
            bool alerted = false; int aiPathCount = -1; bool aiLastOk = false; string aiTarget = "?";
            var baseAI = nearest.GetComponent<BaseAI>();
            if (baseAI != null) {
                try {
                    alerted = baseAI.IsAlerted();
                    agent   = (Pathfinding.AgentType)AccessTools.Field(typeof(BaseAI), "m_pathAgentType").GetValue(baseAI);
                    var pth = AccessTools.Field(typeof(BaseAI), "m_path").GetValue(baseAI) as List<Vector3>;
                    aiPathCount = pth != null ? pth.Count : -1;
                    aiLastOk = (bool)AccessTools.Field(typeof(BaseAI), "m_lastFindPathResult").GetValue(baseAI);
                    var mai = nearest.GetComponent<MonsterAI>();
                    if (mai != null) {
                        var tc = AccessTools.Field(typeof(MonsterAI), "m_targetCreature").GetValue(mai) as Character;
                        aiTarget = tc != null ? tc.name : "null";
                    }
                } catch (System.Exception e) { TerraformPlugin.Log.LogWarning($"[navprobe] AI reflect: {e.Message}"); }
            }
            var cpath = new List<Vector3>();
            bool cgp = pf.GetPath(cpos, pp, cpath, agent);
            float lastDist = cpath.Count > 0 ? Vector3.Distance(cpath[cpath.Count - 1], pp) : -1f;
            float startSnap = cpath.Count > 0 ? Vector3.Distance(cpath[0], cpos) : -1f;

            int hid = 0;
            try { hid = (int)AccessTools.Method(typeof(Pathfinding), "GetAgentID").Invoke(pf, new object[] { agent }); } catch { }
            var hf = new UnityEngine.AI.NavMeshQueryFilter { agentTypeID = hid, areaMask = -1 };
            bool covPlayer   = UnityEngine.AI.NavMesh.SamplePosition(pp,   out var cpH, 2f, hf);
            bool covCreature = UnityEngine.AI.NavMesh.SamplePosition(cpos, out var ccH, 2f, hf);

            TerraformPlugin.Log.LogInfo(
                $"[navprobe] creature='{nearest.name}' dist={Mathf.Sqrt(bestSq):F1} agent={agent} grounded={nearest.IsOnGround()} " +
                $"alerted={alerted} target={aiTarget} aiPathCount={aiPathCount} aiLastPathOk={aiLastOk}");
            TerraformPlugin.Log.LogInfo(
                $"[navprobe]   probe GetPath(creature→player)={cgp} corners={cpath.Count} startSnapDist={startSnap:F2} lastCornerDistToPlayer={lastDist:F2}");
            TerraformPlugin.Log.LogInfo(
                $"[navprobe]   navCoverage@player={covPlayer} (dist={(covPlayer ? Vector3.Distance(cpH.position, pp) : -1f):F2})  @creature={covCreature} (dist={(covCreature ? Vector3.Distance(ccH.position, cpos) : -1f):F2})  [agentId={hid}]");

            VoxelGroundProbe.NearestGroundY(pp, out float ghP);
            TerraformPlugin.Log.LogInfo(
                $"[navprobe]   GroundHeight@player={ghP:F2} playerY={pp.y:F2} raised={(pp.y < ghP)}");
            if (cpath.Count > 0)
                TerraformPlugin.Log.LogInfo(
                    $"[navprobe]   playerPos={pp.ToString("F2")} creaturePos={cpos.ToString("F2")} pathFirst={cpath[0].ToString("F2")} pathLast={cpath[cpath.Count - 1].ToString("F2")}");
        } else {
            TerraformPlugin.Log.LogInfo("[navprobe] no nearby creature");
        }

        var tri = NavMesh.CalculateTriangulation();
        var bands = new List<float>();
        var counts = new List<int>();
        int near = 0;
        for (int i = 0; i < tri.vertices.Length; i++) {
            Vector3 v = tri.vertices[i];
            float dx = v.x - pp.x, dz = v.z - pp.z;
            if (dx * dx + dz * dz > 16f) continue;
            near++;
            int b = -1;
            for (int j = 0; j < bands.Count; j++)
                if (Mathf.Abs(bands[j] - v.y) < 1.5f) { b = j; break; }
            if (b < 0) { bands.Add(v.y); counts.Add(1); }
            else { bands[b] = (bands[b] * counts[b] + v.y) / (counts[b] + 1); counts[b]++; }
        }
        TerraformPlugin.Log.LogInfo($"[navprobe] navmesh verts within 4u XZ: {near} → {bands.Count} Y-band(s)");
        for (int j = 0; j < bands.Count; j++)
            TerraformPlugin.Log.LogInfo($"[navprobe]   band Y≈{bands[j]:F2}  ({counts[j]} verts)");
        TerraformPlugin.Log.LogInfo("[navprobe] (poked tiles; if 0 bands, wait ~1s and press again)");
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
                hm.Poke();
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
