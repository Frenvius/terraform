using System;
using System.Globalization;
using Terraform.Patches;

namespace Terraform.Digging;

public static class StoneYield {
    public static float Accrue(float balance, DigMode mode, float solidRemoved, float stonePerCubicMeter) {
        switch (mode) {
            case DigMode.Surface:
            case DigMode.UndergroundTunnel:
            case DigMode.OpenHoleDeepen:
                return balance + Math.Max(0f, solidRemoved) * stonePerCubicMeter;
            case DigMode.Level:
            case DigMode.Smooth:
                return balance + Math.Min(0f, solidRemoved) * stonePerCubicMeter;
            default:
                return balance;
        }
    }

    public static int TakeWholeStones(ref float balance) {
        if (balance < 1f) return 0;
        int stones = (int)Math.Floor(balance);
        balance -= stones;
        return stones;
    }

    public static float ReadBalance(string value) {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var balance)
            || float.IsNaN(balance) || float.IsInfinity(balance)) return 0f;
        return balance;
    }

    public static string WriteBalance(float balance) => balance.ToString("R", CultureInfo.InvariantCulture);
}
