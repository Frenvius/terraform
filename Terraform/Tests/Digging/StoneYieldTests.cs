using System.Globalization;
using Terraform.Digging;
using Terraform.Patches;
using Terraform.Voxel.Core;
using Xunit;

namespace Terraform.Tests.Digging;

public class StoneYieldTests {
    private const float Tolerance = 0.00001f;
    private const float Rate = 0.4f;

    [Theory]
    [InlineData(DigMode.Surface)]
    [InlineData(DigMode.UndergroundTunnel)]
    [InlineData(DigMode.OpenHoleDeepen)]
    public void CarvingEarnsStoneForRemovedGround(DigMode mode) {
        Assert.Equal(2f, StoneYield.Accrue(0f, mode, 5f, Rate), Tolerance);
    }

    [Theory]
    [InlineData(DigMode.Level)]
    [InlineData(DigMode.Smooth)]
    public void FillingWithTheHoeCreatesDebt(DigMode mode) {
        Assert.Equal(-1.2f, StoneYield.Accrue(0f, mode, -3f, Rate), Tolerance);
    }

    [Theory]
    [InlineData(DigMode.Level)]
    [InlineData(DigMode.Smooth)]
    public void CuttingWithTheHoeEarnsNothing(DigMode mode) {
        Assert.Equal(0.5f, StoneYield.Accrue(0.5f, mode, 4f, Rate), Tolerance);
    }

    [Fact]
    public void RaiseLeavesTheBalanceAlone() {
        Assert.Equal(0.5f, StoneYield.Accrue(0.5f, DigMode.Raise, -10f, Rate), Tolerance);
    }

    [Fact]
    public void DebtIsRepaidBeforeStoneDrops() {
        float balance = StoneYield.Accrue(-1.2f, DigMode.Surface, 5f, Rate);
        Assert.Equal(0, StoneYield.TakeWholeStones(ref balance));
        Assert.Equal(0.8f, balance, Tolerance);
    }

    [Fact]
    public void WholeStonesAreTakenAndTheFractionKept() {
        float balance = 2.75f;
        Assert.Equal(2, StoneYield.TakeWholeStones(ref balance));
        Assert.Equal(0.75f, balance, Tolerance);
    }

    [Fact]
    public void BalanceRoundTripsWithInvariantCulture() {
        var previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.Equal(-0.35f, StoneYield.ReadBalance(StoneYield.WriteBalance(-0.35f)), Tolerance);
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("broken")]
    public void InvalidBalanceReadsAsZero(string value) {
        Assert.Equal(0f, StoneYield.ReadBalance(value), Tolerance);
    }

    [Fact]
    public void ChunkTracksSolidRemovedByWrites() {
        var chunk = new VoxelChunk();
        chunk.Write(1, 1, 1, 1f);
        chunk.Write(2, 1, 1, 0f);
        Assert.Equal(1.5f, chunk.TakeSolidRemoved(), Tolerance);
        Assert.Equal(0f, chunk.TakeSolidRemoved(), Tolerance);
    }

    [Fact]
    public void ChunkCountsAddedSolidAsNegative() {
        var chunk = new VoxelChunk();
        chunk.Write(1, 1, 1, 1f);
        chunk.TakeSolidRemoved();
        chunk.Write(1, 1, 1, -1f);
        Assert.Equal(-1f, chunk.TakeSolidRemoved(), Tolerance);
    }

    [Fact]
    public void ChunkSkipsCornersSharedWithTheNextChunk() {
        var chunk = new VoxelChunk();
        chunk.Write(VoxelChunk.SIZE, 1, 1, 1f);
        chunk.Write(1, 1, VoxelChunk.SIZE, 1f);
        Assert.Equal(0f, chunk.TakeSolidRemoved(), Tolerance);
    }
}
