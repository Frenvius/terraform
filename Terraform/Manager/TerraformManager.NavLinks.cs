using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const int   NavLinkPerimeterSamples = 16;
    private const float NavLinkPerimeterScale   = 1.05f;
    private const float NavLinkMinVerticalDrop  = 1.5f;
    private const float NavLinkWidth            = 1.5f;
    private const float NavLinkCost             = 8f;
    private const float NavLinkProbeUp          = 1.0f;

    private readonly Dictionary<int, List<NavMeshLinkInstance>> _navLinks = new();
    private int  _navLinksAgentId = 0;
    private bool _navLinksAgentIdResolved;

    private int GetNavLinksAgentId() {
        if (_navLinksAgentIdResolved) return _navLinksAgentId;
        try {
            var mi = AccessTools.Method(typeof(Pathfinding), "GetAgentID");
            _navLinksAgentId = (int)mi.Invoke(Pathfinding.instance, new object[] { Pathfinding.AgentType.Humanoid });
        } catch (System.Exception e) {
            TerraformPlugin.Log.LogWarning($"[navlinks] GetAgentID failed: {e.Message}");
            _navLinksAgentId = 0;
        }
        _navLinksAgentIdResolved = true;
        return _navLinksAgentId;
    }

    public void PlaceLinksAtBoundary(Vector3 center, float radius, int ownerChunkId) {
        int agentId = GetNavLinksAgentId();
        if (agentId == 0) return;

        if (!_navLinks.TryGetValue(ownerChunkId, out var list)) {
            list = new List<NavMeshLinkInstance>();
            _navLinks[ownerChunkId] = list;
        }

        float r = radius * NavLinkPerimeterScale;
        int added = 0, skipped = 0;
        for (int i = 0; i < NavLinkPerimeterSamples; i++) {
            float ang = i * (Mathf.PI * 2f / NavLinkPerimeterSamples);
            float sx = center.x + Mathf.Cos(ang) * r;
            float sz = center.z + Mathf.Sin(ang) * r;
            Vector3 samplePos = new Vector3(sx, center.y, sz);

            if (!Heightmap.GetHeight(samplePos, out float terrainY)) { skipped++; continue; }

            Vector3 probeOrigin = new Vector3(sx, terrainY + NavLinkProbeUp, sz);
            if (!VoxelGroundProbe.FindVoxelFloorBelow(probeOrigin, 200f, out var hit)) { skipped++; continue; }

            float floorY = hit.point.y;
            float drop   = terrainY - floorY;
            if (drop < NavLinkMinVerticalDrop) { skipped++; continue; }

            var link = NavMesh.AddLink(new NavMeshLinkData {
                startPosition = new Vector3(sx, terrainY, sz),
                endPosition   = new Vector3(sx, floorY,   sz),
                width         = NavLinkWidth,
                costModifier  = NavLinkCost,
                bidirectional = true,
                agentTypeID   = agentId,
                area          = 2
            });
            if (NavMesh.IsLinkValid(link)) { list.Add(link); added++; }
        }

        if (VerboseLogs) DebugLog($"[navlinks] chunk={ownerChunkId} center=({center.x:F1},{center.z:F1}) r={radius:F1} added={added} skipped={skipped}");
    }

    private void RemoveLinksForChunk(int chunkId) {
        if (!_navLinks.TryGetValue(chunkId, out var list)) return;
        for (int i = 0; i < list.Count; i++) {
            if (NavMesh.IsLinkValid(list[i])) NavMesh.RemoveLink(list[i]);
        }
        list.Clear();
        _navLinks.Remove(chunkId);
    }

    private void PlaceLinksForDig(Vector3 worldPos, float radius) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) return;
        PlaceLinksAtBoundary(worldPos, radius, hm.GetInstanceID());
    }
}
