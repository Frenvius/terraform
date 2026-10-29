using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;
using Terraform.Patches;

namespace Terraform.Persistence;

public class ChunkPersistence {
    public List<DigRecord> Digs = new();
    public float TerrainHeight;
    public byte[] VoxelData;
    public bool VoxelDataIsFull;
    public byte[] AlteredBits;
    public Dictionary<int, byte> UndergroundPaved;
}

public static class TerraformPersistence {
    private const int Magic   = 0x4D465254;
    private const int Version = 10;
    private const int AlteredBitsVersion = 10;
    private const int FlatUndergroundPaintVersion = 8;
    private const int FirstCompressedVersion = 5;

    private static bool s_newerVersionOnDisk;

    private static bool TryResolveWorld(out string path, out long worldUid) {
        path = null;
        worldUid = 0;
        var worldField = AccessTools.Field(typeof(ZNet), "m_world");
        if (worldField == null) return false;
        if (worldField.GetValue(null) is not World world) return false;
        if (string.IsNullOrEmpty(world.m_worldName)) return false;

        string localDir = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
        if (string.IsNullOrEmpty(localDir)) return false;

        try { Directory.CreateDirectory(localDir); } catch { }
        path = Path.Combine(localDir, world.m_worldName + ".terraform.bin");
        worldUid = world.m_uid;
        return true;
    }

    public static Dictionary<long, ChunkPersistence> Load() {
        s_newerVersionOnDisk = false;
        if (!TryResolveWorld(out string path, out long worldUid) || !File.Exists(path)) return null;

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
                s_newerVersionOnDisk = true;
                TerraformPlugin.Log.LogWarning($"Persistence: future version {version} > {Version}");
                return null;
            }

            if (version < FirstCompressedVersion) return ReadBody(reader, version, path);

            long fileUid = reader.ReadInt64();
            if (fileUid != worldUid) {
                TerraformPlugin.Log.LogWarning($"Persistence: {path} belongs to world uid {fileUid}, current is {worldUid}; ignoring it");
                return null;
            }
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var bodyReader = new BinaryReader(gzip);
            return ReadBody(bodyReader, version, path);
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"Persistence: load failed at {path}: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<long, ChunkPersistence> ReadBody(BinaryReader reader, int version, string path) {
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
                    Square   = version >= 6 && reader.ReadBoolean(),
                    Height   = version >= 7 ? reader.ReadSingle() : 0f,
                };
                entry.Digs.Add(rec);
                totalRecords++;
            }

            if (version >= 3) {
                int sdfLen = reader.ReadInt32();
                if (sdfLen > 0) {
                    entry.VoxelData = reader.ReadBytes(sdfLen);
                    if (entry.VoxelData.Length != sdfLen) throw new EndOfStreamException();
                    entry.VoxelDataIsFull = version >= 4;
                    totalSdf++;
                }
            }

            if (version >= AlteredBitsVersion) {
                int alteredLen = reader.ReadInt32();
                if (alteredLen >= 0) {
                    entry.AlteredBits = reader.ReadBytes(alteredLen);
                    if (entry.AlteredBits.Length != alteredLen) throw new EndOfStreamException();
                }
            }

            if (version == FlatUndergroundPaintVersion) {
                int flatLen = reader.ReadInt32();
                if (reader.ReadBytes(flatLen).Length != flatLen) throw new EndOfStreamException();
            } else if (version > FlatUndergroundPaintVersion) {
                int pavedCount = reader.ReadInt32();
                if (pavedCount > 0) {
                    entry.UndergroundPaved = new Dictionary<int, byte>(pavedCount);
                    for (int p = 0; p < pavedCount; p++)
                        entry.UndergroundPaved[reader.ReadInt32()] = reader.ReadByte();
                }
            }

            result[chunkKey] = entry;
        }

        TerraformPlugin.Log.LogInfo($"Persistence: loaded v{version} {chunkCount} chunks ({totalRecords} digs, {totalSdf} with SDF) from {path}");
        return result;
    }

    public static void Save(Dictionary<long, ChunkPersistence> data) {
        if (data == null) return;
        if (!TryResolveWorld(out string path, out long worldUid)) {
            TerraformPlugin.Log.LogWarning("Persistence: cannot resolve save path; world not ready");
            return;
        }
        if (s_newerVersionOnDisk) {
            TerraformPlugin.Log.LogError($"Persistence: {path} was written by a newer version; not overwriting it");
            return;
        }

        string tmpPath = path + ".tmp";
        try {
            int totalRecords, totalSdf;
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write)) {
                using (var header = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
                    header.Write(Magic);
                    header.Write(Version);
                    header.Write(worldUid);
                }
                using (var gzip = new GZipStream(stream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                using (var writer = new BinaryWriter(gzip)) {
                    WriteBody(writer, data, out totalRecords, out totalSdf);
                }
                stream.Flush(true);
            }

            if (File.Exists(path)) File.Replace(tmpPath, path, path + ".bak");
            else File.Move(tmpPath, path);
            TerraformPlugin.Log.LogInfo($"Persistence: wrote {data.Count} chunks ({totalRecords} digs, {totalSdf} with SDF) -> {path}");
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"Persistence: save failed at {path}: {ex.Message}");
            if (File.Exists(tmpPath)) {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    private static void WriteBody(BinaryWriter writer, Dictionary<long, ChunkPersistence> data,
                                  out int totalRecords, out int totalSdf) {
        totalRecords = 0;
        totalSdf = 0;
        writer.Write(data.Count);
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
                writer.Write(rec.Square);
                writer.Write(rec.Height);
                totalRecords++;
            }

            if (entry.VoxelData != null && entry.VoxelData.Length > 0) {
                writer.Write(entry.VoxelData.Length);
                writer.Write(entry.VoxelData);
                totalSdf++;
            } else {
                writer.Write(0);
            }

            if (entry.AlteredBits != null) {
                writer.Write(entry.AlteredBits.Length);
                writer.Write(entry.AlteredBits);
            } else {
                writer.Write(-1);
            }

            var paved = entry.UndergroundPaved;
            writer.Write(paved?.Count ?? 0);
            if (paved != null) {
                foreach (var point in paved) {
                    writer.Write(point.Key);
                    writer.Write(point.Value);
                }
            }
        }
    }
}
