using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Water;

internal struct WaterStreamCell {
    public int X;
    public int Z;
    public float FloorY;
    public int Strength;
}

internal struct WaterStreamFall {
    public int X;
    public int Z;
    public float TopY;
    public float BottomY;
}

internal sealed class WaterStreamSolver {
    public const float CellSize = PlacedWater.Scale;
    public const int MaxStrength = 8;
    private const int DropSearchDepth = 5;
    private const int MaxCells = 4096;
    private const float FlatTolerance = 0.1f;
    private const float StepUp = 0.25f;
    private const float FallThreshold = 0.75f;
    private const float WallProbeHeight = 0.15f;
    private const float MaxFall = 64f;
    private const float LayerMatch = 1f;
    private const float MinDepth = 0.04f;
    private const float MaxDepth = 0.16f;
    private static readonly (int x, int z)[] Directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private enum StepKind { Blocked, Flat, Downhill, Drop }

    private struct Step {
        public int X;
        public int Z;
        public float Floor;
        public StepKind Kind;
    }

    private struct SearchNode {
        public int X;
        public int Z;
        public float Floor;
        public int FirstDirection;
        public int Depth;
    }

    public readonly List<WaterStreamCell> Cells = new();
    public readonly List<WaterStreamFall> Falls = new();
    private readonly Dictionary<long, List<int>> _cellsByColumn = new();
    private readonly HashSet<long> _fallKeys = new();
    private readonly Dictionary<long, float> _floorCache = new();
    private readonly Dictionary<long, Step> _stepCache = new();
    private readonly Queue<int> _open = new();
    private readonly Queue<SearchNode> _search = new();
    private readonly HashSet<long> _searchSeen = new();
    private readonly Step[] _steps = new Step[4];
    private readonly int[] _descentDistance = new int[4];

    public static float Depth(int strength) => MinDepth + (MaxDepth - MinDepth) * strength / MaxStrength;

    public static Vector3 Center(int x, int z, float y) => new((x + 0.5f) * CellSize, y, (z + 0.5f) * CellSize);

    public static int CellIndex(float world) => Mathf.FloorToInt(world / CellSize);

    public void Clear() {
        Cells.Clear();
        Falls.Clear();
        _cellsByColumn.Clear();
        _fallKeys.Clear();
        _floorCache.Clear();
        _stepCache.Clear();
        _open.Clear();
    }

    public void AddSource(Vector3 opening) {
        int x = CellIndex(opening.x);
        int z = CellIndex(opening.z);
        if (!TryProbeFloor(x, z, opening.y + StepUp, out float floor)) return;
        if (opening.y - floor > FallThreshold) AddFall(x, z, opening.y, floor);
        Offer(x, z, floor, MaxStrength);
    }

    public void Solve() {
        while (_open.Count > 0) Expand(_open.Dequeue());
    }

    public int FindCell(int x, int z, float floorY) {
        if (!_cellsByColumn.TryGetValue(Pack(x, z, 0), out var column)) return -1;
        foreach (int index in column) {
            if (Mathf.Abs(Cells[index].FloorY - floorY) < LayerMatch) return index;
        }
        return -1;
    }

    private void Expand(int index) {
        var cell = Cells[index];
        var best = StepKind.Blocked;
        for (int i = 0; i < Directions.Length; i++) {
            _steps[i] = Classify(cell.X, cell.Z, cell.FloorY, i);
            if (_steps[i].Kind > best) best = _steps[i].Kind;
        }
        if (best == StepKind.Blocked) return;

        bool flat = best == StepKind.Flat;
        if (flat && cell.Strength <= 1) return;
        int nearest = flat ? FindNearestDescent() : 0;

        for (int i = 0; i < Directions.Length; i++) {
            var step = _steps[i];
            if (step.Kind != best) continue;
            if (flat && nearest != int.MaxValue && _descentDistance[i] != nearest) continue;
            switch (step.Kind) {
                case StepKind.Drop:
                    AddFall(step.X, step.Z, cell.FloorY + Depth(cell.Strength), step.Floor);
                    Offer(step.X, step.Z, step.Floor, MaxStrength);
                    break;
                case StepKind.Downhill:
                    Offer(step.X, step.Z, step.Floor, cell.Strength);
                    break;
                default:
                    Offer(step.X, step.Z, step.Floor, cell.Strength - 1);
                    break;
            }
        }
    }

