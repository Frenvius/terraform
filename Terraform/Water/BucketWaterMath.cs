using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Terraform.Water;

internal static class BucketWaterMath {
    public static float Capacity = 16f;
    public const float DryDepth = 0.001f;
    public const float FilmDepth = 0.2f;
    public const float RunoffDrop = 0.25f;
    public const float RestingDrop = 0.05f;
    public const float FlowRate = 0.15f;
    public const float MaxDrop = 24f;
    private const float SplashDepth = 0.25f;

    public static float ReadAmount(string value) {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || float.IsNaN(amount) || float.IsInfinity(amount)) return 0f;
        return Math.Max(0f, Math.Min(Capacity, amount));
    }

    public static float Transfer(float fromFloor, float fromDepth, float toFloor, float toDepth) {
        float difference = fromFloor + fromDepth - toFloor - toDepth;
        if (!(difference >= 0.001f) || !(fromDepth > 0f)) return 0f;
        float retained = FilmDepth * Math.Max(0f, 1f - Math.Max(0f, fromFloor - toFloor) / RestingDrop);
        return Math.Max(0f, Math.Min(difference * FlowRate, fromDepth - retained));
    }

    public static float Deposit(IList<float> depths, int index, float amount, Func<int, int, bool> canFlow = null,
        IList<float> heights = null) {
        if (!(amount > 0f) || float.IsInfinity(amount)) return 0f;
        Func<int, int, bool> canSplash = heights == null ? canFlow : (from, to) =>
            heights[to] <= heights[index] + RunoffDrop && (canFlow == null || canFlow(from, to));
        var cells = Connected(depths, index, Math.Max(1, (int)Math.Ceiling(amount / SplashDepth)), canSplash, false);
        foreach (int cell in cells) depths[cell] += amount / cells.Count;
        return cells.Count > 0 ? amount : 0f;
    }

    public static float Withdraw(IList<float> depths, int index, float amount, Func<int, int, bool> canFlow = null) {
        if (!(amount > 0f) || float.IsInfinity(amount)) return 0f;
        float remaining = amount;
        var cells = Connected(depths, index, int.MaxValue, canFlow, true);
        for (bool scooped = true; scooped && remaining > 0f;) {
            scooped = false;
            foreach (int cell in cells) {
                float taken = Math.Min(Math.Min(depths[cell], FilmDepth), remaining);
                if (taken <= 0f) continue;
                depths[cell] -= taken;
                remaining -= taken;
                scooped = true;
                if (remaining <= 0f) break;
            }
        }
        return amount - remaining;
    }

    private static List<int> Connected(IList<float> depths, int index, int limit, Func<int, int, bool> canFlow, bool wetOnly) {
        var cells = new List<int>();
        int size = (int)Math.Sqrt(depths.Count);
        if (size < 3 || depths.Count != size * size || index < 0 || index >= depths.Count
            || (wetOnly && depths[index] <= 0f)) return cells;
        var visited = new bool[depths.Count];
        visited[index] = true;
        cells.Add(index);
        for (int i = 0; i < cells.Count; i++) {
            int current = cells[i];
            int x = current % size;
            int z = current / size;
            if (x > 0) Visit(current, current - 1);
            if (x < size - 1) Visit(current, current + 1);
            if (z > 0) Visit(current, current - size);
            if (z < size - 1) Visit(current, current + size);
        }
        return cells;

        void Visit(int from, int neighbor) {
            if (cells.Count >= limit || visited[neighbor] || (wetOnly && depths[neighbor] <= 0f)
                || (canFlow != null && !canFlow(from, neighbor))) return;
            visited[neighbor] = true;
            cells.Add(neighbor);
        }
    }

    public static byte[] Encode(IList<float> depths) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(depths.Count);
        for (int i = 0; i < depths.Count; i++) {
            float depth = depths[i] < 0f && depths[i] > -DryDepth ? 0f : depths[i];
            ValidateDepth(depth);
            writer.Write(depth);
        }
        return stream.ToArray();
    }

    public static float[] Decode(byte[] bytes, int count) {
        if (count <= 0 || bytes == null || bytes.Length != 8L + count * 4L)
            throw new InvalidDataException("Invalid water grid size.");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (reader.ReadInt32() != 1 || reader.ReadInt32() != count)
            throw new InvalidDataException("Unsupported water grid format.");
        var depths = new float[count];
        for (int i = 0; i < count; i++) {
            depths[i] = reader.ReadSingle();
            ValidateDepth(depths[i]);
        }
        return depths;
    }

    private static void ValidateDepth(float depth) {
        if (float.IsNaN(depth) || float.IsInfinity(depth) || depth < 0f || depth > 300f)
            throw new InvalidDataException("Invalid water depth.");
    }

    public static float ProbeHeight(IList<float> heights, IList<float> depths, float originY, float blockedFloor) {
        float level = originY;
        for (int i = 0; i < depths.Count; i++) {
            if (depths[i] > 0f && heights[i] < blockedFloor)
                level = Math.Max(level, originY + heights[i] + depths[i]);
        }
        return level + 0.05f;
    }

    public static float Surface(IList<float> heights, IList<float> depths, int index, int width, float wetDepth = DryDepth) {
        if (depths[index] > wetDepth) return Level(index);
        int size = width + 1;
        int x = index % size;
        int z = index / size;
        float wetHeight = float.PositiveInfinity;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++) {
            int nx = x + dx, nz = z + dz;
            if (nx < 0 || nz < 0 || nx > width || nz > width) continue;
            int neighbor = nz * size + nx;
            if (depths[neighbor] > wetDepth) wetHeight = Math.Min(wetHeight, Level(neighbor));
        }
        return Math.Min(heights[index], wetHeight);

        float Level(int cell) => heights[cell] + depths[cell];
    }
}
