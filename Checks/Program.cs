using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Terraform.Water;

static void Check(bool condition, string name) {
    if (!condition) throw new Exception(name);
}

static void Close(float actual, float expected, string name) => Check(Math.Abs(actual - expected) < 0.00001f, name);

static void Reject(Action action, string name) {
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception(name);
}

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
Close(BucketWaterMath.ReadAmount("0.25"), 0.25f, "Invariant bucket state");
foreach (string value in new[] { null, "", "NaN", "Infinity", "-1", "broken" })
    Close(BucketWaterMath.ReadAmount(value), 0f, "Invalid bucket state");
Close(BucketWaterMath.ReadAmount("99"), BucketWaterMath.Capacity, "Bucket capacity");

var depths = new float[49];
float bucket = 2f;
bucket -= BucketWaterMath.Deposit(depths, 24, bucket);
Close(bucket, 0f, "A pour empties the bucket");
Close(depths.Sum(), 2f, "A pour conserves water");
Check(depths.Count(d => d > 0f) == 8, "A pour splashes over more cells the more water it carries");
Close(depths.Max(), 0.25f, "A pour does not stack the bucket on one cell");
Close(BucketWaterMath.Deposit(depths, 24, float.NaN), 0f, "NaN cannot enter the grid");
Close(BucketWaterMath.Deposit(depths, -1, 1f), 0f, "Out-of-range pour");
bucket += BucketWaterMath.Withdraw(depths, 24, 2f);
Close(bucket, 2f, "Collect poured water");
Close(depths.Sum(), 0f, "Collected water leaves the ground");
Close(BucketWaterMath.Withdraw(depths, 24, 1f), 0f, "Empty cells cannot create water");
Close(BucketWaterMath.Deposit(depths, 24, 1f, (_, _) => false), 1f, "A walled-in pour still lands");
Close(depths[24], 1f, "A walled-in pour stays inside its walls");

depths = new float[25];
depths[6] = depths[7] = 0.1f;
depths[18] = 0.3f;
Close(BucketWaterMath.Withdraw(depths, 6, 0.5f), 0.2f, "Collect only the connected pool");
Close(depths[18], 0.3f, "Disconnected pool is untouched");
depths[6] = depths[7] = 0.1f;
Close(BucketWaterMath.Withdraw(depths, 6, 0.5f, (_, _) => false), 0.1f, "A wall blocks collection from the next cell");
Close(depths[7], 0.1f, "Water behind a wall is untouched");
for (int i = 0; i < depths.Length; i++) depths[i] = 1f;
Close(BucketWaterMath.Withdraw(depths, 12, 4f), 4f, "Collect from a deep pool");
Check(depths.Min() >= 1f - BucketWaterMath.FilmDepth - 0.00001f, "Collecting lowers the surface instead of emptying columns");
Array.Clear(depths);
depths[4] = depths[5] = 0.1f;
Close(BucketWaterMath.Withdraw(depths, 4, 0.5f), 0.1f, "Rows do not wrap");

Close(BucketWaterMath.Transfer(0f, 1f, 0f, 0f), BucketWaterMath.FlowRate, "A step moves a fraction of the level difference");
Close(BucketWaterMath.Transfer(0f, 0f, 0f, 1f), 0f, "Water never flows uphill");
Close(BucketWaterMath.Transfer(0f, BucketWaterMath.FilmDepth, 0f, 0f), 0f, "A puddle stays put on level ground");
Close(BucketWaterMath.Transfer(0f, BucketWaterMath.FilmDepth + 0.01f, 0f, 0f), 0.01f, "Only water above the puddle depth spreads");
Close(BucketWaterMath.Transfer(BucketWaterMath.RunoffDrop * 0.5f, BucketWaterMath.FilmDepth * 0.5f, 0f, 0f), 0f,
    "A gentle slope keeps a thinner puddle");
Close(BucketWaterMath.Transfer(1f, 0.04f, 0f, 0f), 0.04f, "Water runs off a steep slope completely");

var holeFloors = new float[49];
for (int i = 0; i < holeFloors.Length; i++) holeFloors[i] = i % 7 >= 3 ? 2f : 0f;
var holeDepths = new float[49];
BucketWaterMath.Deposit(holeDepths, 23, 1f, null, holeFloors);
Close(holeDepths.Where((_, i) => i % 7 >= 3).Sum(), 0f, "A pour inside a hole does not splash onto the rim");
Close(holeDepths.Sum(), 1f, "A pour inside a hole keeps all its water");
Close(BucketWaterMath.Transfer(0f, 1f, 2f, 0f), 0f, "A higher floor holds water back");
Close(BucketWaterMath.Transfer(0f, float.NaN, 0f, 0f), 0f, "Invalid depths do not flow");

Array.Clear(depths);
depths[12] = 0.0000123f;
depths[13] = 0.3456789f;
var encoded = BucketWaterMath.Encode(depths);
Check(BucketWaterMath.Decode(encoded, depths.Length).SequenceEqual(depths), "Save preserves shallow water exactly");
Reject(() => BucketWaterMath.Decode(encoded, 24), "Reject grid size mismatch");
Reject(() => BucketWaterMath.Decode(encoded.Take(encoded.Length - 1).ToArray(), 25), "Reject truncated saves");
encoded[0] = 2;
Reject(() => BucketWaterMath.Decode(encoded, 25), "Reject unknown versions");
depths[0] = float.NaN;
Reject(() => BucketWaterMath.Encode(depths), "Reject non-finite saved depths");
depths[0] = -1f;
Reject(() => BucketWaterMath.Encode(depths), "Reject negative saved depths");
depths[0] = -0.0000001f;
Close(BucketWaterMath.Decode(BucketWaterMath.Encode(depths), 25)[0], 0f, "Rounding noise below zero saves as dry");

Array.Clear(depths);
var heights = new float[25];
depths[12] = 0.02f;
heights[11] = 96f;
Close(BucketWaterMath.Surface(heights, depths, 12, 4), 0.02f, "Thin water is drawn at its real level");
Close(BucketWaterMath.Surface(heights, depths, 11, 4), 0.02f, "Dry barriers do not stretch the water mesh upward");
Close(BucketWaterMath.Surface(heights, depths, 7, 4), 0f, "Shore ends at the ground");
depths[12] = 0.4f;
Close(BucketWaterMath.Surface(heights, depths, 12, 4), 0.4f, "Deep water is drawn at its real level");
heights[13] = 5f;
heights[14] = 3f;
depths[14] = 0.2f;
Close(BucketWaterMath.Surface(heights, depths, 13, 4), 0.4f, "A bank between two water levels follows the lower one");

var caveFloors = new[] { -2f, 5f, 96f };
var caveDepths = new[] { 0.5f, 0f, 0.5f };
Close(BucketWaterMath.ProbeHeight(caveFloors, caveDepths, 30f, 96f), 30.05f,
    "Dry roof and blocked columns cannot raise a cave floor probe");
caveDepths[0] = 2.5f;
Close(BucketWaterMath.ProbeHeight(caveFloors, caveDepths, 30f, 96f), 30.55f,
    "Floor probes rise with the actual water surface");
caveFloors[0] = -10f;
Close(BucketWaterMath.ProbeHeight(caveFloors, caveDepths, 30f, 96f), 30.05f,
    "A lowered custom floor stays reachable from the pouring position");

Console.WriteLine("Water checks passed: pours, collection, walls, flow, bucket state, saves, invalid data, shore geometry and cave probes.");