    private int FindNearestDescent() {
        _search.Clear();
        _searchSeen.Clear();
        for (int i = 0; i < Directions.Length; i++) {
            _descentDistance[i] = int.MaxValue;
            var step = _steps[i];
            if (step.Kind != StepKind.Flat) continue;
            if (!_searchSeen.Add(Pack(step.X, step.Z, FloorBand(step.Floor)))) continue;
            _search.Enqueue(new SearchNode { X = step.X, Z = step.Z, Floor = step.Floor, FirstDirection = i, Depth = 1 });
        }

        int nearest = int.MaxValue;
        while (_search.Count > 0) {
            var node = _search.Dequeue();
            if (node.Depth > nearest) break;
            for (int d = 0; d < Directions.Length; d++) {
                var next = Classify(node.X, node.Z, node.Floor, d);
                if (next.Kind == StepKind.Downhill || next.Kind == StepKind.Drop) {
                    _descentDistance[node.FirstDirection] = Mathf.Min(_descentDistance[node.FirstDirection], node.Depth);
                    nearest = Mathf.Min(nearest, node.Depth);
                    continue;
                }
                if (next.Kind != StepKind.Flat || node.Depth >= DropSearchDepth) continue;
                if (!_searchSeen.Add(Pack(next.X, next.Z, FloorBand(next.Floor)))) continue;
                _search.Enqueue(new SearchNode {
                    X = next.X, Z = next.Z, Floor = next.Floor, FirstDirection = node.FirstDirection, Depth = node.Depth + 1,
                });
            }
        }
        return nearest;
    }

    private Step Classify(int x, int z, float floor, int direction) {
        long key = Pack(x, z, FloorBand(floor)) * 4 + direction;
        if (_stepCache.TryGetValue(key, out var cached)) return cached;

        var (dx, dz) = Directions[direction];
        var step = new Step { X = x + dx, Z = z + dz, Kind = StepKind.Blocked };
        float wallY = floor + WallProbeHeight;
        bool walled = Physics.Linecast(Center(x, z, wallY), Center(step.X, step.Z, wallY),
            PlacedWater.GroundMask, QueryTriggerInteraction.Ignore);
        if (!walled && TryProbeFloor(step.X, step.Z, floor + StepUp, out float neighborFloor)) {
            step.Floor = neighborFloor;
            float drop = floor - neighborFloor;
            step.Kind = drop > FallThreshold ? StepKind.Drop
                : drop > FlatTolerance ? StepKind.Downhill
                : StepKind.Flat;
        }
        _stepCache[key] = step;
        return step;
    }

    private bool TryProbeFloor(int x, int z, float fromY, out float floor) {
        long key = Pack(x, z, FloorBand(fromY));
        if (!_floorCache.TryGetValue(key, out floor)) {
            floor = Physics.Raycast(Center(x, z, fromY), Vector3.down, out var hit, MaxFall,
                PlacedWater.GroundMask, QueryTriggerInteraction.Ignore) ? hit.point.y : float.NaN;
            _floorCache[key] = floor;
        }
        return !float.IsNaN(floor);
    }

    private void Offer(int x, int z, float floor, int strength) {
        if (strength <= 0) return;
        int index = FindCell(x, z, floor);
        if (index >= 0) {
            var cell = Cells[index];
            if (cell.Strength >= strength) return;
            cell.Strength = strength;
            Cells[index] = cell;
        } else {
            if (Cells.Count >= MaxCells) return;
            index = Cells.Count;
            Cells.Add(new WaterStreamCell { X = x, Z = z, FloorY = floor, Strength = strength });
            long column = Pack(x, z, 0);
            if (!_cellsByColumn.TryGetValue(column, out var list)) _cellsByColumn[column] = list = new List<int>(1);
            list.Add(index);
        }
        _open.Enqueue(index);
    }

    private void AddFall(int x, int z, float topY, float bottomY) {
        if (!_fallKeys.Add(Pack(x, z, FloorBand(bottomY)))) return;
        Falls.Add(new WaterStreamFall { X = x, Z = z, TopY = topY, BottomY = bottomY });
    }

    private static int FloorBand(float y) => Mathf.RoundToInt(y * 8f);

    private static long Pack(int x, int z, int y) =>
        ((long)(x & 0xFFFFF) << 40) | ((long)(z & 0xFFFFF) << 20) | (long)(y & 0xFFFFF);
}
