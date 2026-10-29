using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private const float UndergroundProbeUp = 0.5f;
    private const float SolidAboveScanStep = 1f;
    private const float AirScanStep = 0.05f;
    private const float BuriedSdfMax = -0.05f;

    private bool TryGetChunkAt(Vector3 worldPos, out ChunkRenderData data) {
        data = null;
        var hm = Heightmap.FindHeightmap(worldPos);
        return hm != null && _chunks.TryGetValue(hm.GetInstanceID(), out data) && data?.MeshObject != null;
    }

    private static bool TryGetLocalCell(ChunkRenderData data, Vector3 worldPos, out int cx, out int cz) {
        Vector3 origin = data.MeshObject.transform.position;
        cx = Mathf.FloorToInt(worldPos.x - origin.x);
        cz = Mathf.FloorToInt(worldPos.z - origin.z);
        return cx >= 0 && cz >= 0 && cx < VoxelChunk.SIZE && cz < VoxelChunk.SIZE;
    }

    public bool OriginalHeight(Vector3 worldPos, out float height) => Heightmap.GetHeight(worldPos, out height);

    public bool IsReplaced(Vector3 worldPos) =>
        TryGetChunkAt(worldPos, out var data)
        && TryGetLocalCell(data, worldPos, out int cx, out int cz)
        && data.CellReplaced[cz * VoxelChunk.SIZE + cx];

    public float SurfaceHeight(Vector3 worldPos, float heightmapY) {
        if (!TryGetChunkAt(worldPos, out var data) || data.Chunk == null) return heightmapY;

        var hm = data.Heightmap;
        var heights = GetHeightsArray(hm);
        int stride = hm.m_width + 1;
        if (heights == null || heights.Count < stride * stride) return heightmapY;

        Vector3 origin = data.MeshObject.transform.position;
        float lx = Mathf.Clamp(worldPos.x - origin.x, 0f, VoxelChunk.SIZE);
        float lz = Mathf.Clamp(worldPos.z - origin.z, 0f, VoxelChunk.SIZE);
        int cx = Mathf.Min((int)lx, VoxelChunk.SIZE - 1);
        int cz = Mathf.Min((int)lz, VoxelChunk.SIZE - 1);
        if (!data.CellReplaced[cz * VoxelChunk.SIZE + cx]) return heightmapY;

        float hmY = hm.transform.position.y;
        float CornerY(int vx, int vz) => ColumnTopY(data, vx, vz, heights[vz * stride + vx] + hmY);
        float fx = lx - cx, fz = lz - cz;
        float y0 = Mathf.Lerp(CornerY(cx, cz), CornerY(cx + 1, cz), fx);
        float y1 = Mathf.Lerp(CornerY(cx, cz + 1), CornerY(cx + 1, cz + 1), fx);
        return Mathf.Lerp(y0, y1, fz);
    }

    public bool IsVoxelAir(Vector3 pos) =>
        TryGetChunkAt(pos, out var data) && data.Chunk != null && TryGetLocalVoxelPos(data, pos, out Vector3 local)
        && SampleSdfTri(data.Chunk, local.x, local.y, local.z) > 0f;

    private static bool TryGetLocalVoxelPos(ChunkRenderData data, Vector3 worldPos, out Vector3 local) {
        local = worldPos - data.MeshObject.transform.position;
        return local.x >= 0f && local.x <= VoxelChunk.SIZE
            && local.y >= 0f && local.y <= VoxelChunk.SIZE
            && local.z >= 0f && local.z <= VoxelChunk.SIZE;
    }

    private static bool HasSolidAbove(ChunkRenderData data, Vector3 local) {
        for (float y = local.y + SolidAboveScanStep; y <= VoxelChunk.SIZE; y += SolidAboveScanStep)
            if (SampleSdfTri(data.Chunk, local.x, y, local.z) < 0f) return true;
        return SampleSdfTri(data.Chunk, local.x, VoxelChunk.SIZE, local.z) < 0f;
    }

    public bool TryFindAirAbove(Vector3 pos, float maxRise, out float airY) {
        airY = pos.y;
        if (!TryGetChunkAt(pos, out var data) || data.Chunk == null) return false;
        if (!TryGetLocalVoxelPos(data, pos, out Vector3 local)) return false;
        if (SampleSdfTri(data.Chunk, local.x, local.y, local.z) > BuriedSdfMax) return false;

        for (float rise = AirScanStep; rise <= maxRise && local.y + rise <= VoxelChunk.SIZE; rise += AirScanStep) {
            if (SampleSdfTri(data.Chunk, local.x, local.y + rise, local.z) <= 0f) continue;
            airY = pos.y + rise;
            return true;
        }
        return false;
    }

    public bool HasVoxelGeometryNear(Vector3 center, float halfSize) {
        foreach (var data in _chunks.Values) {
            if (data?.Renderer == null || !data.Renderer.enabled) continue;
            Bounds b = data.Renderer.bounds;
            if (b.max.x < center.x - halfSize || b.min.x > center.x + halfSize) continue;
            if (b.max.z < center.z - halfSize || b.min.z > center.z + halfSize) continue;
            return true;
        }
        return false;
    }

    public bool IsInCavity(Vector3 pos) =>
        TryGetChunkAt(pos, out var data) && data.Chunk != null && TryGetLocalVoxelPos(data, pos, out Vector3 local)
        && SampleSdfTri(data.Chunk, local.x, local.y, local.z) > 0f
        && HasSolidAbove(data, local);

    public bool IsBelowNativeSurface(Vector3 pos) =>
        TryGetChunkAt(pos, out var data)
        && TryGetLocalCell(data, pos, out int cx, out int cz)
        && !data.CellReplaced[cz * VoxelChunk.SIZE + cx]
        && OriginalHeight(pos, out float h)
        && pos.y < h - UndergroundEnterMarginY;

    public bool IsInDugAir(Vector3 pos) {
        if (!TryGetChunkAt(pos, out var data) || data.Chunk == null) return false;
        if (!TryGetLocalVoxelPos(data, pos, out Vector3 local)) return false;
        if (SampleSdfTri(data.Chunk, local.x, local.y, local.z) <= 0f) return false;
        return (OriginalHeight(pos, out float h) && pos.y < h - UndergroundEnterMarginY) || HasSolidAbove(data, local);
    }

    private bool OpFitsVerticalWindow(Vector3 center, float radius) {
        if (!TryGetChunkAt(center, out var data)) return false;
        float originY = data.MeshObject.transform.position.y;
        return center.y - radius - 1f >= originY && center.y + radius + 1f <= originY + VoxelChunk.SIZE;
    }
}
