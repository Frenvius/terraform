using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static bool IsCarvingMode(DigMode mode) =>
        mode == DigMode.Surface || mode == DigMode.UndergroundTunnel || mode == DigMode.OpenHoleDeepen;

    private static float RaiseVerticalRadius(float radius) =>
        Mathf.Max(radius * 1.75f, radius + 0.75f);

    private static float CraterRadiusAt(float worldX, float worldZ, float baseRadius) {
        float n = Mathf.PerlinNoise(worldX * NoiseFrequency + NoiseOffset,
                                    worldZ * NoiseFrequency + NoiseOffset);
        return baseRadius * (1f + (n - 0.5f) * 2f * NoiseAmplitude);
    }

    private static float PickaxeNoiseAt(float worldX, float worldY, float worldZ) {
        float nxy = Mathf.PerlinNoise(worldX * PickaxeNoiseFrequency, worldY * PickaxeNoiseFrequency);
        float nyz = Mathf.PerlinNoise(worldY * PickaxeNoiseFrequency, worldZ * PickaxeNoiseFrequency);
        float nzx = Mathf.PerlinNoise(worldZ * PickaxeNoiseFrequency, worldX * PickaxeNoiseFrequency);
        return ((nxy + nyz + nzx) / 3f - 0.5f) * 2f;
    }

    public void Dig(Vector3 worldPos, float radius, DigMode mode) {
        Dig(worldPos, radius, mode, Vector3.up);
    }

    public void Dig(Vector3 worldPos, float radius, DigMode mode, Vector3 normal) {
        Dig(new DigRecord { WorldPos = worldPos, Normal = normal.normalized, Radius = radius, Mode = mode });
    }

    private void Dig(DigRecord record) {
        Vector3 worldPos = record.WorldPos;
        float radius     = record.Radius;
        DigMode mode     = record.Mode;
        var swTotal = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        int chunkHit = 0;
        float solidRemoved = 0f;
        float footprint = Mathf.Max(radius * OuterRadiusMultiplier, DigFootprintRadius(record));
        foreach (var data in _chunks.Values) {
            if (data == null || data.MeshObject == null || data.Heightmap == null) continue;

            Vector3 origin = data.MeshObject.transform.position;
            if (!ChunkOverlapsDig(Mathf.RoundToInt(origin.x), Mathf.RoundToInt(origin.z), worldPos, footprint)) continue;

            chunkHit++;
            solidRemoved += ApplyDigToChunk(data, record);
            if (IsCarvingMode(mode)) PaintGroundOpenedByDig(data, record);
            SubmitDig(data, record);
        }

        if (chunkHit == 0) {
            TerraformPlugin.Log.LogWarning(
                $"Dig: no chunk overlapping {worldPos} (total chunks={_chunks.Count})");
            return;
        }

        FinishDig(record);
        SettleStone(record, solidRemoved);
        if (swTotal != null) DebugLog($"[perf] Dig total={swTotal.ElapsedMilliseconds}ms chunks={chunkHit}");
    }

    private float ApplyDigToChunk(ChunkRenderData data, DigRecord record) {
        if (!_chunkDigHistory.TryGetValue(data.ChunkKey, out var digs)) {
            digs = new List<DigRecord>();
            _chunkDigHistory[data.ChunkKey] = digs;
        }
        digs.Add(record);

        EnsureChunkAllocated(data);
        data.Chunk.TakeSolidRemoved();
        CarveCrater(data, record);
        float solidRemoved = data.Chunk.TakeSolidRemoved();

        RefreshCellOwnership(data, CellRectForDig(data, record));
        RebuildMesh(data);

        if (VerboseLogs) DebugLog(
            $"Dig world=({record.WorldPos.x:F2},{record.WorldPos.y:F2},{record.WorldPos.z:F2}) " +
            $"mode={record.Mode} terrainH={data.TerrainHeight:F1} history={digs.Count}");
        return solidRemoved;
    }

    private void PaintGroundOpenedByDig(ChunkRenderData data, DigRecord record) {
        Vector3 worldPos = record.WorldPos;
        float paintRadius = DigFootprintRadius(record);
        int painted = PaintCellsOpenedByVoxel(
            data,
            Mathf.FloorToInt(worldPos.x - paintRadius), Mathf.FloorToInt(worldPos.x + paintRadius),
            Mathf.FloorToInt(worldPos.z - paintRadius), Mathf.FloorToInt(worldPos.z + paintRadius));
        if (painted == 0) return;
        data.Heightmap.Poke();
        ClutterSystem.instance?.ResetGrass(worldPos, paintRadius * Mathf.Sqrt(2f) + 0.5f);
    }

    private void FinishDig(DigRecord record) {
        RefreshWaterLeaks();
        float reach = record.Radius * OuterRadiusMultiplier + ExtendedPunchBleed;
        MarkNavDirty(record.WorldPos.x - reach, record.WorldPos.z - reach,
                     record.WorldPos.x + reach, record.WorldPos.z + reach);
    }

    private void CarveCrater(ChunkRenderData data, DigRecord dig) {
        EnsureChunkAllocated(data);

        switch (dig.Mode) {
            case DigMode.Surface:
                CarveSurfaceCrater(data, dig.WorldPos, dig.Radius);
                break;
            case DigMode.Raise:
                CarveRaiseMound(data, dig);
                break;
            case DigMode.Smooth:
            case DigMode.Level:
                FlattenTowardPlane(data, dig);
                break;
            case DigMode.OpenHoleDeepen:
                CarvePickaxeSphere(data, dig.WorldPos, OpenHoleFunnelHeight);
                break;
            default:
                CarvePickaxeSphere(data, dig.WorldPos, 0f);
                break;
        }
    }

    private void CarveSurfaceCrater(ChunkRenderData data, Vector3 worldPos, float radius) {
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);

        float outerRadius = radius * OuterRadiusMultiplier;
        float bowlRadius  = radius * BowlRadiusMultiplier;
        float shelfDepth  = radius * ShelfDepthMultiplier;

        int worldCellMinX = Mathf.FloorToInt(worldPos.x - outerRadius);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + outerRadius);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - outerRadius);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + outerRadius);
        var punchedMask = BuildPunchedCellMask(
            worldCellMinX, worldCellMaxX, worldCellMinZ, worldCellMaxZ, worldPos, outerRadius);

        bool IsCurrentCellPunched(int worldX, int worldZ) {
            return IsMaskedCellPunched(punchedMask, worldCellMinX, worldCellMinZ, worldX, worldZ);
        }

        int boundaryCornersTouched = 0, interiorCornersTouched = 0, cornersOutOfChunk = 0;

        for (int worldCellZ = worldCellMinZ; worldCellZ <= worldCellMaxZ; worldCellZ++)
        for (int worldCellX = worldCellMinX; worldCellX <= worldCellMaxX; worldCellX++) {
            if (!IsCurrentCellPunched(worldCellX, worldCellZ)) continue;

            int chunkCellX = worldCellX - chunkOriginX;
            int chunkCellZ = worldCellZ - chunkOriginZ;

            for (int cornerZ = 0; cornerZ <= 1; cornerZ++)
            for (int cornerX = 0; cornerX <= 1; cornerX++) {
                int vx = chunkCellX + cornerX;
                int vz = chunkCellZ + cornerZ;
                if (vx < 0 || vx > VoxelChunk.SIZE || vz < 0 || vz > VoxelChunk.SIZE) { cornersOutOfChunk++; continue; }

                int worldVx = chunkOriginX + vx;
                int worldVz = chunkOriginZ + vz;

                bool allFour =
                    IsCurrentCellPunched(worldVx,     worldVz)     &&
                    IsCurrentCellPunched(worldVx - 1, worldVz)     &&
                    IsCurrentCellPunched(worldVx,     worldVz - 1) &&
                    IsCurrentCellPunched(worldVx - 1, worldVz - 1);
                bool isBoundary = !allFour;
                if (isBoundary) boundaryCornersTouched++;
                else            interiorCornersTouched++;

                float terrainY = ColumnTopY(data, vx, vz, SampleHeightmapMeshY(
                    data.Heightmap,
                    new Vector3(worldVx, 0f, worldVz),
                    data.TerrainHeight));

                float topYWorld = terrainY;

                float voxDx = worldVx - worldPos.x;
                float voxDz = worldVz - worldPos.z;
                float voxHorizDist = Mathf.Sqrt(voxDx * voxDx + voxDz * voxDz);

                float distFromSquareEdge = outerRadius - voxHorizDist;
                float shelfWidth         = outerRadius - bowlRadius + RimFeatherExtra;
                float shelfRamp          = Mathf.Clamp01(distFromSquareEdge / Mathf.Max(shelfWidth, 0.001f));
                float shelfEase          = shelfRamp * shelfRamp * (3f - 2f * shelfRamp);
                float depthFromShelf     = shelfEase * shelfDepth;

                float voxBowlNoisedRadius = CraterRadiusAt(worldVx, worldVz, bowlRadius);
                float depthFromBowl = 0f;
                if (voxHorizDist < voxBowlNoisedRadius) {
                    float t = voxHorizDist / Mathf.Max(voxBowlNoisedRadius, 0.001f);
                    depthFromBowl = radius * BowlDepthMultiplier * (1f - Mathf.Pow(t, BowlFalloffExponent));
                }

                float depthAtXZ       = depthFromShelf + depthFromBowl;
                float bowlFloorYWorld = topYWorld - depthAtXZ;

                int yMinWorld = Mathf.FloorToInt(bowlFloorYWorld) - 1;
                int yMaxWorld = chunkOriginY + VoxelChunk.SIZE;

                int yMinLocal = Mathf.Max(ProtectedYLocalExclusive(chunkOriginY), yMinWorld - chunkOriginY);
                int yMaxLocal = Mathf.Min(VoxelChunk.SIZE, yMaxWorld - chunkOriginY);

                for (int yLocal = yMinLocal; yLocal <= yMaxLocal; yLocal++) {
                    int worldY = chunkOriginY + yLocal;
                    float sdf;
                    if (isBoundary) {
                        sdf = Mathf.Clamp(worldY - topYWorld, -1f, 1f);
                        if (sdf == 0f) sdf = 0.001f;
                    } else if (worldY > topYWorld) {
                        sdf = 1f;
                    } else {
                        sdf = Mathf.Clamp(worldY - bowlFloorYWorld + 0.05f, -1f, 1f);
                    }

                    if (sdf > data.Chunk.Get(vx, yLocal, vz).Value)
                        data.Chunk.Write(vx, yLocal, vz, sdf);
                }
            }
        }

        if (VerboseLogs) {
            string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
            DebugLog(
                $"[carve-surface] hm={hmName} dig=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) r={radius:F1} outerR={outerRadius:F1} " +
                $"chunkOrigin=({chunkOriginX},{chunkOriginY},{chunkOriginZ}) " +
                $"cellRange=[{worldCellMinX}..{worldCellMaxX}]x[{worldCellMinZ}..{worldCellMaxZ}] " +
                $"corners: boundary={boundaryCornersTouched} interior={interiorCornersTouched} outOfChunk={cornersOutOfChunk}");
        }
    }

    private void CarvePickaxeSphere(ChunkRenderData data, Vector3 worldPos, float funnelHeight) {
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);
        int yFloor       = ProtectedYLocalExclusive(chunkOriginY);

        float maxRadius  = PickaxeRadius + PickaxeNoiseAmplitude + funnelHeight * OpenHoleFunnelSlope;
        float maxRadiusY = (PickaxeRadius + PickaxeNoiseAmplitude) * PickaxeHeightMultiplier;
        int worldVxMin = Mathf.FloorToInt(worldPos.x - maxRadius)  - 1;
        int worldVxMax = Mathf.CeilToInt (worldPos.x + maxRadius)  + 1;
        int worldVyMin = Mathf.FloorToInt(worldPos.y - maxRadiusY) - 1;
        int worldVyMax = Mathf.CeilToInt (worldPos.y + Mathf.Max(maxRadiusY, funnelHeight)) + 1;
        int worldVzMin = Mathf.FloorToInt(worldPos.z - maxRadius)  - 1;
        int worldVzMax = Mathf.CeilToInt (worldPos.z + maxRadius)  + 1;

        int writes = 0, cornersOutOfChunk = 0, cornersOutsideSphere = 0;

        for (int worldVz = worldVzMin; worldVz <= worldVzMax; worldVz++)
        for (int worldVy = worldVyMin; worldVy <= worldVyMax; worldVy++)
        for (int worldVx = worldVxMin; worldVx <= worldVxMax; worldVx++) {
            int vx = worldVx - chunkOriginX;
            int vy = worldVy - chunkOriginY;
            int vz = worldVz - chunkOriginZ;
            if (vx < 0 || vx > VoxelChunk.SIZE || vy < yFloor || vy > VoxelChunk.SIZE || vz < 0 || vz > VoxelChunk.SIZE) {
                cornersOutOfChunk++;
                continue;
            }

            float dx = worldVx - worldPos.x;
            float dy = (worldVy - worldPos.y) / PickaxeHeightMultiplier;
            float dz = worldVz - worldPos.z;
            float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);

            float noise = PickaxeNoiseAt(worldVx, worldVy, worldVz);
            float effectiveRadius = PickaxeRadius + noise * PickaxeNoiseAmplitude;

            float sdf = Mathf.Max(effectiveRadius - dist, FunnelSdf(dx, worldVy - worldPos.y, dz, effectiveRadius, funnelHeight));
            if (sdf < -1f) {
                cornersOutsideSphere++;
                continue;
            }

            sdf = Mathf.Clamp(sdf, -1f, 1f);
            if (sdf == 0f) sdf = 0.001f;

            if (sdf > data.Chunk.Get(vx, vy, vz).Value && data.Chunk.Write(vx, vy, vz, sdf))
                writes++;
        }

        if (VerboseLogs) {
            string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
            DebugLog(
                $"[carve-pickaxe] hm={hmName} dig=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) r={PickaxeRadius:F2} " +
                $"chunkOrigin=({chunkOriginX},{chunkOriginY},{chunkOriginZ}) " +
                $"writes={writes} skippedOutsideSphere={cornersOutsideSphere} outOfChunk={cornersOutOfChunk}");
        }
    }

    private static float FunnelSdf(float dx, float rise, float dz, float baseRadius, float funnelHeight) {
        if (rise < 0f || rise > funnelHeight) return -1f;
        float radius = baseRadius + rise * OpenHoleFunnelSlope;
        float horizontal = Mathf.Sqrt(dx * dx + dz * dz);
        return (radius - horizontal) / Mathf.Sqrt(1f + OpenHoleFunnelSlope * OpenHoleFunnelSlope);
    }

    private static float FlattenWeight(DigRecord dig, Vector3 delta) {
        float radius = dig.Radius;
        if (dig.Mode == DigMode.Level && dig.Square) {
            bool inside = Mathf.Abs(delta.x) <= radius && Mathf.Abs(delta.y) <= radius && Mathf.Abs(delta.z) <= radius;
            return inside ? 1f : 0f;
        }
        float dist = delta.magnitude;
        if (dist > radius) return 0f;
        if (dig.Mode == DigMode.Level) return 1f;
        return 1f - Mathf.Pow(Mathf.Clamp01(dist / radius), SmoothPower);
    }

    private void FlattenTowardPlane(ChunkRenderData data, DigRecord dig) {
        Vector3 center = dig.WorldPos;
        Vector3 normal = dig.Normal.sqrMagnitude > 0.001f ? dig.Normal.normalized : Vector3.up;
        float radius   = dig.Radius;
        float maxStep  = dig.Mode == DigMode.Smooth ? SmoothMaxStep : LevelMaxStep;
        Vector3 origin = data.MeshObject.transform.position;
        int originX = Mathf.RoundToInt(origin.x);
        int originY = Mathf.RoundToInt(origin.y);
        int originZ = Mathf.RoundToInt(origin.z);
        int yFloor  = ProtectedYLocalExclusive(originY);

        int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius) - 1 - originX);
        int maxX = Mathf.Min(VoxelChunk.SIZE, Mathf.CeilToInt(center.x + radius) + 1 - originX);
        int minY = Mathf.Max(yFloor, Mathf.FloorToInt(center.y - radius) - 1 - originY);
        int maxY = Mathf.Min(VoxelChunk.SIZE, Mathf.CeilToInt(center.y + radius) + 1 - originY);
        int minZ = Mathf.Max(0, Mathf.FloorToInt(center.z - radius) - 1 - originZ);
        int maxZ = Mathf.Min(VoxelChunk.SIZE, Mathf.CeilToInt(center.z + radius) + 1 - originZ);

        int writes = 0;
        for (int vz = minZ; vz <= maxZ; vz++)
        for (int vy = minY; vy <= maxY; vy++)
        for (int vx = minX; vx <= maxX; vx++) {
            var delta = new Vector3(originX + vx - center.x, originY + vy - center.y, originZ + vz - center.z);
            float weight = FlattenWeight(dig, delta);
            if (weight <= 0f) continue;

            float target  = Mathf.Clamp(Vector3.Dot(delta, normal) + SdfBias, -1f, 1f);
            float current = data.Chunk.Get(vx, vy, vz).Value;
            float step    = Mathf.Clamp((target - current) * weight, -maxStep, maxStep);
            float value   = Mathf.Clamp(current + step, -1f, 1f);
            if (value == 0f) value = SeedSurfaceValue;

            if (data.Chunk.Write(vx, vy, vz, value)) writes++;
        }

        if (VerboseLogs) {
            string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
            DebugLog(
                $"[carve-{dig.Mode}] hm={hmName} pos=({center.x:F2},{center.y:F2},{center.z:F2}) r={radius:F2} " +
                $"normal=({normal.x:F2},{normal.y:F2},{normal.z:F2}) writes={writes}");
        }
    }

    private void CarveRaiseMound(ChunkRenderData data, DigRecord dig) {
        Vector3 worldPos      = dig.WorldPos;
        float radius          = dig.Radius;
        Vector3 surfaceNormal = dig.Normal;
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);

        float outerRadius = radius * OuterRadiusMultiplier;
        float verticalRadius = dig.Height > 0f ? dig.Height : RaiseVerticalRadius(radius);
        float shapeScale     = Mathf.Min(verticalRadius, outerRadius);
        Vector3 normal = surfaceNormal.sqrMagnitude > 0.001f ? surfaceNormal.normalized : Vector3.up;
        float normalYSq = normal.y * normal.y;
        float verticalExtent = Mathf.Sqrt(outerRadius * outerRadius * (1f - normalYSq) +
                                          verticalRadius * verticalRadius * normalYSq) + 1f;

        float EllipsoidMoundDistance(Vector3 delta) {
            float axial = Vector3.Dot(delta, normal);
            float radial = Mathf.Sqrt(Mathf.Max(0f, delta.sqrMagnitude - axial * axial));
            float radialNorm = radial / Mathf.Max(outerRadius, 0.001f);
            float axialNorm = axial / Mathf.Max(verticalRadius, 0.001f);
            float density = radialNorm * radialNorm + axialNorm * axialNorm;
            return MoundShapeDistance(Mathf.Sqrt(density), delta.magnitude, shapeScale);
        }

        int worldCellMinX = Mathf.FloorToInt(worldPos.x - outerRadius);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + outerRadius);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - outerRadius);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + outerRadius);
        var punchedMask = BuildPunchedCellMask(
            worldCellMinX, worldCellMaxX, worldCellMinZ, worldCellMaxZ, worldPos, outerRadius);

        bool IsCurrentCellPunched(int worldX, int worldZ) {
            return IsMaskedCellPunched(punchedMask, worldCellMinX, worldCellMinZ, worldX, worldZ);
        }

        int touchedCorners = 0, outOfChunk = 0;

        for (int worldCellZ = worldCellMinZ; worldCellZ <= worldCellMaxZ; worldCellZ++)
        for (int worldCellX = worldCellMinX; worldCellX <= worldCellMaxX; worldCellX++) {
            if (!IsCurrentCellPunched(worldCellX, worldCellZ)) continue;

            int chunkCellX = worldCellX - chunkOriginX;
            int chunkCellZ = worldCellZ - chunkOriginZ;

            for (int cornerZ = 0; cornerZ <= 1; cornerZ++)
            for (int cornerX = 0; cornerX <= 1; cornerX++) {
                int vx = chunkCellX + cornerX;
                int vz = chunkCellZ + cornerZ;
                if (vx < 0 || vx > VoxelChunk.SIZE || vz < 0 || vz > VoxelChunk.SIZE) { outOfChunk++; continue; }

                touchedCorners++;

                int worldVx = chunkOriginX + vx;
                int worldVz = chunkOriginZ + vz;

                float voxDx = worldVx - worldPos.x;
                float voxDz = worldVz - worldPos.z;
                float voxHorizDist = Mathf.Sqrt(voxDx * voxDx + voxDz * voxDz);

                int yMinLocal = Mathf.Max(ProtectedYLocalExclusive(chunkOriginY),
                    Mathf.FloorToInt(worldPos.y - verticalExtent) - 1 - chunkOriginY);
                int yMaxLocal = Mathf.Min(VoxelChunk.SIZE,
                    Mathf.CeilToInt(worldPos.y + verticalExtent) + 2 - chunkOriginY);

                for (int yLocal = yMinLocal; yLocal <= yMaxLocal; yLocal++) {
                    int worldY = chunkOriginY + yLocal;
                    Vector3 delta = new Vector3(voxDx, worldY - worldPos.y, voxDz);
                    float shapeDistance = EllipsoidMoundDistance(delta);
                    if (shapeDistance >= 1f) continue;

                    float sdf = Mathf.Clamp(shapeDistance + 0.05f, -1f, 1f);

                    if (sdf < data.Chunk.Get(vx, yLocal, vz).Value)
                        data.Chunk.Write(vx, yLocal, vz, sdf);
                }
            }
        }

        if (VerboseLogs) {
            string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
            DebugLog(
                $"[carve-raise] hm={hmName} pos=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) r={radius:F1} outerR={outerRadius:F1} " +
                $"chunkOrigin=({chunkOriginX},{chunkOriginY},{chunkOriginZ}) " +
                $"corners: touched={touchedCorners} outOfChunk={outOfChunk}");
        }
    }

    private static float MoundShapeDistance(float ellipsoidScale, float distanceFromCenter, float insideScale) {
        if (ellipsoidScale < 1f) return (ellipsoidScale - 1f) * insideScale;
        return distanceFromCenter * (1f - 1f / ellipsoidScale);
    }
}
