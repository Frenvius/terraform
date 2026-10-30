using System.Globalization;
using System.IO;
using System.Linq;
using Terraform.Water;
using Xunit;

namespace Terraform.Tests.Water;

public class BucketWaterMathTests {
    private const float Tolerance = 0.00001f;
    private const int PourCenter = 24;

    [Fact]
    public void BucketStateIsReadWithInvariantCulture() {
        var previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.Equal(0.25f, BucketWaterMath.ReadAmount("0.25"), Tolerance);
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("broken")]
    public void InvalidBucketStateReadsAsEmpty(string value) {
        Assert.Equal(0f, BucketWaterMath.ReadAmount(value), Tolerance);
    }

    [Fact]
    public void BucketStateIsClampedToCapacity() {
        Assert.Equal(BucketWaterMath.Capacity, BucketWaterMath.ReadAmount("99"), Tolerance);
    }

    [Fact]
    public void PourEmptiesTheBucketAndConservesWater() {
        var depths = new float[49];
        Assert.Equal(2f, BucketWaterMath.Deposit(depths, PourCenter, 2f), Tolerance);
        Assert.Equal(2f, depths.Sum(), Tolerance);
    }

    [Fact]
    public void PourSplashesOverMoreCellsTheMoreWaterItCarries() {
        var depths = new float[49];
        BucketWaterMath.Deposit(depths, PourCenter, 2f);
        Assert.Equal(8, depths.Count(d => d > 0f));
        Assert.Equal(0.25f, depths.Max(), Tolerance);
    }

    [Fact]
    public void NaNCannotEnterTheGrid() {
        Assert.Equal(0f, BucketWaterMath.Deposit(new float[49], PourCenter, float.NaN), Tolerance);
    }

    [Fact]
    public void OutOfRangePourIsIgnored() {
        Assert.Equal(0f, BucketWaterMath.Deposit(new float[49], -1, 1f), Tolerance);
    }

    [Fact]
    public void PouredWaterCanBeCollectedBack() {
        var depths = new float[49];
        BucketWaterMath.Deposit(depths, PourCenter, 2f);
        Assert.Equal(2f, BucketWaterMath.Withdraw(depths, PourCenter, 2f), Tolerance);
        Assert.Equal(0f, depths.Sum(), Tolerance);
    }

    [Fact]
    public void EmptyCellsCannotCreateWater() {
        Assert.Equal(0f, BucketWaterMath.Withdraw(new float[49], PourCenter, 1f), Tolerance);
    }

    [Fact]
    public void WalledInPourLandsInsideItsWalls() {
        var depths = new float[49];
        Assert.Equal(1f, BucketWaterMath.Deposit(depths, PourCenter, 1f, (_, _) => false), Tolerance);
        Assert.Equal(1f, depths[PourCenter], Tolerance);
    }

    [Fact]
    public void CollectionTakesOnlyTheConnectedPool() {
        var depths = new float[25];
        depths[6] = depths[7] = 0.1f;
        depths[18] = 0.3f;
        Assert.Equal(0.2f, BucketWaterMath.Withdraw(depths, 6, 0.5f), Tolerance);
        Assert.Equal(0.3f, depths[18], Tolerance);
    }

    [Fact]
    public void WallBlocksCollectionFromTheNextCell() {
        var depths = new float[25];
        depths[6] = depths[7] = 0.1f;
        Assert.Equal(0.1f, BucketWaterMath.Withdraw(depths, 6, 0.5f, (_, _) => false), Tolerance);
        Assert.Equal(0.1f, depths[7], Tolerance);
    }

    [Fact]
    public void CollectingFromADeepPoolLowersTheSurface() {
        var depths = Enumerable.Repeat(1f, 25).ToArray();
        Assert.Equal(4f, BucketWaterMath.Withdraw(depths, 12, 4f), Tolerance);
        Assert.True(depths.Min() >= 1f - BucketWaterMath.FilmDepth - Tolerance);
    }

    [Fact]
    public void CollectionDoesNotWrapAcrossRows() {
        var depths = new float[25];
        depths[4] = depths[5] = 0.1f;
        Assert.Equal(0.1f, BucketWaterMath.Withdraw(depths, 4, 0.5f), Tolerance);
    }

    [Fact]
    public void StepMovesAFractionOfTheLevelDifference() {
        Assert.Equal(BucketWaterMath.FlowRate, BucketWaterMath.Transfer(0f, 1f, 0f, 0f), Tolerance);
    }

    [Fact]
    public void WaterNeverFlowsUphill() {
        Assert.Equal(0f, BucketWaterMath.Transfer(0f, 0f, 0f, 1f), Tolerance);
    }

    [Fact]
    public void PuddleStaysPutOnLevelGround() {
        Assert.Equal(0f, BucketWaterMath.Transfer(0f, BucketWaterMath.FilmDepth, 0f, 0f), Tolerance);
    }

    [Fact]
    public void OnlyWaterAboveThePuddleDepthSpreads() {
        Assert.Equal(0.01f, BucketWaterMath.Transfer(0f, BucketWaterMath.FilmDepth + 0.01f, 0f, 0f), Tolerance);
    }

    [Fact]
    public void GentleSlopeKeepsAThinnerPuddle() {
        float transfer = BucketWaterMath.Transfer(BucketWaterMath.RunoffDrop * 0.5f, BucketWaterMath.FilmDepth * 0.5f, 0f, 0f);
        Assert.Equal(0f, transfer, Tolerance);
    }

