using System.Collections.Generic;
using System.IO;
using Terraform.Patches;
using Terraform.Voxel.Core;
using UnityEngine;

namespace Terraform.Network;

public sealed class ZoneState {
    private const byte FormatVersion = 1;

    public float TerrainHeight;
    public List<DigRecord> Digs = new();
    public Dictionary<int, byte> UndergroundPaved;
    public byte[] Altered;

    public bool NeedsReplay => Altered == null;

    public static byte[] EncodeAltered(VoxelChunk chunk) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) chunk.WriteAltered(writer);
        return stream.ToArray();
    }

    public void ApplyAltered(VoxelChunk chunk) {
        using var reader = new BinaryReader(new MemoryStream(Altered));
        chunk.ReadAltered(reader);
    }

    public bool SameVoxelsAs(VoxelChunk chunk) =>
        Altered != null && chunk != null && BytesEqual(Altered, EncodeAltered(chunk));

    public bool SamePaintAs(Dictionary<int, byte> paved) {
        int count = UndergroundPaved?.Count ?? 0;
        if (count != (paved?.Count ?? 0)) return false;
        if (count == 0) return true;
        foreach (var point in UndergroundPaved)
            if (!paved.TryGetValue(point.Key, out byte value) || value != point.Value) return false;
        return true;
    }

    public byte[] Encode() {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) {
            writer.Write(FormatVersion);
            writer.Write(TerrainHeight);
            WriteDigs(writer, Digs);
            WritePaved(writer, UndergroundPaved);
            writer.Write(Altered != null);
            if (Altered != null) {
                writer.Write(Altered.Length);
                writer.Write(Altered);
            }
        }
        return Utils.Compress(stream.ToArray());
    }

    public static ZoneState Decode(byte[] compressed) {
        using var reader = new BinaryReader(new MemoryStream(Utils.Decompress(compressed)));
        byte version = reader.ReadByte();
        if (version > FormatVersion) throw new InvalidDataException($"zone state version {version} is newer than {FormatVersion}");

        var state = new ZoneState {
            TerrainHeight    = reader.ReadSingle(),
            Digs             = ReadDigs(reader),
            UndergroundPaved = ReadPaved(reader),
        };
        if (reader.ReadBoolean()) {
            int length = reader.ReadInt32();
            state.Altered = reader.ReadBytes(length);
            if (state.Altered.Length != length) throw new EndOfStreamException();
        }
        return state;
    }

    public static void WriteDig(BinaryWriter writer, DigRecord dig) {
        Vector3 normal = dig.Normal.sqrMagnitude > 0.001f ? dig.Normal.normalized : Vector3.up;
        writer.Write(dig.WorldPos.x);
        writer.Write(dig.WorldPos.y);
        writer.Write(dig.WorldPos.z);
        writer.Write(normal.x);
        writer.Write(normal.y);
        writer.Write(normal.z);
        writer.Write(dig.Radius);
        writer.Write((byte)dig.Mode);
        writer.Write(dig.Square);
        writer.Write(dig.Height);
    }

    public static DigRecord ReadDig(BinaryReader reader) => new() {
        WorldPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        Normal   = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        Radius   = reader.ReadSingle(),
        Mode     = (DigMode)reader.ReadByte(),
        Square   = reader.ReadBoolean(),
        Height   = reader.ReadSingle(),
    };

    private static void WriteDigs(BinaryWriter writer, List<DigRecord> digs) {
        writer.Write(digs?.Count ?? 0);
        if (digs == null) return;
        foreach (var dig in digs) WriteDig(writer, dig);
    }

    private static List<DigRecord> ReadDigs(BinaryReader reader) {
        int count = reader.ReadInt32();
        var digs = new List<DigRecord>(count);
        for (int i = 0; i < count; i++) digs.Add(ReadDig(reader));
        return digs;
    }

    private static void WritePaved(BinaryWriter writer, Dictionary<int, byte> paved) {
        writer.Write(paved?.Count ?? 0);
        if (paved == null) return;
        foreach (var point in paved) {
            writer.Write(point.Key);
            writer.Write(point.Value);
        }
    }

    private static Dictionary<int, byte> ReadPaved(BinaryReader reader) {
        int count = reader.ReadInt32();
        if (count == 0) return null;
        var paved = new Dictionary<int, byte>(count);
        for (int i = 0; i < count; i++) paved[reader.ReadInt32()] = reader.ReadByte();
        return paved;
    }

    private static bool BytesEqual(byte[] a, byte[] b) {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
