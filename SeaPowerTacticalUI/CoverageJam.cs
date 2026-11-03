using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>One label candidate per jammed unit (worker result; picked on the main thread when drawing). T-182.</summary>
    internal sealed class JamMark
    {
        public int OwnerUid;
        public double Lat, Lon, RangeM;      // position of the label on the outline; RangeM = effective range of the furthest tier (0 = bearing-only label)
        public string State;                 // known | partial | unknown
        public bool Approx;                  // T-225: the cut is set by a source with an estimated (ESM bearings) range: label "≈ N"
        public double BearingDeg;
        public string Text;
    }

    /// <summary>Worker result of one radar: cut profiles (same order as the input), dashed old outline, effective range per tier (+inf = not cut). T-182.</summary>
    internal sealed class JamSpecResult
    {
        public RayProfile[] Profiles;
        public List<GeoSeg> Dashed = new List<GeoSeg>();
        public double[] RangeM;
        public bool Cut;
    }

    /// <summary>
    /// T-182 (ADR-0081): pure jamming model of the radar coverage zone (no Unity, no game access). The burn-through radius of the game
    /// (ECM.CombinedJamUnit without self-screening) is cut out of every tier profile; the player's knowledge rule and the states
    /// none/known/partial/unknown are decided here. Checked offline by SelfTest() and against tools/bench/radar-jamming/burnthrough.py.
    /// </summary>
    internal static class CoverageJamMath
    {
        public const string StateNone = "none", StateKnown = "known", StatePartial = "partial", StateUnknown = "unknown";
        public const double J2SDefault = 30.0, BearingTolDeg = 15.0, DistTriggerFrac = 0.10;
        /// <summary>T-190 (Q2): the latch holds a signal for SignalGapFactor x the longest recent pause between the game's fills of _jamBearings, within [Min, Max].</summary>
        public const double SignalHoldMinS = 6.0, SignalHoldMaxS = 30.0, SignalGapFactor = 2.5, SignalGapMinS = 0.5;

        public static double LatchHoldS(double maxRecentGapS)
        {
            if (double.IsNaN(maxRecentGapS) || maxRecentGapS <= 0) return SignalHoldMinS;
            double h = SignalGapFactor * maxRecentGapS;
            return h < SignalHoldMinS ? SignalHoldMinS : h > SignalHoldMaxS ? SignalHoldMaxS : h;
        }
        public const int JamLabelsMax = 6;
        public const byte StyleJamDashed = 3;
        private const double Rad = Math.PI / 180.0;

        /// <summary>Q1 switch (customer 2026-10-23: variant a): "mark" (no cut, bearing + "&lt;=") | "hide" (variant b: drop the zone of an unknown-jammed radar).</summary>
        public static string UnknownPolicy = "mark";

        // ---------------------------------------------------------------- burn-through

        private static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        /// <summary>Band match (the jammer lists the radar's frequency) and effectiveness above the radar's resistance.</summary>
        public static bool Affects(JamSource s, int frequency, double jamResistance)
        {
            if (s == null || s.Frequencies == null || Array.IndexOf(s.Frequencies, frequency) < 0) return false;
            return Clamp01(s.Effectiveness - jamResistance) > 0;
        }

        /// <summary>T-206: the range to the jammer is known to the player (a ranging sensor or a narrow error ellipse), not just a bearing-derived estimate. Needs Locate() first (DistM).</summary>
        public static bool RangeUsable(JamSource s)
        {
            return s != null && RangeClass(s) == OverviewMath.RangeKnown;
        }

        /// <summary>T-225: OverviewMath range class of the source (RangeKnown | RangeApprox | RangeNone). Needs Locate() first (DistM). RangeApprox sources cut the zone too, with "≈".</summary>
        public static int RangeClass(JamSource s)
        {
            return s == null ? OverviewMath.RangeNone : OverviewMath.RangeOf(false, s.Sensors, s.SigmaM, s.DistM);
        }

        /// <summary>T-225: the burn-through of this radar is set by (has a non-zero term from) at least one source with an estimated range.</summary>
        public static bool CutHasApprox(RadarJam j)
        {
            if (j == null) return false;
            foreach (JamSource s in j.Known)
                if (s.RangeApprox && Affects(s, j.Frequency, j.JamResistance)) return true;
            return false;
        }

        /// <summary>Bearing and distance from the radar antenna to the track estimate: great circle plus the height difference when the height is measured.</summary>
        public static void Locate(RadarSpec r, JamSource s)
        {
            double b, g;
            CoverageMath.BearingDistance(r.Lat, r.Lon, s.Lat, s.Lon, r.Globe, out b, out g);   // T-354
            s.BearingDeg = b;
            double dh = double.IsNaN(s.AltM) ? 0.0 : s.AltM - r.AntennaM;
            s.DistM = Math.Sqrt(g * g + dh * dh);
        }

        /// <summary>
        /// ECM.CombinedJamUnit without self-screening: R = (J2S * P * 1000 * G * sigma / sum(Pj * 1000 * Gj * clamp01(eff - res) * 4 pi / d^2))^(1/4) * crew.
        /// +inf when no known jammer affects this radar.
        /// </summary>
        public static double BurnThroughM(RadarSpec r, RadarJam j, double rcsM2)
        {
            if (r == null || j == null || !(rcsM2 > 0)) return double.PositiveInfinity;
            double den = 0;
            foreach (JamSource s in j.Known)
            {
                if (!Affects(s, j.Frequency, j.JamResistance)) continue;
                double d = Math.Max(1.0, s.DistM);
                den += s.PeakKw * 1000.0 * s.GainLin * Clamp01(s.Effectiveness - j.JamResistance) * 4.0 * Math.PI / (d * d);
            }
            if (!(den > 0)) return double.PositiveInfinity;
            double num = j.J2S * r.PeakPowerKw * 1000.0 * r.GainFactor * rcsM2;
            if (!(num > 0)) return 0.0;
            return Math.Pow(num / den, 0.25) * j.CrewFactor;
        }

        // ---------------------------------------------------------------- knowledge rule and states

        /// <summary>ADR-0081 section 4 as a predicate (every input is read by the caller on the main thread). ecmType: 0 Defensive, 1 Offensive, 2 Universal.</summary>
        public static bool IsKnownJammer(bool inList, int ecmType, bool on, bool inoperable, bool destroyed, bool hasTrack, bool identified, bool hasPositionFix)
        {
            if (!inList || !hasTrack || !identified || !hasPositionFix) return false;
            if (ecmType != 1 && ecmType != 2) return false;
            return on && !inoperable && !destroyed;
        }

        private static double AngDiff(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        /// <summary>none: no signal of the game; unknown: signal and no known jammer; known: every game bearing lies within tolDeg of a known jammer; partial: otherwise.</summary>
        public static string Classify(bool signal, IList<double> gameBearingsDeg, IList<double> knownBearingsDeg, double tolDeg = BearingTolDeg)
        {
            if (!signal) return StateNone;
            if (knownBearingsDeg == null || knownBearingsDeg.Count == 0) return StateUnknown;
            if (gameBearingsDeg != null)
                foreach (double g in gameBearingsDeg)
                {
                    bool explained = false;
                    foreach (double k in knownBearingsDeg)
                        if (AngDiff(g, k) <= tolDeg + 1e-9) { explained = true; break; }
                    if (!explained) return StatePartial;
                }
            return StateKnown;
        }

        private static bool Active(RadarSpec r) { return CoverageMath.Emitting(r); }

        public static bool CutApplies(RadarSpec r)
        {
            if (r == null || r.Jam == null || !Active(r)) return false;
            return r.Jam.State == StateKnown || r.Jam.State == StatePartial;
        }

        // ---------------------------------------------------------------- profiles

        private static int Words(RayProfile p) { return (p.Bins + 63) / 64; }

        private static RayProfile CloneWith(RayProfile src)
        {
            return new RayProfile
            {
                Lat = src.Lat, Lon = src.Lon, RangeM = src.RangeM, BinM = src.BinM, Rays = src.Rays, Bins = src.Bins, Tier = src.Tier,
                Active = src.Active, Airborne = src.Airborne, Globe = src.Globe, ShadowPct = src.ShadowPct, OwnerUid = src.OwnerUid, Radar = src.Radar,
                Visible = src.Visible == null ? null : (ulong[])src.Visible.Clone()
            };
        }

        /// <summary>First bin whose cell centre lies beyond rbtM.</summary>
        private static int FirstCutBin(RayProfile p, double rbtM)
        {
            if (!(rbtM > 0)) return 0;
            double k = Math.Floor(rbtM / p.BinM + 0.5);
            return k >= p.Bins ? p.Bins : (int)k;
        }

        private static void ClearRange(RayProfile p, int from, int to)   // bins [from, to)
        {
            int words = Words(p);
            for (int ray = 0; ray < p.Rays; ray++)
                for (int k = from; k < to; k++)
                    p.Visible[ray * words + k / 64] &= ~(1UL << (k % 64));
        }

        /// <summary>Clone with every visible bin beyond rbtM cleared on all rays (shadows and sector kept); the same instance when rbtM covers the whole profile.</summary>
        public static RayProfile ApplyJam(RayProfile src, double rbtM)
        {
            if (src == null || src.Visible == null || !(src.RangeM > 0) || !(rbtM < src.RangeM)) return src;
            var p = CloneWith(src);
            ClearRange(p, FirstCutBin(p, rbtM), p.Bins);
            return p;
        }

        /// <summary>Outline of the uncut profile beyond rbtM (+ half a bin), Style 3, no union hiding.</summary>
        public static List<GeoSeg> DashedOutline(RayProfile uncut, double rbtM)
        {
            var res = new List<GeoSeg>();
            if (uncut == null || uncut.Visible == null || !(uncut.RangeM > 0) || !(rbtM < uncut.RangeM)) return res;
            var tail = CloneWith(uncut);
            tail.Active = true;
            ClearRange(tail, 0, FirstCutBin(tail, rbtM));
            double edge = rbtM + 0.5 * tail.BinM + 1e-6;
            foreach (GeoSeg g in CoverageMath.Outlines(new[] { tail }))
            {
                double b, d1, d2;
                CoverageMath.BearingDistance(tail.Lat, tail.Lon, g.Lat1, g.Lon1, tail.Globe, out b, out d1);
                CoverageMath.BearingDistance(tail.Lat, tail.Lon, g.Lat2, g.Lon2, tail.Globe, out b, out d2);
                if (Math.Max(d1, d2) <= edge) continue;   // the inner ring of the tail coincides with the new cut edge
                var s = g; s.Style = StyleJamDashed;
                res.Add(s);
            }
            return res;
        }

        private static RayProfile Empty(RayProfile src)
        {
            var p = CloneWith(src);
            p.Visible = null; p.RangeM = 0;
            return p;
        }

        /// <summary>Whole step of one radar in the worker: tier profiles in, cut profiles + dashed outline + ranges out. spec.Jam == null: the same instances.</summary>
        public static JamSpecResult ApplySpec(RadarSpec spec, IList<CoverageTier> tiers, IList<RayProfile> uncut, double minPowerKw)
        {
            int n = uncut.Count;
            var res = new JamSpecResult { Profiles = new RayProfile[n], RangeM = new double[n] };
            for (int i = 0; i < n; i++) { res.Profiles[i] = uncut[i]; res.RangeM[i] = double.PositiveInfinity; }
            if (spec == null || spec.Jam == null || !Active(spec)) return res;
            if (spec.Jam.State == StateUnknown && UnknownPolicy == "hide")
            {
                for (int i = 0; i < n; i++) res.Profiles[i] = Empty(uncut[i]);
                return res;
            }
            if (!CutApplies(spec)) return res;
            for (int i = 0; i < n && i < tiers.Count; i++)
            {
                double rbt = BurnThroughM(spec, spec.Jam, tiers[i].RcsM2);
                if (double.IsPositiveInfinity(rbt)) continue;
                double limit = CoverageMath.RangeLimit(spec, tiers[i], minPowerKw).RangeM;
                res.RangeM[i] = Math.Min(limit, rbt);
                RayProfile cut = ApplyJam(uncut[i], rbt);
                if (ReferenceEquals(cut, uncut[i])) continue;
                res.Profiles[i] = cut;
                res.Dashed.AddRange(DashedOutline(uncut[i], rbt));
                res.Cut = true;
            }
            return res;
        }

        // ---------------------------------------------------------------- labels

        private static double FurthestM(RadarSpec spec, JamSpecResult res, IList<CoverageTier> tiers, double minPowerKw, bool useCut)
        {
            double bestAir = 0, bestSurf = 0;
            for (int i = 0; i < tiers.Count; i++)
            {
                double v = useCut ? res.RangeM[i] : CoverageMath.RangeLimit(spec, tiers[i], minPowerKw).RangeM;
                if (double.IsInfinity(v) || !(v > 0)) continue;
                if (tiers[i].Air) bestAir = Math.Max(bestAir, v); else bestSurf = Math.Max(bestSurf, v);
            }
            return bestAir > 0 ? bestAir : bestSurf;
        }

        private static double StrongestBearing(RadarJam j)
        {
            double best = -1, bearing = double.NaN;
            foreach (JamSource s in j.Known)
            {
                if (!Affects(s, j.Frequency, j.JamResistance)) continue;
                double d = Math.Max(1.0, s.DistM);
                double w = s.PeakKw * s.GainLin * Clamp01(s.Effectiveness - j.JamResistance) / (d * d);
                if (w > best) { best = w; bearing = s.BearingDeg; }
            }
            return bearing;
        }

        /// <summary>
        /// One mark per owner: the furthest visible air tier of a cut radar (surface if no air tier) at the bearing of the strongest known jammer.
        /// Nothing is cut but the game reports bearings nobody explains (unknown / partial without effect): a bearing-only mark (the first game bearing).
        /// </summary>
        public static JamMark MarkFor(IList<RadarSpec> ownerSpecs, IList<JamSpecResult> results, IList<CoverageTier> tiers, double minPowerKw)
        {
            if (ownerSpecs == null || ownerSpecs.Count == 0) return null;
            string state = null;
            int bestCut = -1, bestAny = -1;
            double rCut = 0, rAny = 0;
            for (int i = 0; i < ownerSpecs.Count; i++)
            {
                RadarSpec s = ownerSpecs[i];
                if (s.Jam == null || s.Jam.State == StateNone || !Active(s)) continue;
                state = s.Jam.State;
                if (results[i].Cut)
                {
                    double r = FurthestM(s, results[i], tiers, minPowerKw, true);
                    if (r > rCut) { rCut = r; bestCut = i; }
                }
                double a = FurthestM(s, results[i], tiers, minPowerKw, false);
                if (a > rAny) { rAny = a; bestAny = i; }
            }
            if (state == null) return null;
            var m = new JamMark { OwnerUid = ownerSpecs[0].OwnerUid };
            if (bestCut >= 0)
            {
                RadarSpec s = ownerSpecs[bestCut];
                m.State = s.Jam.State == StatePartial ? StatePartial : StateKnown;
                m.RangeM = rCut;
                m.Approx = CutHasApprox(s.Jam);
                m.BearingDeg = StrongestBearing(s.Jam);
                if (double.IsNaN(m.BearingDeg)) m.BearingDeg = 0;
                CoverageMath.Destination(s.Lat, s.Lon, m.BearingDeg, rCut, s.Globe, out m.Lat, out m.Lon);
            }
            else
            {
                if (bestAny < 0) return null;
                RadarSpec s = ownerSpecs[bestAny];
                if (s.Jam.State == StateKnown) return null;   // every bearing is explained and no band is affected: nothing to say
                m.State = StateUnknown;
                m.RangeM = 0;
                m.BearingDeg = s.Jam.BearingsDeg != null && s.Jam.BearingsDeg.Length > 0 ? s.Jam.BearingsDeg[0] : 0;
                CoverageMath.Destination(s.Lat, s.Lon, m.BearingDeg, 0.6 * rAny, s.Globe, out m.Lat, out m.Lon);
            }
            m.Text = LabelText(m);
            return m;
        }

        public static string LabelText(JamMark m)
        {
            if (m == null) return "";
            if (m.State == StateUnknown) return UiText.Fmt("cov.jam.label_bearing", ((int)Math.Round(m.BearingDeg) % 360).ToString("000", CultureInfo.InvariantCulture) + "°");
            string nm = UnitFmt.Nm(m.RangeM / CoverageMath.MPerNm);
            // T-225-Q2: with partial the upper bound ("≤") wins over the estimate ("≈")
            return UiText.Fmt(m.State == StatePartial ? "cov.jam.label_le" : m.Approx ? "cov.jam.label_approx" : "cov.jam.label", nm);
        }

        /// <summary>Indices of the marks to draw: the selected own unit first, then the nearest to the map centre (px), at most max.</summary>
        public static List<int> PickLabels(IList<JamMark> marks, IList<double[]> xy, int selectedUid, double cx, double cy, int max = JamLabelsMax)
        {
            var idx = new List<int>();
            for (int i = 0; i < marks.Count; i++) if (xy[i] != null) idx.Add(i);
            idx.Sort((a, b) =>
            {
                bool sa = marks[a].OwnerUid == selectedUid, sb = marks[b].OwnerUid == selectedUid;
                if (sa != sb) return sa ? -1 : 1;
                double da = (xy[a][0] - cx) * (xy[a][0] - cx) + (xy[a][1] - cy) * (xy[a][1] - cy);
                double db = (xy[b][0] - cx) * (xy[b][0] - cx) + (xy[b][1] - cy) * (xy[b][1] - cy);
                int c = da.CompareTo(db);
                return c != 0 ? c : a.CompareTo(b);
            });
            if (idx.Count > max) idx.RemoveRange(max, idx.Count - max);
            return idx;
        }

        // ---------------------------------------------------------------- triggers

        /// <summary>Poll signature of one owner: signal flag, sorted "trackId:identified" set, bearing count. Order of the ids does not matter.</summary>
        public static string Signature(bool signal, IList<int> trackIds, IList<bool> identified, int bearings)
        {
            var parts = new List<string>();
            for (int i = 0; i < trackIds.Count; i++) parts.Add(trackIds[i].ToString(CultureInfo.InvariantCulture) + ":" + (identified[i] ? "1" : "0"));
            parts.Sort(StringComparer.Ordinal);
            return (signal ? "j1" : "j0") + "[" + string.Join(",", parts.ToArray()) + "]" + bearings.ToString(CultureInfo.InvariantCulture);
        }

        public static bool DistMoved(double lastM, double nowM)
        {
            return Math.Abs(nowM - lastM) > DistTriggerFrac * Math.Abs(lastM);
        }

        // ---------------------------------------------------------------- self test

        /// <summary>Reference table made by tools/bench/radar-jamming/burnthrough.py (SPS-49 280 kW / 39 dB, ALQ-99 11 kW / 5 dB, eff 1, J2S 30): { distance km, sigma m2, metres }.</summary>
        internal static readonly double[][] JamRefTable =
        {
            new double[] { 20, 1, 2795.3 },
            new double[] { 20, 10, 4970.9 },
            new double[] { 20, 1000, 15719.3 },
            new double[] { 50, 1, 4419.8 },
            new double[] { 50, 10, 7859.7 },
            new double[] { 50, 1000, 24854.5 },
            new double[] { 100, 1, 6250.6 },
            new double[] { 100, 10, 11115.2 },
            new double[] { 100, 1000, 35149.5 },
            new double[] { 200, 1, 8839.6 },
            new double[] { 200, 10, 15719.3 },
            new double[] { 200, 1000, 49708.9 },
        };

        private static string _fail;
        private static void Check(bool ok, string what) { if (!ok && _fail == null) _fail = what; }
        private static void Near(double got, double want, double relTol, string what)
        {
            if (_fail != null) return;
            if (double.IsNaN(got) || Math.Abs(got - want) > Math.Abs(want) * relTol + 1e-9)
                _fail = what + ": got " + got.ToString("R", CultureInfo.InvariantCulture) + ", want " + want.ToString("R", CultureInfo.InvariantCulture);
        }

        private const int BandS = 4, BandX = 6;

        private static RadarSpec TestRadar()
        {
            return new RadarSpec
            {
                Globe = true,   // T-352: the stands keep the beta (spherical) expectations; the flat world has its own sections
                OwnerUid = 1, OwnerName = "Test", Name = "SPS-49", Type = "Search", Air = true, Surface = true, On = true,
                Lat = 15.0, Lon = 42.0, AntennaM = 0, MinRangeM = 600, MaxRangeM = 463000, MinAltM = 3, MaxAltM = 45000,
                PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916, FullCircle = true, HArcMin = -181, HArcMax = 181,
                VArcMin = -91, VArcMax = 91, VisibleHeightM = 3
            };
        }

        private static JamSource TestJammer(RadarSpec r, double bearing, double distM, double eff = 1.0, int uid = 7)
        {
            var s = new JamSource { Uid = uid, PeakKw = 11, GainLin = Math.Pow(10, 0.5), Effectiveness = eff, Frequencies = new[] { BandS } };
            CoverageMath.Destination(r.Lat, r.Lon, bearing, distM, r.Globe, out s.Lat, out s.Lon);
            Locate(r, s);
            return s;
        }

        private static RadarJam TestJam(RadarSpec r, params JamSource[] src)
        {
            var j = new RadarJam { Frequency = BandS, J2S = 30.0, State = StateKnown };
            j.Known.AddRange(src);
            return j;
        }

        private static int CountBits(RayProfile p)
        {
            int n = 0;
            if (p.Visible == null) return 0;
            foreach (ulong w in p.Visible) { ulong v = w; while (v != 0) { n++; v &= v - 1; } }
            return n;
        }

        private static bool BitAt(RayProfile p, int ray, int bin) { return (p.Visible[ray * Words(p) + bin / 64] >> (bin % 64) & 1UL) != 0; }

        private static RayProfile FullProfile(int rays, int bins, double binM)
        {
            var p = new RayProfile { Lat = 15, Lon = 42, Rays = rays, Bins = bins, BinM = binM, RangeM = bins * binM, Active = true, OwnerUid = 1, Radar = "SPS-49", Visible = new ulong[rays * ((bins + 63) / 64)] };
            for (int ray = 0; ray < rays; ray++)
                for (int k = 0; k < bins; k++)
                    p.Visible[ray * Words(p) + k / 64] |= 1UL << (k % 64);
            return p;
        }

        public static string SelfTest()
        {
            _fail = null;
            string savedPolicy = UnknownPolicy;
            try { SelfTestChecks(); }
            catch (Exception e) { return "coverage jam selftest exception: " + e.Message; }
            finally { UnknownPolicy = savedPolicy; }
            return _fail == null ? null : "coverage jam selftest: " + _fail;
        }

        /// <summary>T-354: bearing/range of the jammer, the dashed outline and the mark follow the world flag (60 N and others).</summary>
        private static void WorldFlatChecks()
        {
            foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
            {
                var r = TestRadar(); r.Lat = phi; r.Globe = false;
                double lat, lon;
                CoverageMath.Destination(r.Lat, r.Lon, 90, 50000, false, out lat, out lon);
                var s = new JamSource { Uid = 3, Lat = lat, Lon = lon, AltM = double.NaN };
                Locate(r, s);
                Check(Math.Abs(s.BearingDeg - 90) < 1e-9, "T-354 flat locate bearing at " + phi + ": " + s.BearingDeg);
                Check(Math.Abs(s.DistM - 50000) < 1e-6, "T-354 flat locate range at " + phi + ": " + s.DistM);
                var g = TestRadar(); g.Lat = phi; g.Globe = true;
                CoverageMath.Destination(g.Lat, g.Lon, 90, 50000, true, out lat, out lon);
                var sg = new JamSource { Uid = 4, Lat = lat, Lon = lon, AltM = double.NaN };
                Locate(g, sg);
                double b0, d0;
                CoverageMath.BearingDistance(g.Lat, g.Lon, lat, lon, g.Globe, out b0, out d0);
                Check(sg.BearingDeg == b0 && sg.DistM == d0, "T-354 globe locate equals BearingDistance(globe) at " + phi);
            }
            // the dashed outline on the flat world at 60 N: every segment is beyond R_bt by the flat metric
            {
                var p = FullProfile(36, 64, 1000); p.Lat = 60; p.Globe = false;
                var dashed = DashedOutline(p, 40 * 1000.0);
                bool far = dashed.Count > 0;
                foreach (GeoSeg g in dashed)
                {
                    double b, d1, d2;
                    CoverageMath.BearingDistance(p.Lat, p.Lon, g.Lat1, g.Lon1, false, out b, out d1);
                    CoverageMath.BearingDistance(p.Lat, p.Lon, g.Lat2, g.Lon2, false, out b, out d2);
                    if (Math.Max(d1, d2) < 40 * 1000.0 - 1e-6) far = false;
                }
                Check(far, "T-354 flat dashed outline beyond R_bt at 60 N");
            }
            // the mark at 60 N, flat: its point sits at (BearingDeg, RangeM) from the radar by the flat metric
            {
                var tiers = new List<CoverageTier> { new CoverageTier { HeightM = 15, RcsM2 = 1, Air = true } };
                var spec = TestRadar(); spec.Lat = 60; spec.Globe = false;
                var unc = new List<RayProfile> { CoverageMath.BuildProfile(spec, tiers[0], 0, 2e-14, null, 72, 64) };
                spec.Jam = TestJam(spec, TestJammer(spec, 90, 50000));
                var res = ApplySpec(spec, tiers, unc, 2e-14);
                var m = MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { res }, tiers, 2e-14);
                Check(m != null, "T-354 flat mark exists");
                if (m != null)
                {
                    double b, d;
                    CoverageMath.BearingDistance(spec.Lat, spec.Lon, m.Lat, m.Lon, false, out b, out d);
                    Check(Math.Abs(b - m.BearingDeg) < 1e-6 && Math.Abs(d - m.RangeM) < 1e-3, "T-354 flat mark point " + b + "/" + d + " vs " + m.BearingDeg + "/" + m.RangeM);
                }
            }
        }

        private static void SelfTestChecks()
        {
            WorldFlatChecks();   // T-354
            var radar = TestRadar();
            // --- A1 burn-through against the reference table (+-1 %)
            foreach (double[] row in JamRefTable)
            {
                var j = TestJam(radar, TestJammer(radar, 90, row[0] * 1000.0));
                Near(BurnThroughM(radar, j, row[1]), row[2], 0.01, "R_bt d=" + row[0] + " km sigma=" + row[1]);
            }
            {
                var j2 = TestJam(radar, TestJammer(radar, 90, 50000), TestJammer(radar, 200, 100000, 0.5, 8));
                Near(BurnThroughM(radar, j2, 1), 4291.6, 0.01, "two jammers");
                var jc = TestJam(radar, TestJammer(radar, 90, 50000)); jc.CrewFactor = 1.2;
                Near(BurnThroughM(radar, jc, 1), 5303.8, 0.01, "crew 1.2");
                var j3 = TestJam(radar, TestJammer(radar, 90, 50000)); j3.J2S = 3.0;
                Near(BurnThroughM(radar, j3, 1), 4419.8 * Math.Pow(10, -0.25), 0.01, "J2S 3 vs 30");
                var half = TestJam(radar, TestJammer(radar, 90, 50000, 0.5));
                Near(BurnThroughM(radar, half, 1), 4419.8 * Math.Pow(2.0, 0.25), 0.01, "eff 0.5");
                var res = TestJam(radar, TestJammer(radar, 90, 50000, 0.6)); res.JamResistance = 0.1;
                Near(BurnThroughM(radar, res, 1), 4419.8 * Math.Pow(2.0, 0.25), 0.01, "eff - resistance = 0.5");
                var other = TestJam(radar, TestJammer(radar, 90, 50000)); other.Frequency = BandX;
                Check(double.IsPositiveInfinity(BurnThroughM(radar, other, 1)), "other band does not cut");
                var weak = TestJam(radar, TestJammer(radar, 90, 50000, 0.3)); weak.JamResistance = 0.3;
                Check(double.IsPositiveInfinity(BurnThroughM(radar, weak, 1)), "eff <= resistance does not cut");
                Check(double.IsPositiveInfinity(BurnThroughM(radar, new RadarJam { Frequency = BandS }, 1)), "no jammers");
            }
            // --- A1 Locate: the height difference enters the distance
            {
                var s = TestJammer(radar, 0, 30000); s.AltM = 40000; Locate(radar, s);
                Near(s.DistM, Math.Sqrt(30000.0 * 30000.0 + 40000.0 * 40000.0), 0.001, "locate 3D");
            }
            // --- A2 knowledge rule
            Check(IsKnownJammer(true, 1, true, false, false, true, true, true), "known: offensive");
            Check(IsKnownJammer(true, 2, true, false, false, true, true, true), "known: universal");
            Check(!IsKnownJammer(false, 1, true, false, false, true, true, true), "not in the list");
            Check(!IsKnownJammer(true, 0, true, false, false, true, true, true), "defensive");
            Check(!IsKnownJammer(true, 1, false, false, false, true, true, true), "switched off");
            Check(!IsKnownJammer(true, 1, true, true, false, true, true, true), "inoperable");
            Check(!IsKnownJammer(true, 1, true, false, true, true, true, true), "destroyed");
            Check(!IsKnownJammer(true, 1, true, false, false, false, true, true), "no track");
            Check(!IsKnownJammer(true, 1, true, false, false, true, false, true), "not identified");
            Check(!IsKnownJammer(true, 1, true, false, false, true, true, false), "no position estimate");
            // --- T-206 range knowledge
            {
                var rs = new JamSource { Sensors = 0x8, DistM = 300000 };
                Check(RangeUsable(rs), "range: search radar");
                rs.Sensors = 0x200; Check(!RangeUsable(rs), "range: esm without sigma");
                rs.Sensors = 0; rs.SigmaM = 0; Check(RangeUsable(rs), "range: no sensors sigma 0");
                rs.Sensors = 0x200; rs.SigmaM = 1000; Check(RangeUsable(rs), "range: esm narrow ellipse at 300 km");
                rs.SigmaM = 50000; Check(!RangeUsable(rs), "range: esm wide ellipse");
                var cl = rs.Clone(); Check(cl.Sensors == 0x200 && cl.SigmaM == 50000, "clone copies sensors and sigma");
                // T-225 J1: range class and the "≈" label
                Check(RangeClass(rs) == OverviewMath.RangeApprox, "range class: esm wide ellipse is approx");
                rs.Sensors = 0x8; Check(RangeClass(rs) == OverviewMath.RangeKnown, "range class: search radar is known");
                Check(RangeClass(null) == OverviewMath.RangeNone, "range class: null");
                rs.Sensors = 0x200; rs.SigmaM = 1000; Check(!RangeUsable(rs) == (RangeClass(rs) != OverviewMath.RangeKnown), "RangeUsable follows RangeClass");
            }
            {
                var tiers = new List<CoverageTier> { new CoverageTier { HeightM = 15, RcsM2 = 1, Air = true } };
                var spec = TestRadar();
                var unc = new List<RayProfile> { CoverageMath.BuildProfile(spec, tiers[0], 0, 2e-14, null, 72, 64) };
                var esm = TestJammer(spec, 90, 50000); esm.Sensors = 0x200; esm.SigmaM = 50000; esm.RangeApprox = RangeClass(esm) == OverviewMath.RangeApprox;
                Check(esm.RangeApprox, "esm source is approx");
                Check(Classify(true, new double[] { 85 }, new double[] { esm.BearingDeg }) == StateKnown, "esm bearing explains the game bearing");
                spec.Jam = TestJam(spec, esm);
                var ra = ApplySpec(spec, tiers, unc, 2e-14);
                Check(ra.Cut, "approx source cuts the profile");
                var ma = MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { ra }, tiers, 2e-14);
                Check(ma != null && ma.State == StateKnown && ma.Approx, "approx mark: known + Approx");
                using (UiText.Push("ru")) Check(ma != null && LabelText(ma).StartsWith("помехи: ≈ "), "approx label ru: " + (ma == null ? "" : LabelText(ma)));
                using (UiText.Push("en")) Check(ma != null && LabelText(ma).StartsWith("jamming: ≈ "), "approx label en");
                var exact = TestJammer(spec, 90, 50000);
                spec.Jam = TestJam(spec, exact);
                var me = MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { ApplySpec(spec, tiers, unc, 2e-14) }, tiers, 2e-14);
                Check(me != null && !me.Approx && !LabelText(me).Contains("≈"), "exact source: no approx");
                spec.Jam = TestJam(spec, esm); spec.Jam.State = StatePartial;
                var mp = MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { ApplySpec(spec, tiers, unc, 2e-14) }, tiers, 2e-14);
                using (UiText.Push("ru")) Check(mp != null && mp.State == StatePartial && LabelText(mp).StartsWith("помехи: ≤ ") && !LabelText(mp).Contains("≈"), "partial + approx: upper bound wins");
                var other = TestJam(spec, esm); other.Frequency = BandX;
                Check(!CutHasApprox(other), "approx source of another band is not a cause");
            }
            Check(Classify(false, new double[0], new double[0]) == StateNone, "class none");
            Check(Classify(true, new double[] { 40 }, new double[0]) == StateUnknown, "class unknown");
            Check(Classify(true, new double[] { 40 }, new double[] { 52 }) == StateKnown, "class known");
            Check(Classify(true, new double[] { 40, 200 }, new double[] { 45 }) == StatePartial, "class partial");
            Check(Classify(true, new double[] { 359 }, new double[] { 3 }) == StateKnown, "class wrap 359/3");
            Check(Classify(true, new double[] { 40 }, new double[] { 56 }) == StatePartial, "class 16 deg is partial");
            Check(Classify(true, new double[] { 40 }, new double[] { 55 }) == StateKnown, "class 15 deg is known");
            // --- A3 ApplyJam / DashedOutline / ApplySpec
            {
                var p = FullProfile(36, 64, 1000);
                for (int ray = 10; ray <= 12; ray++) for (int k = 20; k <= 30; k++) p.Visible[ray * Words(p) + k / 64] &= ~(1UL << (k % 64));   // shadow
                int before = CountBits(p);
                RayProfile cut = ApplyJam(p, 40 * 1000.0);
                Check(!ReferenceEquals(cut, p) && CountBits(p) == before, "ApplyJam clones");
                bool beyond = false, inside = true;
                for (int ray = 0; ray < 36; ray++)
                    for (int k = 0; k < 64; k++)
                    {
                        if (k >= 40 && BitAt(cut, ray, k)) beyond = true;
                        if (k < 40 && BitAt(cut, ray, k) != BitAt(p, ray, k)) inside = false;
                    }
                Check(!beyond, "ApplyJam clears cells beyond R_bt on all rays");
                Check(inside, "ApplyJam keeps the shadow and the cells inside");
                Check(ReferenceEquals(ApplyJam(p, 1e9), p) && ReferenceEquals(ApplyJam(p, p.RangeM), p), "ApplyJam: R_bt >= range -> same instance");
                var dashed = DashedOutline(p, 40 * 1000.0);
                Check(dashed.Count > 0, "dashed outline exists");
                bool allStyle = true, far = true;
                foreach (GeoSeg g in dashed)
                {
                    if (g.Style != StyleJamDashed) allStyle = false;
                    double b, d1, d2;
                    CoverageMath.BearingDistance(p.Lat, p.Lon, g.Lat1, g.Lon1, p.Globe, out b, out d1);
                    CoverageMath.BearingDistance(p.Lat, p.Lon, g.Lat2, g.Lon2, p.Globe, out b, out d2);
                    if (Math.Max(d1, d2) < 40 * 1000.0) far = false;
                }
                Check(allStyle && far, "dashed outline: Style 3 and beyond R_bt");
                Check(DashedOutline(p, 1e9).Count == 0, "no dashed outline when nothing is cut");
            }
            {
                var tiers = new List<CoverageTier> { new CoverageTier { HeightM = 15, RcsM2 = 1, Air = true }, new CoverageTier { HeightM = 3000, RcsM2 = 10, Air = true } };
                var spec = TestRadar();
                var unc = new List<RayProfile>();
                for (int t = 0; t < tiers.Count; t++) unc.Add(CoverageMath.BuildProfile(spec, tiers[t], t, 2e-14, null, 72, 64));
                var none = ApplySpec(spec, tiers, unc, 2e-14);
                Check(ReferenceEquals(none.Profiles[0], unc[0]) && none.Dashed.Count == 0 && !none.Cut, "ApplySpec: Jam == null -> same instances");
                spec.Jam = TestJam(spec, TestJammer(spec, 90, 50000));
                var r = ApplySpec(spec, tiers, unc, 2e-14);
                Check(r.Cut && r.Dashed.Count > 0, "ApplySpec: known -> cut + dashed");
                Near(r.RangeM[0], 4419.8, 0.01, "ApplySpec range tier 0");
                Near(r.RangeM[1], 7859.7, 0.01, "ApplySpec range tier 1");
                Check(CountBits(r.Profiles[0]) < CountBits(unc[0]), "ApplySpec cuts the profile");
                var other = TestJam(spec, TestJammer(spec, 90, 50000)); other.Frequency = BandX; spec.Jam = other;
                Check(!ApplySpec(spec, tiers, unc, 2e-14).Cut, "ApplySpec: other band");
                spec.Jam = TestJam(spec, TestJammer(spec, 90, 50000));
                spec.On = false;
                Check(!ApplySpec(spec, tiers, unc, 2e-14).Cut, "ApplySpec: radar off");
                spec.On = true; spec.Emcon = true;
                Check(ApplySpec(spec, tiers, unc, 2e-14).Cut, "ApplySpec: Emcon as the game (an emitting radar is jammed)");
                spec.Emcon = false;
                spec.Jam = new RadarJam { State = StateUnknown, Frequency = BandS, BearingsDeg = new double[] { 40 } };
                UnknownPolicy = "mark";
                var um = ApplySpec(spec, tiers, unc, 2e-14);
                Check(!um.Cut && ReferenceEquals(um.Profiles[0], unc[0]), "ApplySpec: unknown + mark -> no cut");
                UnknownPolicy = "hide";
                var uh = ApplySpec(spec, tiers, unc, 2e-14);
                UnknownPolicy = "mark";
                Check(uh.Profiles[0].Visible == null && !(uh.Profiles[0].RangeM > 0) && CountBits(unc[0]) > 0, "ApplySpec: unknown + hide -> empty clone");
                // --- A5 marks
                var other2 = TestRadar(); other2.Name = "Other"; other2.Jam = new RadarJam { State = StateKnown, Frequency = BandX };
                spec.Jam = TestJam(spec, TestJammer(spec, 90, 50000), TestJammer(spec, 200, 150000, 1.0, 9));
                var res1 = ApplySpec(spec, tiers, unc, 2e-14);
                var res2 = ApplySpec(other2, tiers, unc, 2e-14);
                var mark = MarkFor(new List<RadarSpec> { spec, other2 }, new List<JamSpecResult> { res1, res2 }, tiers, 2e-14);
                Check(mark != null && mark.State == StateKnown && mark.OwnerUid == 1, "mark: known");
                if (mark != null)
                {
                    Near(mark.RangeM, res1.RangeM[1], 1e-9, "mark: furthest air tier");
                    Near(mark.BearingDeg, 90, 0.001, "mark: strongest jammer bearing");
                }
                spec.Jam.State = StatePartial;
                var mp = MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { res1 }, tiers, 2e-14);
                Check(mp != null && mp.State == StatePartial, "mark: partial");
                spec.Jam = new RadarJam { State = StateUnknown, Frequency = BandS, BearingsDeg = new double[] { 40, 200 } };
                var mu = MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { ApplySpec(spec, tiers, unc, 2e-14) }, tiers, 2e-14);
                Check(mu != null && mu.State == StateUnknown && Math.Abs(mu.BearingDeg - 40) < 1e-9, "mark: unknown -> first game bearing");
                spec.Jam = new RadarJam { State = StateNone };
                Check(MarkFor(new List<RadarSpec> { spec }, new List<JamSpecResult> { ApplySpec(spec, tiers, unc, 2e-14) }, tiers, 2e-14) == null, "mark: none");
                using (UiText.Push("ru"))
                {
                    Check(LabelText(new JamMark { State = StateUnknown, BearingDeg = 40 }) == "помехи: пеленг 040°", "label ru bearing: " + LabelText(new JamMark { State = StateUnknown, BearingDeg = 40 }));
                    string k = LabelText(new JamMark { State = StateKnown, RangeM = 21.3 * 1852 });
                    string pr = LabelText(new JamMark { State = StatePartial, RangeM = 21.3 * 1852 });
                    Check(k.StartsWith("помехи: 21,3") && pr.StartsWith("помехи: ≤ 21,3"), "label ru: " + k + " | " + pr);
                }
                using (UiText.Push("en"))
                {
                    string k = LabelText(new JamMark { State = StateKnown, RangeM = 21.3 * 1852 });
                    Check(k.StartsWith("jamming: 21.3"), "label en: " + k);
                }
            }
            // --- A5 PickLabels
            {
                var marks = new List<JamMark>(); var xy = new List<double[]>();
                for (int i = 0; i < 9; i++) { marks.Add(new JamMark { OwnerUid = 10 + i }); xy.Add(new double[] { 100 + i * 50, 100 }); }
                var pick = PickLabels(marks, xy, 18, 100, 100);
                Check(pick.Count == JamLabelsMax && pick[0] == 8 && pick[1] == 0 && pick[2] == 1, "PickLabels: selected first, then nearest, max 6");
                xy[3] = null;
                Check(!PickLabels(marks, xy, -1, 100, 100).Contains(3), "PickLabels: off-screen marks are skipped");
            }
            // --- A4 triggers and latch
            Check(Signature(true, new[] { 5, 2 }, new[] { true, false }, 2) == Signature(true, new[] { 2, 5 }, new[] { false, true }, 2), "Signature ignores the order");
            Check(Signature(true, new[] { 2 }, new[] { false }, 1) != Signature(true, new[] { 2 }, new[] { true }, 1), "Signature: Identified changes it");
            Check(Signature(true, new[] { 2 }, new[] { true }, 1) != Signature(true, new[] { 2 }, new[] { true }, 2), "Signature: bearing count changes it");
            Check(Signature(false, new int[0], new bool[0], 0) != Signature(true, new int[0], new bool[0], 0), "Signature: signal changes it");
            Check(!DistMoved(100, 109) && DistMoved(100, 111) && DistMoved(100, 89) && !DistMoved(100, 91), "DistMoved 10 %");
            {
                var latch = new JamSignalLatch();
                double[] b;
                Check(!latch.Update(1, new float[0], 0, out b) && b.Length == 0, "latch: empty at start");
                Check(latch.Update(1, new float[] { 40f }, 1, out b) && b.Length == 1, "latch: signal");
                Check(latch.Update(1, new float[0], 6.9, out b) && b.Length == 1 && Math.Abs(b[0] - 40) < 1e-6, "latch: first signal held 6 s (5.9 after)");
                Check(!latch.Update(1, new float[0], 7.1, out b) && b.Length == 0, "latch: released after 6 s");
                latch.Update(2, new float[] { 10f }, 5, out b); latch.Update(3, new float[] { 20f }, 5, out b);
                latch.Forget(new List<int> { 2 });
                Check(latch.Count == 1, "latch: Forget removes the dead");
                latch.Reset();
                Check(latch.Count == 0, "latch: Reset");
            }
            // --- T-190 B1: adaptive hold
            Near(LatchHoldS(0), 6, 1e-9, "LatchHoldS(0)"); Near(LatchHoldS(4), 10, 1e-9, "LatchHoldS(4)"); Near(LatchHoldS(100), 30, 1e-9, "LatchHoldS(100)"); Near(LatchHoldS(double.NaN), 6, 1e-9, "LatchHoldS(NaN)");
            {
                // the game fills the list once every 8 s (one poll sees it), polled every second: after the first cycle the signal never drops
                var latch = new JamSignalLatch();
                double[] b; int dropsAfterWarmup = 0, toggles0 = -1;
                for (int t = 0; t <= 120; t++)
                {
                    bool fill = t % 8 == 0;
                    bool sig = latch.Update(5, fill ? new float[] { 73f } : new float[0], t, out b);
                    if (t == 20) toggles0 = latch.Toggles;
                    if (t > 20 && !sig) dropsAfterWarmup++;
                }
                Check(dropsAfterWarmup == 0 && latch.Toggles == toggles0, "latch: fill every 8 s never drops after the first cycle (drops " + dropsAfterWarmup + ", toggles " + latch.Toggles + ")");
                double age, gap, hold;
                Check(latch.Diag(5, 120.5, out age, out gap, out hold) && Math.Abs(gap - 8) < 1e-9 && Math.Abs(hold - 20) < 1e-9 && Math.Abs(age - 0.5) < 1e-9, "latch: Diag " + gap + "/" + hold + "/" + age);
                Check(!latch.Diag(99, 1, out age, out gap, out hold), "latch: Diag of an unknown owner");
            }
            {
                // fill every 20 s: hold 30 s (ceiling)
                var latch = new JamSignalLatch();
                double[] b; int drops = 0;
                for (int t = 0; t <= 200; t++)
                {
                    bool sig = latch.Update(5, t % 20 == 0 ? new float[] { 10f } : new float[0], t, out b);
                    if (t > 45 && !sig) drops++;
                }
                Check(drops == 0, "latch: fill every 20 s holds up to 30 s (drops " + drops + ")");
                // the game stops: released at the ceiling at the latest, one toggle more
                int before = latch.Toggles; double[] b2;
                bool held31 = latch.Update(5, new float[0], 200 + 30.5, out b2);
                Check(!held31 && latch.Toggles == before + 1, "latch: released after the ceiling, one toggle (" + latch.Toggles + " vs " + before + ")");
            }
            {
                // reads in neighbouring frames are not pauses: the hold stays 6 s
                var latch = new JamSignalLatch();
                double[] b;
                for (int i = 0; i < 100; i++) latch.Update(5, new float[] { 10f }, i * 0.02, out b);
                double age, gap, hold;
                latch.Diag(5, 2.0, out age, out gap, out hold);
                Check(Math.Abs(hold - 6) < 1e-9 && gap == 0, "latch: frame-to-frame reads keep the 6 s hold (" + hold + ")");
                Check(latch.Holds(5, 1.98 + 5.9) && !latch.Holds(5, 1.98 + 6.1), "latch: Holds follows the hold");
                // continuous polling every 1 s while the list stays filled does not shrink the learned pause
                var l2 = new JamSignalLatch();
                for (int t = 0; t <= 100; t++)
                {
                    int ph = t % 20;
                    l2.Update(5, ph <= 3 ? new float[] { 10f } : new float[0], t, out b);   // filled for 4 s, empty for 16 s
                }
                l2.Diag(5, 100, out age, out gap, out hold);
                Check(gap >= 12, "latch: learned pause survives continuous reads (" + gap + ")");
            }
        }
    }

    /// <summary>
    /// Holds the jam signal of an owner after the last non-empty _jamBearings read (the game clears the list at the start of every data-link cycle and
    /// fills it on the antenna sweep) and unions the bearings seen in that window. A bearing within 5 degrees of a held one refreshes it.
    /// T-190: the hold is adaptive, LatchHoldS(longest of the last 4 pauses between fills), 6..30 s; before any pause is known it is 6 s.
    /// A pause is the time from the last non-empty read to the next one when an empty read was seen in between (or it is 1.5 s or longer); reads in
    /// neighbouring frames (under 0.5 s apart) are never pauses.
    /// </summary>
    internal sealed class JamSignalLatch
    {
        private sealed class Entry { public double Deg, Seen; }
        private sealed class Owner
        {
            public readonly List<Entry> List = new List<Entry>();
            public readonly double[] Gaps = new double[4];
            public int GapN;
            public double LastRead = double.NegativeInfinity;
            public bool EmptySeen;
            public double MaxGap { get { double m = 0; for (int i = 0; i < GapN && i < Gaps.Length; i++) if (Gaps[i] > m) m = Gaps[i]; return m; } }
            public double Hold { get { return CoverageJamMath.LatchHoldS(MaxGap); } }
        }
        private readonly Dictionary<int, Owner> _byOwner = new Dictionary<int, Owner>();

        public int Count { get { return _byOwner.Count; } }

        /// <summary>Number of times a held signal was released (all owners).</summary>
        public int Toggles { get; private set; }

        public IEnumerable<int> Owners { get { return _byOwner.Keys; } }

        public bool Update(int ownerUid, IList<float> bearingsNow, double now, out double[] bearings)
        {
            Owner o;
            if (!_byOwner.TryGetValue(ownerUid, out o)) o = null;
            bool fresh = bearingsNow != null && bearingsNow.Count > 0;
            if (o != null)
            {
                double hold = o.Hold;
                int had = o.List.Count;
                o.List.RemoveAll(e => now - e.Seen > hold + 1e-9);
                if (had > 0 && o.List.Count == 0) Toggles++;
            }
            if (fresh)
            {
                if (o == null) _byOwner[ownerUid] = o = new Owner();
                double gap = now - o.LastRead;
                if (!double.IsInfinity(gap) && gap > CoverageJamMath.SignalGapMinS && (o.EmptySeen || gap >= 1.5))
                    o.Gaps[o.GapN++ % o.Gaps.Length] = gap;
                o.LastRead = now; o.EmptySeen = false;
                for (int i = 0; i < bearingsNow.Count; i++)
                {
                    double d = ((bearingsNow[i] % 360.0) + 360.0) % 360.0;
                    Entry hit = null;
                    foreach (Entry e in o.List) { double a = Math.Abs(e.Deg - d) % 360.0; if ((a > 180.0 ? 360.0 - a : a) <= 5.0) { hit = e; break; } }
                    if (hit == null) o.List.Add(new Entry { Deg = d, Seen = now });
                    else { hit.Deg = d; hit.Seen = now; }
                }
            }
            else if (o != null && !double.IsInfinity(o.LastRead)) o.EmptySeen = true;
            if (o == null || o.List.Count == 0) { bearings = new double[0]; return false; }
            bearings = new double[o.List.Count];
            for (int i = 0; i < o.List.Count; i++) bearings[i] = o.List[i].Deg;
            return true;
        }

        /// <summary>True while the owner still holds a signal (cheap poll, no allocation).</summary>
        public bool Holds(int ownerUid, double now)
        {
            Owner o;
            if (!_byOwner.TryGetValue(ownerUid, out o)) return false;
            double hold = o.Hold;
            foreach (Entry e in o.List) if (now - e.Seen <= hold + 1e-9) return true;
            return false;
        }

        /// <summary>Dev diagnostics: seconds since the last non-empty read, the longest recent pause between fills, the current hold. False = unknown owner.</summary>
        public bool Diag(int ownerUid, double now, out double ageS, out double gapS, out double holdS)
        {
            ageS = gapS = holdS = 0;
            Owner o;
            if (!_byOwner.TryGetValue(ownerUid, out o)) return false;
            ageS = double.IsInfinity(o.LastRead) ? 0 : now - o.LastRead;
            gapS = o.MaxGap; holdS = o.Hold;
            return true;
        }

        public void Forget(ICollection<int> aliveOwners)
        {
            var dead = new List<int>();
            foreach (int k in _byOwner.Keys) if (!aliveOwners.Contains(k)) dead.Add(k);
            foreach (int k in dead) _byOwner.Remove(k);
        }

        public void Reset() { _byOwner.Clear(); }
    }
}
