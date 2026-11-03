using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>T-350: the flight profile of a missile (climb, cruise, dive): tof = OffsetS + range / CruiseKn. MinNm = climb + dive path: below it the line is an extrapolation.</summary>
    internal struct StrikeTofProfile { public double OffsetS, CruiseKn, MinNm; }

    // T-341 phase C (review of phase B, finding 3): the cache of the simulated time of flight (EstimateShot is expensive: ~50 ms cold) and the sim/rough scale per missile.
    // The execution tick uses only TickTof (rough formula * scale): no game call, no wall clock. Pure.
    internal sealed class StrikeTofCache
    {
        private readonly Dictionary<string, double> _tof = new Dictionary<string, double>();
        private readonly Dictionary<string, List<double>> _ratios = new Dictionary<string, List<double>>();
        private readonly Dictionary<string, List<KeyValuePair<double, double>>> _byRange = new Dictionary<string, List<KeyValuePair<double, double>>>();   // T-342: (range nm, ratio) per missile

        public int Count { get { return _tof.Count; } }

        private static long R(double v) { return (long)Math.Round(v, MidpointRounding.AwayFromZero); }

        private static string Key(string ammo, double rangeNm, double tgtKn, double altFt)
        {
            return (ammo ?? "") + "|" + R(rangeNm / StrikeCalib.TofCacheNm).ToString(CultureInfo.InvariantCulture) + "|" + R(tgtKn / 5).ToString(CultureInfo.InvariantCulture)
                   + "|" + ((long)Math.Floor(altFt / 1000)).ToString(CultureInfo.InvariantCulture);
        }

        public bool TryGet(string ammo, double rangeNm, double tgtKn, double altFt, out double tofS) { return _tof.TryGetValue(Key(ammo, rangeNm, tgtKn, altFt), out tofS); }

        /// <summary>Stores a simulated value and updates the scale of the missile (the median of sim / rough, clamped).</summary>
        public void Put(string ammo, double rangeNm, double tgtKn, double altFt, double tofS, double roughS)
        {
            if (!(tofS > 0)) return;
            _tof[Key(ammo, rangeNm, tgtKn, altFt)] = tofS;
            if (!(roughS > 0)) return;
            List<double> l;
            if (!_ratios.TryGetValue(ammo ?? "", out l)) { l = new List<double>(); _ratios[ammo ?? ""] = l; }
            l.Add(tofS / roughS);
            if (l.Count > 25) l.RemoveAt(0);
            List<KeyValuePair<double, double>> br;
            if (!_byRange.TryGetValue(ammo ?? "", out br)) { br = new List<KeyValuePair<double, double>>(); _byRange[ammo ?? ""] = br; }
            br.Add(new KeyValuePair<double, double>(rangeNm, tofS / roughS));
            if (br.Count > 60) br.RemoveAt(0);
        }

        private static double Median(List<double> l)
        {
            var s = l.OrderBy(x => x).ToList();
            double med = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
            return Math.Max(StrikeCalib.TofScaleMin, Math.Min(StrikeCalib.TofScaleMax, med));
        }

        /// <summary>T-342: the scale of the missile at a range: the median of the ratios of the nearest range band (StrikeCalib.TofBandNm wide) that has data; no band data = the common scale.</summary>
        public double Scale(string ammo, double rangeNm)
        {
            List<KeyValuePair<double, double>> br;
            if (!_byRange.TryGetValue(ammo ?? "", out br) || br.Count == 0) return Scale(ammo);
            long band = (long)Math.Floor(rangeNm / StrikeCalib.TofBandNm), best = long.MaxValue, bestDist = long.MaxValue;
            foreach (var kv in br)
            {
                long b = (long)Math.Floor(kv.Key / StrikeCalib.TofBandNm), d = Math.Abs(b - band);
                if (d < bestDist || (d == bestDist && b < best)) { bestDist = d; best = b; }
            }
            var inBand = new List<double>();
            foreach (var kv in br) if ((long)Math.Floor(kv.Key / StrikeCalib.TofBandNm) == best) inBand.Add(kv.Value);
            return inBand.Count == 0 ? Scale(ammo) : Median(inBand);
        }

        /// <summary>T-342: no ratio within withinNm of this range: a new warm-up at the current range is worth it.</summary>
        public bool NeedRewarm(string ammo, double rangeNm, double withinNm = 10)
        {
            List<KeyValuePair<double, double>> br;
            if (!_byRange.TryGetValue(ammo ?? "", out br)) return true;
            foreach (var kv in br) if (Math.Abs(kv.Key - rangeNm) <= withinNm) return false;
            return true;
        }

        /// <summary>sim / rough of the missile: the median of the stored ratios clamped to [TofScaleMin; TofScaleMax]; 1 without data.</summary>
        public double Scale(string ammo)
        {
            List<double> l;
            if (!_ratios.TryGetValue(ammo ?? "", out l) || l.Count == 0) return 1;
            var s = l.OrderBy(x => x).ToList();
            double med = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
            return Math.Max(StrikeCalib.TofScaleMin, Math.Min(StrikeCalib.TofScaleMax, med));
        }

        /// <summary>The time of flight for the tick: the rough formula times the scale of the missile. No game call.</summary>
        public double TickTof(string ammo, double rangeNm, double vmaxKn) { return TickTof(ammo, rangeNm, vmaxKn, StrikeCalib.TofProfiles); }

        /// <summary>T-350: a missile with a flight profile gets the profile (no cache, no Scale, no TofScaleSup); the others the formula above, unchanged.</summary>
        internal double TickTof(string ammo, double rangeNm, double vmaxKn, Dictionary<string, StrikeTofProfile> profiles)
        {
            double p; bool ex;
            if (TryProfile(profiles, ammo, rangeNm, out p, out ex)) return p;
            return StrikeTotMath.RoughTofS(rangeNm, vmaxKn) * Scale(ammo, rangeNm) * (vmaxKn >= StrikeCalib.SupersonicKn ? StrikeCalib.TofScaleSup : 1.0);
        }

        /// <summary>T-350: tof of the ammo from its flight profile (StrikeCalib.TofProfiles); extrap = the range is below the measured part. false = no profile for the ammo.</summary>
        public static bool TryProfile(string ammo, double rangeNm, out double tofS, out bool extrap) { return TryProfile(StrikeCalib.TofProfiles, ammo, rangeNm, out tofS, out extrap); }

        internal static bool TryProfile(Dictionary<string, StrikeTofProfile> profiles, string ammo, double rangeNm, out double tofS, out bool extrap)
        {
            tofS = 0; extrap = false;
            StrikeTofProfile pr;
            if (profiles == null || ammo == null || !profiles.TryGetValue(ammo, out pr) || !(pr.CruiseKn > 0)) return false;
            return TryProfile(pr, rangeNm, out tofS, out extrap);
        }

        internal static bool TryProfile(StrikeTofProfile pr, double rangeNm, out double tofS, out bool extrap)
        {
            tofS = pr.OffsetS + Math.Max(0, rangeNm) / pr.CruiseKn * 3600.0;
            extrap = rangeNm < pr.MinNm;
            return pr.CruiseKn > 0;
        }

        /// <summary>The missiles with no simulated value yet.</summary>
        public IList<string> NeedWarm(IEnumerable<string> ammos)
        {
            var res = new List<string>();
            if (ammos == null) return res;
            foreach (string a in ammos) if (a != null && !res.Contains(a) && !_ratios.ContainsKey(a)) res.Add(a);
            return res;
        }

        /// <summary>true = the missile has at least one simulated value (the scale is meaningful).</summary>
        public bool HasSim(string ammo) { return _ratios.ContainsKey(ammo ?? ""); }

        public void Clear() { _tof.Clear(); _ratios.Clear(); _byRange.Clear(); }

        public static string SelfTest(string part = "all")
        {
            string e;
            if ((part == "all" || part == "band") && (e = SelfTestBand()) != null) return e;
            if ((part == "all" || part == "sup") && (e = SelfTestSup()) != null) return e;
            if ((part == "all" || part == "prof") && (e = SelfTestProfile()) != null) return e;
            if (part == "band" || part == "sup" || part == "prof") return null;
            return SelfTestBase();
        }

        private static string SelfTestBand()
        {
            var c = new StrikeTofCache();
            // two ranges with different ratios: the band of 60 nm and the band of 150 nm
            c.Put("a", 150, 0, 500, 1.10 * 100, 100); c.Put("a", 155, 0, 500, 1.12 * 100, 100); c.Put("a", 60, 0, 500, 0.90 * 100, 100); c.Put("a", 62, 0, 500, 0.92 * 100, 100);
            if (Math.Abs(c.Scale("a", 60) - 0.91) > 1e-9) return "band 60 " + c.Scale("a", 60);
            if (Math.Abs(c.Scale("a", 150) - 1.11) > 1e-9) return "band 150 " + c.Scale("a", 150);
            if (Math.Abs(c.Scale("a", 70) - 0.91) > 1e-9) return "the nearest band for 70 nm " + c.Scale("a", 70);   // band 3 (60-80) holds the data
            if (Math.Abs(c.Scale("a", 130) - 1.11) > 1e-9) return "the nearest band for 130 nm " + c.Scale("a", 130);
            // no band data (an unknown missile): the common scale = 1 (as v1)
            if (c.Scale("none", 80) != 1) return "unknown ammo";
            // the common median of v1 stays
            if (Math.Abs(c.Scale("a") - (0.92 + 1.10) / 2) > 1e-9) return "common median " + c.Scale("a");
            // NeedRewarm: a ratio within 10 nm of the range = no
            if (c.NeedRewarm("a", 66) || c.NeedRewarm("a", 52) || !c.NeedRewarm("a", 80) || !c.NeedRewarm("a", 100) || !c.NeedRewarm("zz", 60)) return "need rewarm";
            if (c.NeedRewarm("a", 80, 25)) return "need rewarm with a wide window";
            c.Put("a", 80, 0, 500, 1.0 * 100, 100);
            if (c.NeedRewarm("a", 80) || Math.Abs(c.Scale("a", 80) - 1.0) > 1e-9 || Math.Abs(c.Scale("a", 60) - 0.91) > 1e-9) return "after a rewarm";
            c.Clear();
            if (!c.NeedRewarm("a", 60) || c.Scale("a", 60) != 1) return "clear";
            return null;
        }

        private static string SelfTestSup()
        {
            var c = new StrikeTofCache();
            c.Put("sub", 50, 0, 500, 100, 100); c.Put("sup", 50, 0, 500, 100, 100);
            double rough = StrikeTotMath.RoughTofS(50, 540), roughSup = StrikeTotMath.RoughTofS(50, 1100);
            if (Math.Abs(c.TickTof("sub", 50, 540) - rough * c.Scale("sub", 50)) > 1e-9) return "a subsonic missile is not multiplied";
            if (Math.Abs(c.TickTof("sup", 50, 1100) - roughSup * c.Scale("sup", 50) * StrikeCalib.TofScaleSup) > 1e-9) return "a supersonic missile gets TofScaleSup";
            if (Math.Abs(c.TickTof("sup", 50, StrikeCalib.SupersonicKn - 1) - StrikeTotMath.RoughTofS(50, StrikeCalib.SupersonicKn - 1) * c.Scale("sup", 50)) > 1e-9) return "below the limit";
            return null;
        }

        private static string SelfTestProfile()
        {
            double t; bool ex;
            // no profile: false and the tick is the formula as before (Harpoon 540 kn, AS-6 1100 kn)
            if (TryProfile(new Dictionary<string, StrikeTofProfile>(), "wp_harpoon", 100, out t, out ex) || TryProfile(new Dictionary<string, StrikeTofProfile>(), null, 100, out t, out ex)) return "profile: none expected";
            var c = new StrikeTofCache();
            c.Put("wp_harpoon", 50, 0, 500, 100, 100); c.Put("wp_as-6", 50, 0, 500, 100, 100);
            var other = new Dictionary<string, StrikeTofProfile> { { "wp_x", new StrikeTofProfile { OffsetS = 40, CruiseKn = 2400, MinNm = 70 } } };
            if (c.TickTof("wp_harpoon", 50, 540, other) != StrikeTotMath.RoughTofS(50, 540) * c.Scale("wp_harpoon", 50)) return "profile: harpoon tick changed";
            if (c.TickTof("wp_as-6", 50, 1100, other) != StrikeTotMath.RoughTofS(50, 1100) * c.Scale("wp_as-6", 50) * StrikeCalib.TofScaleSup) return "profile: as-6 tick changed";
            // the test profile
            var p = new StrikeTofProfile { OffsetS = 40, CruiseKn = 2400, MinNm = 70 };
            if (!TryProfile(p, 120, out t, out ex) || Math.Abs(t - 220) > 1e-9 || ex) return "profile: 120 nm " + t + " " + ex;
            if (!TryProfile(p, 50, out t, out ex) || !ex || Math.Abs(t - (40 + 75)) > 1e-9) return "profile: 50 nm extrap " + t + " " + ex;
            if (!TryProfile(p, 70, out t, out ex) || ex) return "profile: MinNm itself is measured";
            // the tick with a profile: no Scale, no TofScaleSup, no cache
            var d = new StrikeTofCache();
            d.Put("wp_x", 120, 0, 500, 900, 100);   // a huge scale (clamped) must not matter
            if (Math.Abs(d.TickTof("wp_x", 120, 1100, other) - 220) > 1e-9) return "profile: the tick ignores the scale " + d.TickTof("wp_x", 120, 1100, other);
            if (d.TickTof("wp_x", 120, 1100, new Dictionary<string, StrikeTofProfile>()) == 220) return "profile: the fallback differs";
            // the real table: the public overload gives the table's value for the ammo that has one
            foreach (var kv in StrikeCalib.TofProfiles)
            {
                if (!TryProfile(kv.Key, 100, out t, out ex) || Math.Abs(t - (kv.Value.OffsetS + 100 / kv.Value.CruiseKn * 3600)) > 1e-9) return "profile: table " + kv.Key;
                if (Math.Abs(new StrikeTofCache().TickTof(kv.Key, 100, 1100) - t) > 1e-9) return "profile: the public tick " + kv.Key;
            }
            if (TryProfile("wp_harpoon", 100, out t, out ex)) return "profile: harpoon has none";
            return null;
        }

        private static string SelfTestBase()
        {
            var c = new StrikeTofCache();
            double t;
            if (c.TryGet("h", 59, 0, 500, out t) || c.Scale("h") != 1 || c.Count != 0) return "empty";
            if (Math.Abs(c.TickTof("h", 60, 540) - StrikeTotMath.RoughTofS(60, 540)) > 1e-9) return "tick tof without data";
            c.Put("h", 59, 0, 500, 405, 404);
            if (!c.TryGet("h", 59.3, 2, 700, out t) || t != 405) return "hit";
            if (c.TryGet("h", 61, 2, 700, out t)) return "miss by range";
            if (c.TryGet("h", 59, 20, 700, out t)) return "miss by speed";
            if (c.TryGet("h", 59, 0, 1500, out t)) return "miss by altitude";
            if (c.TryGet("x", 59, 0, 500, out t)) return "miss by ammo";
            if (Math.Abs(c.Scale("h") - 405.0 / 404) > 1e-9) return "scale one";
            c.Put("h", 30, 0, 500, 210, 200); c.Put("h", 40, 0, 500, 262, 250);
            // ratios 1.0025, 1.05, 1.048: the median 1.048
            if (Math.Abs(c.Scale("h") - 262.0 / 250) > 1e-9) return "scale median " + c.Scale("h");
            var d = new StrikeTofCache();
            d.Put("a", 10, 0, 0, 300, 100); if (d.Scale("a") != StrikeCalib.TofScaleMax) return "scale clamp high";
            d.Put("b", 10, 0, 0, 10, 100); if (d.Scale("b") != StrikeCalib.TofScaleMin) return "scale clamp low";
            if (Math.Abs(c.TickTof("h", 60, 540) - StrikeTotMath.RoughTofS(60, 540) * c.Scale("h", 60)) > 1e-9) return "tick tof";
            var need = c.NeedWarm(new[] { "h", "n1", "n1", "n2" });
            if (need.Count != 2 || need[0] != "n1" || need[1] != "n2") return "need warm";
            c.Put("h", 1, 0, 0, 0, 5);   // a zero value is not stored
            if (c.Count != 3) return "zero not stored " + c.Count;
            c.Clear();
            if (c.Count != 0 || c.Scale("h") != 1 || c.HasSim("h")) return "clear";
            if (!d.HasSim("a") || d.HasSim("zz")) return "has sim";
            return null;
        }
    }
}
