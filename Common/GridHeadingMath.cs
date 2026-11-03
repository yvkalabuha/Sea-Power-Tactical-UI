using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-308 (ADR-0120): grid (LCC / Unity yaw) &lt;-&gt; true heading. conv = the grid yaw of true north at the point.
    /// true = Norm360(grid - conv), grid = Norm360(true + conv). Pure: no game, no Unity (shared with VoiceBotBridge and the offline stand).
    /// </summary>
    internal static class GridHeadingMath
    {
        public static double Norm360(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return deg;
            double r = deg % 360.0;
            if (r < 0) r += 360.0;
            return r >= 360.0 ? 0.0 : r;
        }

        /// <summary>(-180, 180].</summary>
        public static double Norm180(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return deg;
            double r = Norm360(deg);
            return r > 180.0 ? r - 360.0 : r;
        }

        /// <summary>trueOfZeroYaw = the game's UnityYawToTrueHeading(0, at); conv = -Norm180(it).</summary>
        public static double ConvFromProbe(double trueOfZeroYaw) { return -Norm180(trueOfZeroYaw); }

        public static double TrueFromGrid(double gridDeg, double convDeg) { return Norm360(gridDeg - convDeg); }

        public static double GridFromTrue(double trueDeg, double convDeg) { return Norm360(trueDeg + convDeg); }

        /// <summary>Rotates a grid-axes horizontal vector (x east-grid, z north-grid) into true axes (x east, z north).</summary>
        public static void RotateGridToTrue(double gx, double gz, double convDeg, out double tx, out double tz)
        {
            double c = convDeg * Math.PI / 180.0, cs = Math.Cos(c), sn = Math.Sin(c);
            tx = gx * cs - gz * sn;
            tz = gz * cs + gx * sn;
        }

        private static double Bearing(double x, double z) { return Norm360(Math.Atan2(x, z) * 180.0 / Math.PI); }

        private static double AngDiff(double a, double b) { return Math.Abs(Norm180(a - b)); }

        /// <summary>"ok" or the first failure.</summary>
        public static string SelfTest()
        {
            double[] convs = { -9, -1.78, 0, 4.45 };
            foreach (double cv in convs)
            {
                for (double h = 0; h < 360; h += 7.5)
                {
                    double g = GridFromTrue(h, cv), back = TrueFromGrid(g, cv);
                    if (AngDiff(back, h) > 1e-9) return "round trip h=" + h + " conv=" + cv + " -> " + back;
                    if (g < 0 || g >= 360) return "grid out of range: " + g;
                    // game formula: UnwrapAngle(WrapAngle(yaw - conv)) with yaw = grid
                    if (AngDiff(Norm360(g - cv), h) > 1e-9) return "game formula h=" + h;
                }
                if (AngDiff(TrueFromGrid(0, cv), Norm360(-cv)) > 1e-9) return "zero yaw conv=" + cv;
                if (AngDiff(ConvFromProbe(TrueFromGrid(0, cv)), cv) > 1e-9) return "ConvFromProbe conv=" + cv + " -> " + ConvFromProbe(TrueFromGrid(0, cv));
                // 0/360 crossing
                if (AngDiff(TrueFromGrid(359.5, cv), Norm360(359.5 - cv)) > 1e-9) return "wrap 359.5";
                if (TrueFromGrid(cv, cv) > 1e-9 && TrueFromGrid(cv, cv) < 360 - 1e-9) return "grid=conv must be true north: " + TrueFromGrid(cv, cv);
                // vectors
                for (double h = 0; h < 360; h += 15)
                {
                    double sp = 12.5, r = h * Math.PI / 180.0;
                    double gx = sp * Math.Sin(r), gz = sp * Math.Cos(r), tx, tz;
                    RotateGridToTrue(gx, gz, cv, out tx, out tz);
                    if (Math.Abs(Math.Sqrt(tx * tx + tz * tz) - sp) > 1e-9) return "speed modulus h=" + h + " conv=" + cv;
                    if (AngDiff(Bearing(tx, tz), TrueFromGrid(Bearing(gx, gz), cv)) > 1e-7) return "vector vs heading h=" + h + " conv=" + cv;
                }
            }
            if (Norm360(-0.0) != 0 || Norm360(360) != 0 || Norm360(-1) != 359) return "Norm360 edge";
            if (Norm180(180) != 180 || Norm180(-180) != 180 || Norm180(190) != -170) return "Norm180 edge";
            return "ok";
        }
    }
}