    [Fact]
    public void WaterRunsOffASteepSlopeCompletely() {
        Assert.Equal(0.04f, BucketWaterMath.Transfer(1f, 0.04f, 0f, 0f), Tolerance);
    }

    [Fact]
    public void HigherFloorHoldsWaterBack() {
        Assert.Equal(0f, BucketWaterMath.Transfer(0f, 1f, 2f, 0f), Tolerance);
    }

    [Fact]
    public void InvalidDepthsDoNotFlow() {
        Assert.Equal(0f, BucketWaterMath.Transfer(0f, float.NaN, 0f, 0f), Tolerance);
    }

    [Fact]
    public void PourInsideAHoleKeepsItsWaterOffTheRim() {
        var floors = Enumerable.Range(0, 49).Select(i => i % 7 >= 3 ? 2f : 0f).ToArray();
        var depths = new float[49];
        BucketWaterMath.Deposit(depths, 23, 1f, null, floors);
        Assert.Equal(0f, depths.Where((_, i) => i % 7 >= 3).Sum(), Tolerance);
        Assert.Equal(1f, depths.Sum(), Tolerance);
    }

    [Fact]
    public void SavePreservesShallowWaterExactly() {
        var depths = SavedDepths();
        Assert.Equal(depths, BucketWaterMath.Decode(BucketWaterMath.Encode(depths), depths.Length));
    }

    [Fact]
    public void DecodeRejectsGridSizeMismatch() {
        var encoded = BucketWaterMath.Encode(SavedDepths());
        Assert.ThrowsAny<InvalidDataException>(() => BucketWaterMath.Decode(encoded, 24));
    }

    [Fact]
    public void DecodeRejectsTruncatedSaves() {
        var encoded = BucketWaterMath.Encode(SavedDepths());
        Assert.ThrowsAny<InvalidDataException>(() => BucketWaterMath.Decode(encoded[..^1], 25));
    }

    [Fact]
    public void DecodeRejectsUnknownVersions() {
        var encoded = BucketWaterMath.Encode(SavedDepths());
        encoded[0] = 2;
        Assert.ThrowsAny<InvalidDataException>(() => BucketWaterMath.Decode(encoded, 25));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    public void EncodeRejectsInvalidDepths(float depth) {
        var depths = SavedDepths();
        depths[0] = depth;
        Assert.ThrowsAny<InvalidDataException>(() => BucketWaterMath.Encode(depths));
    }

    [Fact]
    public void RoundingNoiseBelowZeroSavesAsDry() {
        var depths = SavedDepths();
        depths[0] = -0.0000001f;
        Assert.Equal(0f, BucketWaterMath.Decode(BucketWaterMath.Encode(depths), 25)[0], Tolerance);
    }

    [Fact]
    public void ThinWaterIsDrawnAtItsRealLevel() {
        var (heights, depths) = ShoreGrid();
        Assert.Equal(0.02f, BucketWaterMath.Surface(heights, depths, 12, 4), Tolerance);
    }

    [Fact]
    public void DryBarriersDoNotStretchTheWaterMeshUpward() {
        var (heights, depths) = ShoreGrid();
        Assert.Equal(0.02f, BucketWaterMath.Surface(heights, depths, 11, 4), Tolerance);
    }

    [Fact]
    public void ShoreEndsAtTheGround() {
        var (heights, depths) = ShoreGrid();
        Assert.Equal(0f, BucketWaterMath.Surface(heights, depths, 7, 4), Tolerance);
    }

    [Fact]
    public void DeepWaterIsDrawnAtItsRealLevel() {
        var (heights, depths) = ShoreGrid();
        depths[12] = 0.4f;
        Assert.Equal(0.4f, BucketWaterMath.Surface(heights, depths, 12, 4), Tolerance);
    }

    [Fact]
    public void BankBetweenTwoWaterLevelsFollowsTheLowerOne() {
        var (heights, depths) = ShoreGrid();
        depths[12] = 0.4f;
        heights[13] = 5f;
        heights[14] = 3f;
        depths[14] = 0.2f;
        Assert.Equal(0.4f, BucketWaterMath.Surface(heights, depths, 13, 4), Tolerance);
    }

    [Fact]
    public void DryRoofAndBlockedColumnsCannotRaiseACaveFloorProbe() {
        var floors = new[] { -2f, 5f, 96f };
        var depths = new[] { 0.5f, 0f, 0.5f };
        Assert.Equal(30.05f, BucketWaterMath.ProbeHeight(floors, depths, 30f, 96f), Tolerance);
    }

    [Fact]
    public void FloorProbesRiseWithTheActualWaterSurface() {
        var floors = new[] { -2f, 5f, 96f };
        var depths = new[] { 2.5f, 0f, 0.5f };
        Assert.Equal(30.55f, BucketWaterMath.ProbeHeight(floors, depths, 30f, 96f), Tolerance);
    }

    [Fact]
    public void LoweredCustomFloorStaysReachableFromThePouringPosition() {
        var floors = new[] { -10f, 5f, 96f };
        var depths = new[] { 2.5f, 0f, 0.5f };
        Assert.Equal(30.05f, BucketWaterMath.ProbeHeight(floors, depths, 30f, 96f), Tolerance);
    }

    private static float[] SavedDepths() {
        var depths = new float[25];
        depths[12] = 0.0000123f;
        depths[13] = 0.3456789f;
        return depths;
    }

    private static (float[] heights, float[] depths) ShoreGrid() {
        var heights = new float[25];
        var depths = new float[25];
        heights[11] = 96f;
        depths[12] = 0.02f;
        return (heights, depths);
    }
}
