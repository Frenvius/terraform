using System.Collections.Generic;
using UnityEngine;
using Terraform.Voxel.Core;

namespace Terraform.Patches;

public partial class TerraformManager {
    private static float SurfacePunchMaxDelta(float radius) =>
        radius * (1f + ShelfDepthMultiplier) + MinCeilingThickness;

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

    public void DigHeightmap(Vector3 worldPos, float radius, float depth) {
        var hm = Heightmap.FindHeightmap(worldPos);
        if (hm == null) {
            TerraformPlugin.Log.LogWarning("No heightmap at position");
            return;
        }

        var tc = hm.GetAndCreateTerrainCompiler();
        if (tc == null) {
            TerraformPlugin.Log.LogWarning("Could not get TerrainComp");
            return;
        }

        EnsureTerrainCompReflection();
        var levelDelta     = (float[])_levelDeltaField.GetValue(tc);
        var modifiedHeight = (bool[])_modifiedHeightField.GetValue(tc);

        hm.WorldToVertex(worldPos, out int cx, out int cy);
        int r     = Mathf.CeilToInt(radius);
        int width = hm.m_width + 1;
        int count = 0;

        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++) {
            int x = cx + dx, y = cy + dy;
            if (x < 0 || y < 0 || x >= width || y >= width) continue;

            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist > radius) continue;

            float t   = 1f - dist / radius;
            int   idx = y * width + x;

