using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using Terraform.Patches;

namespace Terraform.Persistence;

public static class TerraformPersistence {
    private const int Magic   = 0x4D465254;
    private const int Version = 2;

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

    public static Dictionary<long, List<DigRecord>> Load() {
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
            var result = new Dictionary<long, List<DigRecord>>(chunkCount);
            int totalRecords = 0;

            for (int i = 0; i < chunkCount; i++) {
                long chunkKey = reader.ReadInt64();
                int recordCount = reader.ReadInt32();
                var list = new List<DigRecord>(recordCount);
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
                    list.Add(rec);
                    totalRecords++;
                }
                result[chunkKey] = list;
            }

            TerraformPlugin.Log.LogInfo($"Persistence: loaded {chunkCount} chunks ({totalRecords} digs) from {path}");
            return result;
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"Persistence: load failed at {path}: {ex.Message}");
            return null;
        }
    }

    public static void Save(Dictionary<long, List<DigRecord>> history) {
        if (history == null) return;
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
                writer.Write(history.Count);

                int totalRecords = 0;
                foreach (var kv in history) {
                    writer.Write(kv.Key);
                    var list = kv.Value ?? new List<DigRecord>();
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
                }

                writer.Flush();
                stream.Flush(true);
                TerraformPlugin.Log.LogInfo($"Persistence: wrote {history.Count} chunks ({totalRecords} digs) -> {path}");
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
