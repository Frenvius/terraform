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

    public static void RetireImported() {
        if (!TryResolveWorld(out string path, out _) || !File.Exists(path)) return;
        string retired = path + ".imported";
        try {
            if (File.Exists(retired)) File.Delete(retired);
            File.Move(path, retired);
            TerraformPlugin.Log.LogInfo($"Persistence: sidecar imported into the world save; kept as {retired}");
        } catch (System.Exception ex) {
            TerraformPlugin.Log.LogError($"Persistence: could not retire {path}: {ex.Message}");
        }
    }
}
