using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using Terraform.Patches;

namespace Terraform.Persistence;

public class ChunkPersistence {
    public List<DigRecord> Digs = new();
    public float TerrainHeight;
    public byte[] VoxelData;
    public bool VoxelDataIsFull;
}

public static class TerraformPersistence {
    private const int Magic   = 0x4D465254;
    private const int Version = 4;

    private static string ResolveSavePath() {
        var worldField = AccessTools.Field(typeof(ZNet), "m_world");
        if (worldField == null) return null;
        if (worldField.GetValue(null) is not World world) return null;
        if (string.IsNullOrEmpty(world.m_fileName)) return null;

        string localDir = World.GetWorldSavePath(FileHelpers.FileSource.Local);
        if (string.IsNullOrEmpty(localDir)) return null;

        try { Directory.CreateDirectory(localDir); } catch { }
        return Path.Combine(localDir, world.m_fileName + ".terraform.bin");
    }

    public static Dictionary<long, ChunkPersistence> Load() {
        string path = ResolveSavePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);

            int magic = reader.ReadInt32();
            if (magic != Magic) {
                TerraformPlugin.Log.LogWarning($"Persistence: bad magic at {path}");
                return null;
            }
            int version = reader.ReadInt32();
            if (version > Version) {
                TerraformPlugin.Log.LogWarning($"Persistence: future version {version} > {Version}");
                return null;
            }

            int chunkCount = reader.ReadInt32();
            var result = new Dictionary<long, ChunkPersistence>(chunkCount);
            int totalRecords = 0;
            int totalSdf = 0;

            for (int i = 0; i < chunkCount; i++) {
                long chunkKey = reader.ReadInt64();
                var entry = new ChunkPersistence();

                if (version >= 3) {
                    entry.TerrainHeight = reader.ReadSingle();
                }

                int recordCount = reader.ReadInt32();
                entry.Digs = new List<DigRecord>(recordCount);
                for (int j = 0; j < recordCount; j++) {
                    Vector3 worldPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    Vector3 normal = version >= 2
                        ? new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                        : Vector3.up;
                    var rec = new DigRecord {
                        WorldPos = worldPos,
                        Normal   = normal,
                        Radius   = reader.ReadSingle(),
                        Mode     = (DigMode)reader.ReadInt32(),
                    };
                    entry.Digs.Add(rec);
                    totalRecords++;
                }

                if (version >= 3) {
                    int sdfLen = reader.ReadInt32();
                    if (sdfLen > 0) {
                        entry.VoxelData = reader.ReadBytes(sdfLen);
                        entry.VoxelDataIsFull = version >= 4;
                        totalSdf++;
                    }
                }

                result[chunkKey] = entry;
            }

            TerraformPlugin.Log.LogInfo($"Persistence: loaded {chunkCount} chunks ({totalRecords} digs, {totalSdf} with SDF) from {path}");
            return result;
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"Persistence: load failed at {path}: {ex.Message}");
            return null;
        }
    }

    public static void Save(Dictionary<long, ChunkPersistence> data) {
        if (data == null) return;
        string path = ResolveSavePath();
        if (string.IsNullOrEmpty(path)) {
            TerraformPlugin.Log.LogWarning("Persistence: cannot resolve save path; world not ready");
            return;
        }

        string tmpPath = path + ".tmp";
        try {
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream)) {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(data.Count);

                int totalRecords = 0;
                int totalSdf = 0;
                foreach (var kv in data) {
                    writer.Write(kv.Key);
                    var entry = kv.Value ?? new ChunkPersistence();
                    writer.Write(entry.TerrainHeight);

                    var list = entry.Digs ?? new List<DigRecord>();
                    writer.Write(list.Count);
                    foreach (var rec in list) {
                        writer.Write(rec.WorldPos.x);
                        writer.Write(rec.WorldPos.y);
                        writer.Write(rec.WorldPos.z);
                        Vector3 normal = rec.Normal.sqrMagnitude > 0.001f ? rec.Normal.normalized : Vector3.up;
                        writer.Write(normal.x);
                        writer.Write(normal.y);
                        writer.Write(normal.z);
                        writer.Write(rec.Radius);
                        writer.Write((int)rec.Mode);
                        totalRecords++;
                    }

                    if (entry.VoxelData != null && entry.VoxelData.Length > 0) {
                        writer.Write(entry.VoxelData.Length);
                        writer.Write(entry.VoxelData);
                        totalSdf++;
                    } else {
                        writer.Write(0);
                    }
                }

                writer.Flush();
                stream.Flush(true);
                TerraformPlugin.Log.LogInfo($"Persistence: wrote {data.Count} chunks ({totalRecords} digs, {totalSdf} with SDF) -> {path}");
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmpPath, path);
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"Persistence: save failed at {path}: {ex.Message}");
            if (File.Exists(tmpPath)) {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}
