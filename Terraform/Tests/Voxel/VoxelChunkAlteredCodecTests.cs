using System.IO;
using Terraform.Voxel.Core;
using Xunit;

namespace Terraform.Tests.Voxel;

public class VoxelChunkAlteredCodecTests {
    private static byte[] Encode(VoxelChunk chunk) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) chunk.WriteAltered(writer);
        return stream.ToArray();
    }

    private static VoxelChunk Decode(byte[] bytes) {
        var chunk = new VoxelChunk();
        using var reader = new BinaryReader(new MemoryStream(bytes));
        chunk.ReadAltered(reader);
        return chunk;
    }

    [Fact]
    public void RoundTripRestoresAlteredVoxelsExactly() {
        var source = new VoxelChunk();
        source.Write(0, 0, 0, 0.25f);
        source.Write(1, 2, 3, 1f);
        source.Write(VoxelChunk.SIZE, VoxelChunk.SIZE, VoxelChunk.SIZE, 0.123456f);
        source.Write(40, 10, 63, -0.5f);

        var restored = Decode(Encode(source));

        Assert.Equal(4, restored.CountAltered());
        Assert.Equal(0.25f, restored.Get(0, 0, 0).Value);
        Assert.Equal(1f, restored.Get(1, 2, 3).Value);
        Assert.Equal(0.123456f, restored.Get(VoxelChunk.SIZE, VoxelChunk.SIZE, VoxelChunk.SIZE).Value);
        Assert.Equal(-0.5f, restored.Get(40, 10, 63).Value);
        Assert.True(restored.IsAltered(40, 10, 63));
        Assert.False(restored.IsAltered(2, 2, 3));
    }

    [Fact]
    public void UntouchedChunkEncodesToCountOnly() {
        var restored = Decode(Encode(new VoxelChunk()));
        Assert.Equal(0, restored.CountAltered());
        Assert.Equal(4, Encode(new VoxelChunk()).Length);
    }

    [Fact]
    public void ReencodingRestoredChunkIsByteIdentical() {
        var source = new VoxelChunk();
        for (int x = 10; x < 20; x++)
        for (int y = 30; y < 35; y++)
            source.Write(x, y, 5, 0.75f);

        byte[] first = Encode(source);
        Assert.Equal(first, Encode(Decode(first)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(300000u)]
    [InlineData(uint.MaxValue)]
    public void VarUIntRoundTrips(uint value) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            VoxelChunk.WriteVarUInt(writer, value);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        Assert.Equal(value, VoxelChunk.ReadVarUInt(reader));
    }

    [Fact]
    public void CorruptIndexIsRejected() {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(1);
            VoxelChunk.WriteVarUInt(writer, VoxelChunk.TOTAL);
            writer.Write(0.5f);
        }
        Assert.Throws<InvalidDataException>(() => Decode(stream.ToArray()));
    }
}
