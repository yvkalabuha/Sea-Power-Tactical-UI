// T-178 Kb2 (ADR-0077 §2.2): the pure maths of the compass: angle from a point (north up, clockwise), snapping, normalisation, relative <-> absolute, the 1 degree nudge, presets.
using System;

namespace VoiceBotBridge
{
    internal static class CompassMath
    {
        public const double DeadZoneFrac = 0.2;
        public static readonly double[] PresetsAbs = { 0, 45, 90, 135, 180, 225, 270, 315 };
        public static readonly double[] PresetsRel = { 0, 45, 90, 135, 180, -135, -90, -45 };

        /// <summary>[0, 360); -0 and 360 become 0; NaN / infinity become 0.</summary>
        public static double Norm(double deg)
        {
            if (double.IsNaN(deg) || double.IsInfinity(deg)) return 0;
            double d = deg % 360.0;
            if (d < 0) d += 360.0;
            if (d >= 360.0) d -= 360.0;
            return d == 0 ? 0.0 : d;
        }

        /// <summary>The nearest multiple of `step` (357.6 -> 0, 2.4 -> 0, 2.6 -> 5 at step 5), normalised.</summary>
        public static double Snap(double deg, double step)
        {
            double d = Norm(deg);
            if (!(step > 0)) return d;
            return Norm(Math.Round(d / step) * step);
        }

        /// <summary>The angle of the point (mx, my) seen from the centre: north is up (screen y grows down), clockwise. false in the dead zone (20 % of the radius).</summary>
        public static bool AngleAt(float cx, float cy, float r, float mx, float my, out double deg)
        {
            double dx = mx - cx, dy = my - cy;
            deg = 0;
            if (Math.Sqrt(dx * dx + dy * dy) < DeadZoneFrac * r) return false;
            deg = Norm(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
            return true;
        }

        /// <summary>(-180, 180]: the angle of an absolute bearing seen from the course (positive = to starboard).</summary>
        public static double ToRel(double abs, double course)
        {
            double d = Norm(abs - course);
            return d > 180.0 ? d - 360.0 : d;
        }

        public static double ToAbs(double rel, double course) { return Norm(rel + course); }

        /// <summary>+-1 degree without snapping, through 0 (0 - 1 = 359).</summary>
        public static double Nudge(double deg, int dir) { return Norm(Norm(deg) + (dir < 0 ? -1 : dir > 0 ? 1 : 0)); }

        public static string SelfTest()
        {
            double a;
            // the four quarters (screen y grows down): centre (100, 100), radius 32
            if (!AngleAt(100, 100, 32, 100, 70, out a) || Math.Abs(a - 0) > 0.01) return "north: " + a;
            if (!AngleAt(100, 100, 32, 130, 100, out a) || Math.Abs(a - 90) > 0.01) return "east: " + a;
            if (!AngleAt(100, 100, 32, 100, 130, out a) || Math.Abs(a - 180) > 0.01) return "south: " + a;
            if (!AngleAt(100, 100, 32, 70, 100, out a) || Math.Abs(a - 270) > 0.01) return "west: " + a;
            if (!AngleAt(100, 100, 32, 120, 80, out a) || Math.Abs(a - 45) > 0.01) return "north-east: " + a;
            if (!AngleAt(100, 100, 32, 80, 120, out a) || Math.Abs(a - 225) > 0.01) return "south-west: " + a;
            if (!AngleAt(100, 100, 32, 100 + 500, 100, out a) || Math.Abs(a - 90) > 0.01) return "far outside the disc keeps the angle: " + a;
            // dead zone: 20 % of 32 = 6.4
            if (AngleAt(100, 100, 32, 103, 100, out a)) return "dead zone (3 px)";
            if (!AngleAt(100, 100, 32, 107, 100, out a)) return "outside the dead zone (7 px)";
            // snapping
            if (Snap(357.6, 5) != 0) return "357.6 -> 0: " + Snap(357.6, 5);
            if (Snap(357.0, 5) != 355) return "357 -> 355: " + Snap(357.0, 5);
            if (Snap(2.4, 5) != 0) return "2.4 -> 0";
            if (Snap(2.6, 5) != 5) return "2.6 -> 5";
            if (Snap(359.9, 5) != 0 || Snap(-1, 5) != 0 || Snap(720.2, 5) != 0) return "wrap to 0";
            if (Snap(33, 0) != 33) return "no step keeps the angle";
            // Norm and -0
            if (Norm(-0.0) != 0 || 1.0 / Norm(-0.0) < 0) return "-0 must be +0";
            if (Norm(360) != 0 || Norm(-1) != 359 || Norm(725) != 5 || Norm(-1e-14) != 0) return "Norm";
            if (Norm(double.NaN) != 0) return "Norm NaN";
            // relative / absolute (course 145)
            if (ToAbs(90, 145) != 235) return "ToAbs +90: " + ToAbs(90, 145);
            if (ToAbs(-90, 145) != 55) return "ToAbs -90: " + ToAbs(-90, 145);
            if (ToRel(235, 145) != 90 || ToRel(55, 145) != -90) return "ToRel";
            if (ToRel(145 + 180, 145) != 180 || ToRel(145, 145) != 0) return "ToRel 180 / 0";
            if (ToRel(10, 350) != 20 || ToRel(350, 10) != -20) return "ToRel across north";
            // nudge through 0
            if (Nudge(0, -1) != 359 || Nudge(359, 1) != 0 || Nudge(0, 1) != 1 || Nudge(90, -1) != 89) return "Nudge";
            if (Nudge(0.0, 0) != 0) return "Nudge 0";
            // presets
            if (PresetsAbs.Length != 8 || PresetsRel.Length != 8) return "8 presets";
            for (int i = 0; i < 8; i++) if (PresetsAbs[i] != i * 45) return "abs preset " + i;
            if (PresetsRel[5] != -135 || PresetsRel[7] != -45 || PresetsRel[4] != 180) return "rel presets";
            return null;
        }
    }
}