            levelDelta[idx]    -= depth * t;
            levelDelta[idx]     = Mathf.Clamp(levelDelta[idx], -8f, 8f);
            modifiedHeight[idx] = true;
            count++;
        }

        _saveMethod.Invoke(tc, null);
        hm.Poke(false);

        if (ClutterSystem.instance != null)
            ClutterSystem.instance.ResetGrass(worldPos, radius);

        DebugLog($"Dug terrain: {count} verts modified, center=({cx},{cy})");
    }

    public void Dig(Vector3 worldPos, float radius, DigMode mode) {
        Dig(worldPos, radius, mode, Vector3.up);
    }

    public void Dig(Vector3 worldPos, float radius, DigMode mode, Vector3 normal) {
        var record = new DigRecord {
            WorldPos = worldPos, Normal = normal.normalized, Radius = radius, Mode = mode
        };
        var swTotal = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
        int chunkHit = 0;
        long carveTicks = 0, meshTicks = 0;
        foreach (var kv in _chunks) {
            var data = kv.Value;
            if (data == null || data.MeshObject == null || data.Heightmap == null) continue;

            Vector3 origin = data.MeshObject.transform.position;
            int chunkOriginX = Mathf.RoundToInt(origin.x);
            int chunkOriginZ = Mathf.RoundToInt(origin.z);
            if (!ChunkOverlapsDig(chunkOriginX, chunkOriginZ, worldPos, radius)) continue;

            long chunkKey = ChunkKey(chunkOriginX, chunkOriginZ);
            if (!_chunkDigHistory.TryGetValue(chunkKey, out var digs)) {
                digs = new List<DigRecord>();
                _chunkDigHistory[chunkKey] = digs;
            }
            digs.Add(record);

            chunkHit++;
            var swCarve = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
            CarveCrater(data, worldPos, radius, mode, record.Normal);
            if (swCarve != null) carveTicks += swCarve.ElapsedTicks;
            var swMesh = VerboseLogs ? System.Diagnostics.Stopwatch.StartNew() : null;
            RebuildMesh(data);
            if (swMesh != null) meshTicks += swMesh.ElapsedTicks;

            DebugLog(
                $"Dig world=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) " +
                $"mode={mode} terrainH={data.TerrainHeight:F1} " +
                $"history={digs.Count}");
        }

        RebuildAllWaterMeshes();

        if (chunkHit == 0)
            TerraformPlugin.Log.LogWarning(
                $"Dig: no chunk overlapping {worldPos} (total chunks={_chunks.Count})");
        else if (swTotal != null) {
            float toMs(long t) => (float)(t * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            DebugLog($"[perf] Dig total={swTotal.ElapsedMilliseconds}ms chunks={chunkHit} " +
                     $"carve={toMs(carveTicks):F1}ms mesh={toMs(meshTicks):F1}ms");
        }
    }

    private void CarveCrater(ChunkRenderData data, Vector3 worldPos, float radius, DigMode mode, Vector3 normal) {
        if (mode == DigMode.Surface) {
            CarveSurfaceCrater(data, worldPos, radius);
        } else if (mode == DigMode.Raise) {
            CarveRaiseMound(data, worldPos, radius, normal);
        } else {
            CarvePickaxeSphere(data, worldPos);
        }
    }

    private void CarveSurfaceCrater(ChunkRenderData data, Vector3 worldPos, float radius) {
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);

        float outerRadius = radius * OuterRadiusMultiplier;
        float apronRadius = outerRadius + ExtendedPunchBleed;
        float bowlRadius  = radius * BowlRadiusMultiplier;
        float shelfDepth  = radius * ShelfDepthMultiplier;

        int worldCellMinX = Mathf.FloorToInt(worldPos.x - outerRadius);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + outerRadius);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - outerRadius);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + outerRadius);
        var punchedMask = BuildPunchedCellMask(
            worldCellMinX, worldCellMaxX, worldCellMinZ, worldCellMaxZ, worldPos, outerRadius);

        int apronMinX = Mathf.FloorToInt(worldPos.x - apronRadius);
        int apronMaxX = Mathf.FloorToInt(worldPos.x + apronRadius);
        int apronMinZ = Mathf.FloorToInt(worldPos.z - apronRadius);
        int apronMaxZ = Mathf.FloorToInt(worldPos.z + apronRadius);
        AddRenderFootprint(data, apronMinX, apronMaxX + 1, apronMinZ, apronMaxZ + 1);

        SeedHeightmapSdfApron(data, chunkOriginX, chunkOriginY, chunkOriginZ,
            apronMinX, apronMaxX, apronMinZ, apronMaxZ);

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

                float terrainY = SampleHeightmapMeshY(
                    data.Heightmap,
                    new Vector3(worldVx, 0f, worldVz),
                    data.TerrainHeight);

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

                    var v = data.Chunk.Get(vx, yLocal, vz);
                    if (sdf > v.Value)
                        data.Chunk.Set(vx, yLocal, vz, new Terraform.Voxel.Core.Voxel { Value = sdf });
                }
            }
        }

        MergePunchedMaskIntoCarvedCells(data, punchedMask, worldCellMinX, worldCellMinZ);

        string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
        DebugLog(
            $"[carve-surface] hm={hmName} dig=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) r={radius:F1} outerR={outerRadius:F1} " +
            $"chunkOrigin=({chunkOriginX},{chunkOriginY},{chunkOriginZ}) " +
            $"cellRange=[{worldCellMinX}..{worldCellMaxX}]x[{worldCellMinZ}..{worldCellMaxZ}] " +
            $"corners: boundary={boundaryCornersTouched} interior={interiorCornersTouched} outOfChunk={cornersOutOfChunk}");
    }

    private void CarvePickaxeSphere(ChunkRenderData data, Vector3 worldPos) {
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);
        int yFloor       = ProtectedYLocalExclusive(chunkOriginY);

        float maxRadius  = PickaxeRadius + PickaxeNoiseAmplitude;
        float maxRadiusY = maxRadius * PickaxeHeightMultiplier;
        int worldVxMin = Mathf.FloorToInt(worldPos.x - maxRadius)  - 1;
        int worldVxMax = Mathf.CeilToInt (worldPos.x + maxRadius)  + 1;
        int worldVyMin = Mathf.FloorToInt(worldPos.y - maxRadiusY) - 1;
        int worldVyMax = Mathf.CeilToInt (worldPos.y + maxRadiusY) + 1;
        int worldVzMin = Mathf.FloorToInt(worldPos.z - maxRadius)  - 1;
        int worldVzMax = Mathf.CeilToInt (worldPos.z + maxRadius)  + 1;

        int worldCellMinX = Mathf.FloorToInt(worldPos.x - maxRadius);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + maxRadius);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - maxRadius);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + maxRadius);
        AddRenderFootprint(data, worldCellMinX, worldCellMaxX + 1, worldCellMinZ, worldCellMaxZ + 1);

        SeedHeightmapSdfApron(data, chunkOriginX, chunkOriginY, chunkOriginZ,
            worldCellMinX, worldCellMaxX, worldCellMinZ, worldCellMaxZ);

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

            float sdf = effectiveRadius - dist;
            if (sdf < -1f) {
                cornersOutsideSphere++;
                continue;
            }

            sdf = Mathf.Clamp(sdf, -1f, 1f);
            if (sdf == 0f) sdf = 0.001f;

            var v = data.Chunk.Get(vx, vy, vz);
            if (sdf > v.Value) {
                data.Chunk.Set(vx, vy, vz, new Terraform.Voxel.Core.Voxel { Value = sdf });
                writes++;
            }
        }

        string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
        DebugLog(
            $"[carve-pickaxe] hm={hmName} dig=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) r={PickaxeRadius:F2} " +
            $"chunkOrigin=({chunkOriginX},{chunkOriginY},{chunkOriginZ}) " +
            $"writes={writes} skippedOutsideSphere={cornersOutsideSphere} outOfChunk={cornersOutOfChunk}");
    }

    private static void AddRenderFootprint(ChunkRenderData data, int minX, int maxX, int minZ, int maxZ) {
        const int mergePadding = 1;
        var next = new RenderFootprint { MinX = minX, MaxX = maxX, MinZ = minZ, MaxZ = maxZ };

        for (int i = 0; i < data.RenderFootprints.Count; i++) {
            var cur = data.RenderFootprints[i];
            bool overlaps =
                next.MaxX + mergePadding >= cur.MinX && next.MinX - mergePadding <= cur.MaxX &&
                next.MaxZ + mergePadding >= cur.MinZ && next.MinZ - mergePadding <= cur.MaxZ;
            if (!overlaps) continue;

            next.MinX = Mathf.Min(next.MinX, cur.MinX);
            next.MaxX = Mathf.Max(next.MaxX, cur.MaxX);
            next.MinZ = Mathf.Min(next.MinZ, cur.MinZ);
            next.MaxZ = Mathf.Max(next.MaxZ, cur.MaxZ);
            data.RenderFootprints.RemoveAt(i);
            i--;
        }

        data.RenderFootprints.Add(next);
    }

    private static void SeedHeightmapSdfApron(
        ChunkRenderData data,
        int chunkOriginX,
        int chunkOriginY,
        int chunkOriginZ,
        int worldCellMinX,
        int worldCellMaxX,
        int worldCellMinZ,
        int worldCellMaxZ) {
        int minVx = worldCellMinX - 1;
        int maxVx = worldCellMaxX + 2;
        int minVz = worldCellMinZ - 1;
        int maxVz = worldCellMaxZ + 2;

        for (int worldVz = minVz; worldVz <= maxVz; worldVz++)
        for (int worldVx = minVx; worldVx <= maxVx; worldVx++) {
            int vx = worldVx - chunkOriginX;
            int vz = worldVz - chunkOriginZ;
            if (vx < 0 || vx > VoxelChunk.SIZE || vz < 0 || vz > VoxelChunk.SIZE) continue;

            float terrainY = SampleHeightmapMeshY(
                data.Heightmap,
                new Vector3(worldVx, 0f, worldVz),
                data.TerrainHeight);

            int yMinWorld = Mathf.FloorToInt(terrainY) - 2;
            int yMaxWorld = chunkOriginY + VoxelChunk.SIZE;
            int yMinLocal = Mathf.Max(ProtectedYLocalExclusive(chunkOriginY), yMinWorld - chunkOriginY);
            int yMaxLocal = Mathf.Min(VoxelChunk.SIZE, yMaxWorld - chunkOriginY);

            for (int yLocal = yMinLocal; yLocal <= yMaxLocal; yLocal++) {
                int worldY = chunkOriginY + yLocal;
                float sdf = Mathf.Clamp(worldY - terrainY, -1f, 1f);
                if (sdf == 0f) sdf = 0.001f;

                var v = data.Chunk.Get(vx, yLocal, vz);
                bool isAtDefault = v.Value <= -1f + 1e-5f;
                if (sdf > v.Value && isAtDefault)
                    data.Chunk.Set(vx, yLocal, vz, new Terraform.Voxel.Core.Voxel { Value = sdf });
            }
        }
    }

    private void CarveRaiseMound(ChunkRenderData data, Vector3 worldPos, float radius, Vector3 surfaceNormal) {
        Vector3 origin = data.MeshObject.transform.position;
        int chunkOriginX = Mathf.RoundToInt(origin.x);
        int chunkOriginY = Mathf.RoundToInt(origin.y);
        int chunkOriginZ = Mathf.RoundToInt(origin.z);

        float outerRadius = radius * OuterRadiusMultiplier;
        float verticalRadius = Mathf.Max(radius * 1.75f, radius + 0.75f);
        float apronRadius = outerRadius + ExtendedPunchBleed;
        Vector3 normal = surfaceNormal.sqrMagnitude > 0.001f ? surfaceNormal.normalized : Vector3.up;

        int worldCellMinX = Mathf.FloorToInt(worldPos.x - outerRadius);
        int worldCellMaxX = Mathf.FloorToInt(worldPos.x + outerRadius);
        int worldCellMinZ = Mathf.FloorToInt(worldPos.z - outerRadius);
        int worldCellMaxZ = Mathf.FloorToInt(worldPos.z + outerRadius);
        var punchedMask = BuildPunchedCellMask(
            worldCellMinX, worldCellMaxX, worldCellMinZ, worldCellMaxZ, worldPos, outerRadius);

        int apronMinX = Mathf.FloorToInt(worldPos.x - apronRadius);
        int apronMaxX = Mathf.FloorToInt(worldPos.x + apronRadius);
        int apronMinZ = Mathf.FloorToInt(worldPos.z - apronRadius);
        int apronMaxZ = Mathf.FloorToInt(worldPos.z + apronRadius);
        AddRenderFootprint(data, apronMinX, apronMaxX + 1, apronMinZ, apronMaxZ + 1);

        SeedHeightmapSdfApron(data, chunkOriginX, chunkOriginY, chunkOriginZ,
            apronMinX, apronMaxX, apronMinZ, apronMaxZ);

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
                    Mathf.FloorToInt(worldPos.y - verticalRadius) - 1 - chunkOriginY);
                int yMaxLocal = Mathf.Min(VoxelChunk.SIZE,
                    Mathf.CeilToInt(worldPos.y + verticalRadius) + 2 - chunkOriginY);

                for (int yLocal = yMinLocal; yLocal <= yMaxLocal; yLocal++) {
                    int worldY = chunkOriginY + yLocal;
                    Vector3 delta = new Vector3(voxDx, worldY - worldPos.y, voxDz);
                    float axial = Vector3.Dot(delta, normal);
                    float radialSq = Mathf.Max(0f, delta.sqrMagnitude - axial * axial);
                    float radial = Mathf.Sqrt(radialSq);
                    float radialNorm = radial / Mathf.Max(outerRadius, 0.001f);
                    float axialNorm = axial / Mathf.Max(verticalRadius, 0.001f);
                    float density = radialNorm * radialNorm + axialNorm * axialNorm;
                    if (density > 1f) continue;

                    float sdf = Mathf.Clamp(density - 1f + 0.05f, -0.99f, 1f);

                    var v = data.Chunk.Get(vx, yLocal, vz);
                    bool isAtDefault = v.Value <= -1f + 1e-5f;
                    if (sdf < v.Value || (isAtDefault && sdf < 0f))
                        data.Chunk.Set(vx, yLocal, vz, new Terraform.Voxel.Core.Voxel { Value = sdf });
                }
            }
        }

        MergePunchedMaskIntoCarvedCells(data, punchedMask, worldCellMinX, worldCellMinZ);

        string hmName = data.Heightmap != null ? data.Heightmap.gameObject.name : "?";
        DebugLog(
            $"[carve-raise] hm={hmName} pos=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2}) r={radius:F1} outerR={outerRadius:F1} " +
            $"chunkOrigin=({chunkOriginX},{chunkOriginY},{chunkOriginZ}) " +
            $"corners: touched={touchedCorners} outOfChunk={outOfChunk}");
    }
}
