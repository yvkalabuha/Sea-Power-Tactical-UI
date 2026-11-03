using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-173 / ADR-0072 (the parts in force): the maths of the attack run: the drop point, the drop altitude, the MAD verification legs,
    // the depth-charge rule. The lanes and the localization maths moved to T-176 (ADR-0076). No game, no Unity. Frame: X north, Y east (NmPt).

    /// <summary>Global (Live) parameters of the attack run and the localization (ADR-0072).</summary>
    internal sealed class AswLocParams
    {
        public double DropNm = 1.0, MadVerifyS = 240, MadPassFt = 100, AttackFirstS = 300, ApproachMaxS = 600, DropTimeoutS = 180,
                      SubSpookNm = 2.0, SpookMarginNm = 0.3, LostSubKn = 5, LostExpandS = 600, LostMaxNm = 12, LostHoldS = 600;
        public bool MadVerify = false;   // T-316 / ADR-0123
        public string DepthCharges = "mad_only", Lanes = "auto";
    }

    internal static class AswLaneMath
    {
        private const double Deg = Math.PI / 180.0;

        /// <summary>
        /// Legs over the datum for the MAD check: one along the course and one across it, each legNm long through the datum, then the same pair
        /// shifted by shiftNm (the datum is an estimate, the MAD hears about 0.3 nm). Course NaN = north. Points in the north / east frame.
        /// </summary>
        public static List<NmPt> MadVerifyLegs(NmPt datum, double courseDeg, double legNm = 1.0, double shiftNm = 0.15)
        {
            double c = double.IsNaN(courseDeg) ? 0 : courseDeg;
            double dn = Math.Cos(c * Deg), de = Math.Sin(c * Deg);       // along the course
            double pn = -de, pe = dn;                                     // across it (to the right)
            double h = legNm / 2;
            var res = new List<NmPt>();
            res.Add(new NmPt(datum.X - h * dn, datum.Y - h * de));
            res.Add(new NmPt(datum.X + h * dn, datum.Y + h * de));
            res.Add(new NmPt(datum.X - h * pn, datum.Y - h * pe));
            res.Add(new NmPt(datum.X + h * pn, datum.Y + h * pe));
            // the shifted pair: the along-course leg moves to the right, the across leg moves ahead
            res.Add(new NmPt(datum.X - h * dn + shiftNm * pn, datum.Y - h * de + shiftNm * pe));
            res.Add(new NmPt(datum.X + h * dn + shiftNm * pn, datum.Y + h * de + shiftNm * pe));
            res.Add(new NmPt(datum.X - h * pn + shiftNm * dn, datum.Y - h * pe + shiftNm * de));
            res.Add(new NmPt(datum.X + h * pn + shiftNm * dn, datum.Y + h * pe + shiftNm * de));
            return res;
        }

        /// <summary>The altitude of the MAD pass: at most 0.8 of the MAD deployment limit (maxDeployFt &lt;= 0 = unknown: the wanted one).</summary>
        public static int MadPassFt(double madPassFt, double maxDeployFt)
        {
            return AswMath.MadAltFt((int)Math.Round(madPassFt), maxDeployFt);
        }

        /// <summary>The distance to the target at which the torpedo is dropped: DropNm inside [Min + 0.1; Max] of the weapon (null = DropNm as it is).</summary>
        public static double DropRangeNm(double dropNm, AswDropSpec w)
        {
            if (w == null) return dropNm;
            double lo = w.MinNm + 0.1;
            double r = Math.Max(dropNm, lo);
            if (w.MaxNm > 0) r = Math.Min(r, w.MaxNm);
            return r;
        }

        /// <summary>The drop altitude, ft: 1.5 x the lowest allowed (at least +50 ft), under 0.95 of the highest; 0 = unknown, the game picks.</summary>
        public static int DropAltFt(AswDropSpec w)
        {
            if (w == null || w.MinAltFt <= 0) return 0;
            double a = Math.Max(1.5 * w.MinAltFt, w.MinAltFt + 50);
            if (w.MaxAltFt > 0) a = Math.Min(a, 0.95 * w.MaxAltFt);
            return (int)Math.Round(a);
        }

        /// <summary>
        /// A depth charge only on a MAD fix: mode "mad_only" (not "off"), no airborne torpedo machine in the group, a MAD detection not older than 60 s and a depth
        /// that is known and within the charge's limit.
        /// </summary>
        public static bool DepthChargeOk(string mode, bool groupHasTorpedoAir, double madAgeS, double depthFt, double maxDepthFt)
        {
            if (mode != "mad_only" || groupHasTorpedoAir) return false;
            if (madAgeS < 0 || madAgeS > 60) return false;
            if (double.IsNaN(depthFt) || depthFt <= 0 || maxDepthFt <= 0) return false;
            return depthFt <= maxDepthFt;
        }

        public static string SelfTest()
        {
            var w = new AswDropSpec { MinNm = 0.5, MaxNm = 5, MinAltFt = 100, MaxAltFt = 1000 };
            if (Math.Abs(DropRangeNm(1.0, w) - 1.0) > 1e-9) return "drop range 1.0";
            if (Math.Abs(DropRangeNm(0.3, w) - 0.6) > 1e-9) return "drop range clamps to min + 0.1";
            if (Math.Abs(DropRangeNm(9, w) - 5) > 1e-9) return "drop range clamps to max";
            if (DropRangeNm(1.0, null) != 1.0) return "drop range null";
            if (DropAltFt(w) != 150) return "drop alt Mk46";
            if (DropAltFt(null) != 0) return "drop alt null";
            if (DropAltFt(new AswDropSpec { MinAltFt = 100, MaxAltFt = 120 }) != 114) return "drop alt capped by 0.95 max";
            if (MadPassFt(100, 100) != 80 || MadPassFt(100, 0) != 100 || MadPassFt(100, 400) != 100) return "mad pass ft";
            var legs = MadVerifyLegs(new NmPt(0, 0), 90, 1.0, 0.15);
            if (legs.Count != 8) return "mad legs: 8 points";
            // course 90 (east): the first leg runs west-east through the datum, the second north-south
            if (Math.Abs(legs[0].Y + 0.5) > 1e-9 || Math.Abs(legs[1].Y - 0.5) > 1e-9 || Math.Abs(legs[0].X) > 1e-9) return "mad legs: along the course";
            if (Math.Abs(Math.Abs(legs[2].X) - 0.5) > 1e-9 || Math.Abs(legs[2].Y) > 1e-9) return "mad legs: across the course";
            double dx = legs[4].X - legs[0].X, dy = legs[4].Y - legs[0].Y;
            if (Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 0.15) > 1e-9) return "mad legs: the shift is 0.15";
            var n = MadVerifyLegs(new NmPt(1, 1), double.NaN);
            if (Math.Abs(n[0].X - 0.5) > 1e-9 || Math.Abs(n[1].X - 1.5) > 1e-9) return "mad legs: no course = north";
            if (DepthChargeOk("mad_only", true, 10, 300, 600)) return "dc: torpedo aircraft in the group";
            if (DepthChargeOk("mad_only", false, 70, 300, 600)) return "dc: old MAD";
            if (DepthChargeOk("mad_only", false, 10, double.NaN, 600)) return "dc: unknown depth";
            if (DepthChargeOk("off", false, 10, 300, 600)) return "dc: off";
            if (DepthChargeOk("mad_only", false, 10, 900, 600)) return "dc: too deep";
            if (!DepthChargeOk("mad_only", false, 10, 300, 600)) return "dc: ok";
            return null;
        }
    }
}
