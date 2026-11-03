// T-178 Kd2 (ADR-0077 §5): the scale floor of the windows. s_win = max(s, 0.85) for every UiKit window, the Overview and the cards (on 720p the controls were 12 px high).
// The ring has its own geometry and does not use this. The manual [Ui] Scale is 0 (= auto) or 0.85 .. 2.0 in steps of 0.05.
using System;

namespace VoiceBotBridge
{
    internal static class UiScale
    {
        public const double Floor = 0.85, ManualMax = 2.0, ManualStep = 0.05;

        /// <summary>The window scale: never below Floor; a bad value (NaN, infinity, 0, negative) is 1.</summary>
        public static double Windows(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0) return 1.0;
            return Math.Max(s, Floor);
        }

        /// <summary>The manual setting: 0 or a bad value = auto (0); otherwise raised to Floor, capped at ManualMax, snapped to ManualStep.</summary>
        public static double Manual(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return 0;
            if (v > ManualMax) return ManualMax;
            double r = Math.Round(Math.Max(v, Floor) / ManualStep) * ManualStep;
            return Math.Round(Math.Min(ManualMax, Math.Max(Floor, r)), 4);
        }

        public static string SelfTest()
        {
            if (Windows(0.667) != 0.85) return "0.667 -> 0.85";
            if (Windows(1.33) != 1.33 || Windows(1) != 1 || Windows(0.85) != 0.85) return "1.33 stays";
            if (Windows(0) != 1 || Windows(-2) != 1 || Windows(double.NaN) != 1 || Windows(double.PositiveInfinity) != 1) return "bad input -> 1";
            if (Windows(3.5) != 3.5) return "windows has no upper cap";
            if (Manual(0.7) != 0.85) return "manual 0.7 -> 0.85: " + Manual(0.7);
            if (Manual(0) != 0 || Manual(-1) != 0 || Manual(double.NaN) != 0) return "manual 0 = auto";
            if (Manual(2.5) != 2.0 || Manual(2.0) != 2.0) return "manual cap 2";
            if (Math.Abs(Manual(1.02) - 1.0) > 1e-9 || Math.Abs(Manual(1.03) - 1.05) > 1e-9) return "manual snaps to 0.05: " + Manual(1.02) + "," + Manual(1.03);
            if (Math.Abs(Manual(0.85) - 0.85) > 1e-9 || Math.Abs(Manual(1.33) - 1.35) > 1e-9) return "manual 0.85 / 1.33";
            return null;
        }
    }
}
