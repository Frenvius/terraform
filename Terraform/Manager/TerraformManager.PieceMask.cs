using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float BuildPieceMaskVerticalHalf = 16f;
    private const float BuildPieceMaskHorizPad      = 1f;

    private static int  s_buildPieceMask;
    private static bool s_buildPieceMaskInit;

    private static int BuildPieceMask {
        get {
            if (!s_buildPieceMaskInit) {
                s_buildPieceMask     = LayerMask.GetMask("piece", "static_solid");
                s_buildPieceMaskInit = true;
            }
            return s_buildPieceMask;
        }
    }

    private static readonly Collider[] s_pieceOverlap = new Collider[128];
    private readonly List<Collider> _pieceMaskColliders = new();
    private bool  _pieceMaskActive;
    private float _pieceMaskMargin;

    private void RefreshPieceMask(Vector3 worldPos, float footprintRadius) {
        _pieceMaskColliders.Clear();
        _pieceMaskActive = false;

        if (Configs.ModConfig.RespectBuildPieces == null || !Configs.ModConfig.RespectBuildPieces.Value)
            return;

        _pieceMaskMargin = Configs.ModConfig.BuildPieceMargin != null
            ? Configs.ModConfig.BuildPieceMargin.Value
            : 0.1f;

        float half = footprintRadius + BuildPieceMaskHorizPad;
        var halfExtents = new Vector3(half, BuildPieceMaskVerticalHalf, half);
        int n = Physics.OverlapBoxNonAlloc(
            worldPos, halfExtents, s_pieceOverlap,
            Quaternion.identity, BuildPieceMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < n; i++) {
            var col = s_pieceOverlap[i];
            if (col == null) continue;
            if (col is MeshCollider mc && !mc.convex) continue;
            if (col.GetComponentInParent<Piece>() == null) continue;
            _pieceMaskColliders.Add(col);
        }

        _pieceMaskActive = _pieceMaskColliders.Count > 0;
    }

    private const float PieceCarveBand = 1.5f;

    private bool TryPieceCarveSdf(float worldX, float worldY, float worldZ, out float sdf) {
        sdf = 0f;
        if (!_pieceMaskActive) return false;

        var p = new Vector3(worldX, worldY, worldZ);
        float minDistSq = float.MaxValue;
        for (int i = 0; i < _pieceMaskColliders.Count; i++) {
            var col = _pieceMaskColliders[i];
            if (col == null) continue;
            Vector3 closest = col.ClosestPoint(p);
            float dsq = (closest - p).sqrMagnitude;
            if (dsq <= 1e-6f) { sdf = 1f; return true; }
            if (dsq < minDistSq) minDistSq = dsq;
        }

        float signed = Mathf.Sqrt(minDistSq) - _pieceMaskMargin;
        if (signed > PieceCarveBand) return false;

        sdf = Mathf.Clamp(-signed, -1f, 1f);
        if (sdf == 0f) sdf = 0.001f;
        return true;
    }

    private void SubtractPieces(ChunkRenderData data) {
        if (!_pieceMaskActive) return;

        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);
        int yFloor       = ProtectedYLocalExclusive(chunkOriginY);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < _pieceMaskColliders.Count; i++) {
            var b = _pieceMaskColliders[i].bounds;
            if (b.min.x < minX) minX = b.min.x;
            if (b.min.y < minY) minY = b.min.y;
            if (b.min.z < minZ) minZ = b.min.z;
            if (b.max.x > maxX) maxX = b.max.x;
            if (b.max.y > maxY) maxY = b.max.y;
            if (b.max.z > maxZ) maxZ = b.max.z;
        }

        float pad = _pieceMaskMargin + PieceCarveBand + 1f;
        int wxMin = Mathf.FloorToInt(minX - pad);
        int wxMax = Mathf.CeilToInt (maxX + pad);
        int wyMin = Mathf.FloorToInt(minY - pad);
        int wyMax = Mathf.CeilToInt (maxY + pad);
        int wzMin = Mathf.FloorToInt(minZ - pad);
        int wzMax = Mathf.CeilToInt (maxZ + pad);

        for (int wy = wyMin; wy <= wyMax; wy++)
        for (int wz = wzMin; wz <= wzMax; wz++)
        for (int wx = wxMin; wx <= wxMax; wx++) {
            int vx = wx - chunkOriginX;
            int vy = wy - chunkOriginY;
            int vz = wz - chunkOriginZ;
            if (vx < 0 || vx > VoxelChunk.SIZE || vz < 0 || vz > VoxelChunk.SIZE) continue;
            if (vy < yFloor || vy > VoxelChunk.SIZE) continue;
            if (!TryPieceCarveSdf(wx, wy, wz, out float carveSdf)) continue;

            var v = data.Chunk.Get(vx, vy, vz);
            if (carveSdf > v.Value)
                data.Chunk.Set(vx, vy, vz, new Terraform.Voxel.Core.Voxel { Value = carveSdf });
        }
    }
}
