using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-216 S1: "QA 01 live" stand. Reproduces what the radar worker meets in the live QA 01 run at x4: ~26 radars of 4 ships, a few helicopters and F-14 CAP pairs
    /// over a Red Sea like coast (mountains, islands), 40 s of game time per build, a changing radar list (landing, take-off, an inoperable radar) and a DEM that is
    /// not loaded far from the camera (NaN, hypothesis H5). Only the stand uses it; nothing of the mod depends on this file.
    /// </summary>
    internal static class CoverageQa01Live
    {
        internal const double StepS = 40.0, MinPowerKw = 2e-14;
        private const double GroupLat = 17.0, GroupLon = 40.9, KnotMs = 0.514444;
        private const int EventLand = 4, EventTakeOff = 7, EventInoperable = 9;

        internal sealed class Report
        {
            public long Calls, Lookups, NanMisses;
            public int Builds, Built, Shifted, Reused;
            public long MaxAllocPerBuild;
            public int MemoCapacity, MemoCount;   // T-225: slots / entries after the last build
            public double HitPct;
            public long[] CallsPerBuild, LookupsPerBuild, AllocPerBuild;
            public int[] BuiltPerBuild, ShiftedPerBuild, RadarsPerBuild;
            public List<long>[] RebuiltKeys;   // profile keys rebuilt per build (owner uid in the high 32 bits)

            public string Line()
            {
                var sb = new StringBuilder();
                sb.Append("builds=" + Builds + " calls(3+)=" + Calls + " hit%=" + Math.Round(HitPct, 1) + " built(3+)=" + Built + " maxAlloc=" + MaxAllocPerBuild / 1024 + "K");
                sb.Append(" calls/build=[");
                for (int i = 0; i < Builds; i++) sb.Append((i > 0 ? "," : "") + CallsPerBuild[i]);
                sb.Append("] built/build=[");
                for (int i = 0; i < Builds; i++) sb.Append((i > 0 ? "," : "") + BuiltPerBuild[i]);
                sb.Append("] radars=[");
                for (int i = 0; i < Builds; i++) sb.Append((i > 0 ? "," : "") + RadarsPerBuild[i]);
                return sb.Append("]").ToString();
            }
        }

        /// <summary>Red Sea like terrain. Sea axis lon = 41.2 + 0.55 (15 - lat), half width 1.2 deg; beyond the shore up to 2500 m + ripple; 4 islands; NaN beyond demRadiusKm of the camera.</summary>
        public static Func<double, double, double> RedSeaTerrain(double camLat, double camLon, double demRadiusKm, Action onCall)
        {
            var shared = new object();
            double cosCam = Math.Cos(camLat * Math.PI / 180.0);
            double[,] isl = { { 16.2, 40.0, 0.09, 180 }, { 17.6, 39.3, 0.12, 300 }, { 16.7, 40.4, 0.07, 100 }, { 18.3, 39.6, 0.10, 220 } };
            return (la, lo) =>
            {
                lock (shared) { if (onCall != null) onCall(); }
                double dy = (la - camLat) * 111320.0, dx = (lo - camLon) * 111320.0 * cosCam;
                if (dy * dy + dx * dx > demRadiusKm * demRadiusKm * 1e6) return double.NaN;   // chunk not in the game cache
                double axis = 41.2 + 0.55 * (15.0 - la);
                double off = Math.Abs(lo - axis) - 1.2;
                double h = -10.0;
                if (off > 0) h = Math.Min(2500.0, off * 3000.0) + 150.0 * (1.0 + Math.Sin(la * 40.0 + lo * 35.0));
                for (int i = 0; i < 4; i++)
                {
                    double ddy = la - isl[i, 0], ddx = (lo - isl[i, 1]) * Math.Cos(isl[i, 0] * Math.PI / 180.0);
                    if (ddy * ddy + ddx * ddx < isl[i, 2] * isl[i, 2]) h = Math.Max(h, isl[i, 3] * (1.0 - Math.Sqrt(ddy * ddy + ddx * ddx) / isl[i, 2]) + 5.0);
                }
                return h;
            };
        }

        private static RadarSpec Radar(int uid, string owner, string name, bool airborne, bool air, bool surface, double lat, double lon, double antM,
                                       double maxRangeM, double lookDown = 1.0)
        {
            return new RadarSpec
            {
                OwnerUid = uid, OwnerName = owner, Name = name, Type = "Search", Air = air, Surface = surface, On = true, Emcon = false, Airborne = airborne,
                Lat = lat, Lon = lon, AntennaM = antM, MinRangeM = 600, MaxRangeM = maxRangeM, MinAltM = 3, MaxAltM = 45000,
                PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916, FullCircle = true, VisibleHeightM = 3, TerrainUnderAntennaM = -10,
                LookDownMult = lookDown
            };
        }

        private static void Dest(double lat, double lon, double bearing, double distM, out double la, out double lo)
        {
            CoverageMath.Destination(lat, lon, bearing, distM, out la, out lo);
        }

        /// <summary>Radars of build b (1-based; game time (b - 1) x 40 s): ships, aircraft (list order as the game gives it) and the three events.</summary>
        public static List<RadarSpec> Units(int build, bool events = true)
        {
            double t = (build - 1) * StepS;
            var res = new List<RadarSpec>();
            double gLat, gLon;
            Dest(GroupLat, GroupLon, 331.0, 12.8 * KnotMs * t, out gLat, out gLon);
            // ships: name, owner uid, offset bearing/km, radars (name, air, surface, antenna, range)
            string[][] ships =
            {
                new[] { "Nimitz", "1", "0", "0", "SPS-48", "SPS-49", "SPS-67", "SPS-65" },
                new[] { "Yorktown", "2", "45", "5", "SPS-49", "SPS-55", "SPS-55" },
                new[] { "Udaloy", "3", "135", "8", "MR-760", "MR-350", "Nile" },
                new[] { "Perry", "4", "270", "6", "SPS-49", "SPS-55", "SPS-55" }
            };
            foreach (var sh in ships)
            {
                int uid = int.Parse(sh[1]);
                double sLat, sLon;
                Dest(gLat, gLon, double.Parse(sh[2]), double.Parse(sh[3]) * 1000.0, out sLat, out sLon);
                for (int i = 4; i < sh.Length; i++)
                {
                    string n = sh[i];
                    bool surfaceOnly = n == "SPS-55" || n == "SPS-67" || n == "SPS-65" || n == "Nile";
                    var r = Radar(uid, sh[0], n, false, !surfaceOnly, surfaceOnly || n == "MR-350", sLat, sLon, 28 + 3 * (i - 4), surfaceOnly ? 74000 : 463000);
                    if (events && uid == 3 && i == 5 && build >= EventInoperable) r.Inoperable = true;
                    res.Add(r);
                }
            }
            // take-off (build 7): a new F-14 is inserted at the START of the aircraft list
            if (events && build >= EventTakeOff)
            {
                double la, lo; Dest(gLat, gLon, 90, 3000 + 7000.0 * (build - EventTakeOff), out la, out lo);
                res.Add(Radar(28, "F-14 #28", "AWG-9", true, true, true, la, lo, 100 + 700.0 * (build - EventTakeOff), 333000, 0.2));
            }
            double ang0 = t * (350 * KnotMs) / 25000.0;   // rad on the CAP circle
            // E-2C and P-3C: circles of 40 km
            double a, b2;
            Dest(gLat, gLon, ang0 * 180 / Math.PI * 0.6, 40000, out a, out b2);
            res.Add(Radar(10, "E-2C", "APS-145", true, true, false, a, b2, 7600, 556000));
            Dest(gLat, gLon, 200 - ang0 * 180 / Math.PI * 0.6, 40000, out a, out b2);
            res.Add(Radar(11, "P-3C", "APS-137", true, true, true, a, b2, 900, 200000));
            // eight F-14 on CAP circles of 25 km, pairs 20 degrees apart; the landing one (uid 23) is removed from build 4
            for (int k = 0; k < 8; k++)
            {
                int uid = 20 + k;
                if (events && uid == 23 && build >= EventLand) continue;
                int pair = k / 2;
                double cLat, cLon;
                Dest(gLat, gLon, pair * 90.0, 30000, out cLat, out cLon);
                double bearing = (ang0 * 180 / Math.PI) + (k % 2) * 20.0 + pair * 45.0;
                double la, lo; Dest(cLat, cLon, bearing, 25000, out la, out lo);
                double alt = pair == 0 ? 300 + (7600 - 300) * Math.Min(1.0, (build - 1) / 5.0) : 7600;
                res.Add(Radar(uid, "F-14 #" + uid, "AWG-9", true, true, true, la, lo, alt, 333000, 0.2));
            }
            // helicopters: 60 m +- 3 m, 0..20 kt
            double[] speeds = { 10, 20, 0 };
            string[] hn = { "LN-66", "LN-66", "APS-116" };
            for (int k = 0; k < 3; k++)
            {
                double la, lo; Dest(gLat, gLon, 60.0 + 100 * k, 10000 + 3000.0 * k, out la, out lo);
                Dest(la, lo, 90, speeds[k] * KnotMs * t, out la, out lo);
                res.Add(Radar(30 + k, "SH #" + k, hn[k], true, false, true, la, lo, (k == 2 ? 90 : 60) + 3.0 * Math.Sin(build * 1.3 + k), 70000));
            }
            return res;
        }

        public static Report Run(WorkerTuning tuning, int builds = 12, double demRadiusKm = 150, bool events = true)
        {
            string err;
            var tiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
            var cfg = new CoverageConfig { Rays = 360, Bins = 128, Jamming = false };
            long calls = 0;
            var terrain = RedSeaTerrain(GroupLat, GroupLon, demRadiusKm, () => calls++);
            var w = new CoverageWorker(null, null, null, ms => { }, null, false);
            w.Tuning = tuning;
            var rep = new Report
            {
                Builds = builds, CallsPerBuild = new long[builds], LookupsPerBuild = new long[builds], AllocPerBuild = new long[builds],
                RebuiltKeys = new List<long>[builds], BuiltPerBuild = new int[builds], ShiftedPerBuild = new int[builds], RadarsPerBuild = new int[builds]
            };
            for (int b = 1; b <= builds; b++)
            {
                long c0 = calls, l0 = Lookups(w), a0 = GC.GetAllocatedBytesForCurrentThread();
                w.RebuiltLog = new List<long>();
                var f = w.BuildNow(Units(b, events), tiers, MinPowerKw, cfg, terrain, true);
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                if (f.Error != null) throw new InvalidOperationException(f.Error);
                int i = b - 1;
                rep.CallsPerBuild[i] = calls - c0;
                rep.LookupsPerBuild[i] = Lookups(w) - l0;
                rep.AllocPerBuild[i] = a1 - a0;
                rep.RebuiltKeys[i] = w.RebuiltLog;
                rep.BuiltPerBuild[i] = w.ProfilesBuilt;
                rep.ShiftedPerBuild[i] = w.ProfilesShifted;
                rep.RadarsPerBuild[i] = f.Radars;
                if (b >= 3)
                {
                    rep.Calls += rep.CallsPerBuild[i]; rep.Lookups += rep.LookupsPerBuild[i];
                    rep.Built += w.ProfilesBuilt; rep.Shifted += w.ProfilesShifted; rep.Reused += w.ProfilesReused;
                    if (rep.AllocPerBuild[i] > rep.MaxAllocPerBuild) rep.MaxAllocPerBuild = rep.AllocPerBuild[i];
                }
            }
            if (w.Memo != null) { rep.NanMisses = w.Memo.NanMisses; rep.MemoCapacity = w.Memo.Capacity; rep.MemoCount = w.Memo.Count; }
            rep.HitPct = rep.Lookups == 0 ? 0 : 100.0 * (rep.Lookups - rep.Calls) / rep.Lookups;
            return rep;
        }

        /// <summary>Share (%) of the visible cells of the exact profiles (no cache) that differ in the profiles the worker built through its terrain cache.</summary>
        private static double BitError()
        {
            string err;
            var tiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
            var cfg = new CoverageConfig { Rays = 360, Bins = 128, Jamming = false };
            var exact = RedSeaTerrain(GroupLat, GroupLon, 1e6, null);
            var all = Units(1);
            var pick = new List<RadarSpec> { all[0], all[12], all[all.Count - 3] };   // Nimitz SPS-48, a CAP fighter, a helicopter
            var w = new CoverageWorker(null, null, null, ms => { }, null, false);
            var f = w.BuildNow(pick, tiers, MinPowerKw, cfg, exact, true);
            long diff = 0, vis = 0;
            for (int i = 0; i < pick.Count; i++)
                for (int ti = 0; ti < tiers.Count; ti++)
                {
                    var direct = CoverageMath.BuildProfile(pick[i], tiers[ti], ti, MinPowerKw, exact, cfg.Rays, cfg.Bins);
                    var got = f.Profiles[i * tiers.Count + ti];
                    for (int k = 0; k < direct.Visible.Length; k++)
                    {
                        vis += System.Numerics.BitOperations.PopCount(direct.Visible[k]);
                        diff += System.Numerics.BitOperations.PopCount(direct.Visible[k] ^ got.Visible[k]);
                    }
                }
            return vis == 0 ? 0 : 100.0 * diff / vis;
        }

        private static long Lookups(CoverageWorker w) { return w.Memo == null ? 0 : w.Memo.Hits + w.Memo.Misses + w.Memo.NanTileHits; }

        /// <summary>puretest section "coverageqa01".</summary>
        public static string SelfTest()
        {
            try
            {
                // the unit lists: ~26 radars, events change the list
                int n1 = Units(1).Count, n4 = Units(4).Count, n7 = Units(7).Count;
                if (n1 < 24 || n1 > 27 || n4 != n1 - 1 || n7 != n4 + 1) return "units " + n1 + " " + n4 + " " + n7;
                // T-225: absolute thresholds of the T-216 S2 run (the comparison with the 0.30.8 behaviour was removed); measured 0.06 % wrong cells
                double errNew = BitError();
                if (errNew > 2.0) return "terrain cache accuracy: " + Math.Round(errNew, 2) + " % of visible cells differ (limit 2 %)";

                // S2: the targets of T-216 with the production defaults
                var now = Run(new WorkerTuning(), 12);
                Console.WriteLine("coverageqa01 T-216/T-225:   " + now.Line());
                if (now.CallsPerBuild[0] > 300000) return "first build: " + now.CallsPerBuild[0] + " game calls, limit 300000 (measured 141 k)";
                if (now.MemoCapacity != (1 << 21)) return "memo capacity " + now.MemoCapacity;
                if (100.0 * now.MemoCount / now.MemoCapacity >= 50.0) return "memo fill " + Math.Round(100.0 * now.MemoCount / now.MemoCapacity, 1) + " % on 12 builds";
                for (int i = 2; i < now.Builds; i++)
                {
                    bool ev = i + 1 == EventLand || i + 1 == EventTakeOff || i + 1 == EventInoperable;
                    long cap = ev ? 60000 : 20000;   // 2000 per second at RecomputeSeconds = 10
                    if (now.CallsPerBuild[i] > cap) return "build " + (i + 1) + ": " + now.CallsPerBuild[i] + " game calls, limit " + cap;
                    if (now.AllocPerBuild[i] > 2L * 1024 * 1024) return "build " + (i + 1) + ": allocated " + now.AllocPerBuild[i];
                }
                if (now.HitPct < 80) return "hit " + Math.Round(now.HitPct, 1) + " % < 80";
                if (now.Lookups < 1000000) return "too few lookups " + now.Lookups;
                // the events do not rebuild the ships they do not touch: the same run without events rebuilds exactly the same ship profiles
                var calm = Run(new WorkerTuning(), 12, 150, false);
                for (int i = 0; i < now.Builds; i++)
                    foreach (long k in now.RebuiltKeys[i])
                        if ((k >> 32) <= 4 && !calm.RebuiltKeys[i].Contains(k)) return "build " + (i + 1) + ": ship profile rebuilt only because of an event, uid " + (k >> 32);
                // a bigger part of the picture of a calm run shows through: without events the numbers are the same order
                if (now.Built > calm.Built + 40) return "events caused " + (now.Built - calm.Built) + " extra rebuilds";
                return null;
            }
            catch (Exception e) { return "coverageqa01 selftest exception: " + e; }
        }
    }
}
