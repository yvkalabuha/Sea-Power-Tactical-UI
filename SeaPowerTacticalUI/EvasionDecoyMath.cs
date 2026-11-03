using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>T-286 (ADR-0116): noisemaker spending of the own surface evasion: a reserve for the close range.
    /// Pure rule: no Unity, no reflection, no Harmony.</summary>
    internal static class EvasionDecoyMath
    {
        public const int ReserveN = 6;
        public const double CloseNm = 1.5, CloseEveryS = 30, FarEveryS = 60, RangeNm = 6;
        /// <summary>T-294: far band only when the time to impact is at most this (mission s).</summary>
        public const double FarMaxTtiS = 600;

        /// <param name="sinceLastS">mission seconds since the last launch on this unit (large = never)</param>
        /// <param name="distNm">threat distance to the torpedo</param>
        /// <param name="left">noisemakers left before this launch; -1 = could not be read</param>
        /// <param name="ttiS">time to impact, mission s (EvasionMath.Tti); +inf = unknown or not closing</param>
        /// <returns>"close" | "far" = launch in that band; else the skip reason "range" | "wake" | "empty" | "closing" | "reserve" | "interval".</returns>
        public static string Decide(double sinceLastS, double distNm, bool wakeHoming, int left, double ttiS)
        {
            return Decide(sinceLastS, distNm, wakeHoming, left, ttiS, false);
        }

        /// <param name="frozen">T-297: the track is frozen: the close band needs the residual time to impact too (FrozenTti)</param>
        public static string Decide(double sinceLastS, double distNm, bool wakeHoming, int left, double ttiS, bool frozen)
        {
            if (distNm >= RangeNm) return "range";
            if (wakeHoming) return "wake";
            if (left == 0) return "empty";
            bool close = distNm <= CloseNm;
            if ((!close || frozen) && !(ttiS <= FarMaxTtiS)) return "closing";
            if (!close && left >= 0 && left <= ReserveN) return "reserve";
            if (sinceLastS < (close ? CloseEveryS : FarEveryS)) return "interval";
            return close ? "close" : "far";
        }

        /// <summary>T-297: time to impact left for a frozen track: last measured tti minus the time since; +inf = none / not closing; &gt;= 0.</summary>
        public static double FrozenTti(double lastTtiS, double notedS, double nowS)
        {
            if (double.IsNaN(lastTtiS) || double.IsInfinity(lastTtiS) || double.IsNaN(notedS) || double.IsNaN(nowS)) return double.PositiveInfinity;
            return Math.Max(0, lastTtiS - (nowS - notedS));
        }

        /// <summary>T-301: the once-per-run log text of a decoy hold against a wake-homing torpedo. Pure.</summary>
        public static string WakeHoldText(int uid, int trackId, double distNm, string src)
        {
            return "[Evasion] evasion decoy hold uid=" + uid + " reason=wake trk=" + trackId + " dist=" + distNm.ToString("0.00", CultureInfo.InvariantCulture) + " src=" + src;
        }

        public static bool IsLaunch(string verdict) { return verdict == "close" || verdict == "far"; }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            if (Decide(999, 7, false, 20, 0) != "range") return "range 7";
            if (Decide(999, 6, false, 20, 0) != "range") return "range 6";
            if (Decide(999, 3, true, 20, 0) != "wake") return "wake";
            if (WakeHoldText(5, 7051, 1.234, "fresh") != "[Evasion] evasion decoy hold uid=5 reason=wake trk=7051 dist=1.23 src=fresh") return "wake text";
            if (Decide(999, 1, false, 0, 0) != "empty") return "empty";
            if (Decide(999, 3, false, 7, 0) != "far") return "far left 7";
            if (Decide(999, 3, false, 6, 0) != "reserve") return "far left 6";
            if (Decide(999, 3, false, -1, 0) != "far") return "far left unknown";
            if (Decide(59, 3, false, 20, 0) != "interval") return "far 59 s";
            if (Decide(60, 3, false, 20, 0) != "far") return "far 60 s";
            if (Decide(999, 1, false, 1, 0) != "close") return "close left 1";
            if (Decide(999, 1.5, false, 3, 0) != "close") return "close at 1.5";
            if (Decide(29, 1, false, 3, 0) != "interval") return "close 29 s";
            if (Decide(30, 1, false, 3, 0) != "close") return "close 30 s";
            if (Decide(999, 3.27, false, 19, 1329) != "closing") return "closing 7056";
            if (Decide(999, 3.35, false, 19, 1367) != "closing") return "closing 7049";
            if (Decide(999, 3.67, false, 18, 481) != "far") return "far 7050";
            if (Decide(999, 3, false, 20, 600) != "far") return "far tti 600";
            if (Decide(999, 3, false, 20, 601) != "closing") return "closing tti 601";
            if (Decide(999, 3, false, 20, double.PositiveInfinity) != "closing") return "closing inf";
            if (Decide(999, 1, false, 3, double.PositiveInfinity) != "close") return "close ignores tti";
            if (Decide(999, 3, false, 6, double.PositiveInfinity) != "closing") return "closing before reserve";
            if (Decide(999, 3, false, 6, 300) != "reserve") return "reserve tti 300";
            double inf = double.PositiveInfinity;
            if (Decide(999, 1.49, false, 19, inf, true) != "closing") return "frozen 7056 no tti";
            if (Decide(999, 1.2, false, 19, FrozenTti(1329, 0, 136), true) != "closing") return "frozen 7056 r21 1193";
            if (Decide(999, 1.2, false, 19, FrozenTti(300, 0, 100), true) != "close") return "frozen closing 200";
            if (Decide(999, 1.2, false, 19, 600, true) != "close") return "frozen tti 600";
            if (Decide(999, 1.2, false, 19, 601, true) != "closing") return "frozen tti 601";
            if (Decide(29, 1.2, false, 19, 200, true) != "interval") return "frozen interval";
            if (Decide(999, 1.2, true, 19, 200, true) != "wake") return "frozen wake";
            if (Decide(999, 1.2, false, 0, 200, true) != "empty") return "frozen empty";
            if (FrozenTti(300, 0, 400) != 0) return "FrozenTti passed";
            if (!double.IsPositiveInfinity(FrozenTti(inf, 0, 10)) || !double.IsPositiveInfinity(FrozenTti(double.NaN, 0, 10))) return "FrozenTti inf";
            var lt = new TrackLastTti();
            if (!double.IsPositiveInfinity(lt.Residual(8, 7056, 0))) return "lasttti none";
            lt.Note(8, 7056, 1329, 0);
            if (Math.Abs(lt.Residual(8, 7056, 136) - 1193) > 1e-6) return "lasttti 1193";
            if (!double.IsPositiveInfinity(lt.Residual(9, 7056, 136))) return "lasttti uid";
            lt.Note(8, 7056, inf, 140);
            if (!double.IsPositiveInfinity(lt.Residual(8, 7056, 150))) return "lasttti inf overrides";
            lt.Note(8, 7056, 300, 150);
            lt.Prune(new List<int>());
            if (lt.Count != 0 || !double.IsPositiveInfinity(lt.Residual(8, 7056, 151))) return "lasttti prune";
            lt.Note(8, 1, 5, 0); lt.Clear();
            if (lt.Count != 0) return "lasttti clear";
            var fr = new TrackFreshness();
            fr.Update(7056, 10, 10, 0);
            if (fr.MovedWithin(7056, 0, 8)) return "moved first";
            for (int s = 1; s <= 30; s++) fr.Update(7056, 10, 10, s);
            if (fr.MovedWithin(7056, 30, 8) || !fr.Stale(7056, 30)) return "moved static";
            fr.Update(7056, 10.0002, 10, 40);
            if (!fr.MovedWithin(7056, 45, 8) || fr.MovedWithin(7056, 49, 8)) return "moved window";
            fr.Update(7056, 10.0004, 10, 5);
            if (fr.MovedWithin(7056, 6, 8)) return "moved clock back";
            if (!IsLaunch("far") || !IsLaunch("close") || IsLaunch("reserve")) return "IsLaunch";

            // budget: 20 noisemakers, torpedo 5.9 -> 0.3 nm in 900 s (1 s tick), 600 s of it held at 3 nm
            int oldLeft = Budget(false, out _, out _);
            int atClose, closeLaunches;
            int newLeft = Budget(true, out atClose, out closeLaunches);
            Console.WriteLine("t286 budget old_left=" + oldLeft + " new_left=" + newLeft + " at_close=" + atClose);
            if (oldLeft != 0) return "old rule keeps " + oldLeft;
            if (atClose < ReserveN) return "reserve at 1.5 nm is " + atClose;
            if (closeLaunches < 4) return "close launches " + closeLaunches;
            return null;
        }

        private static int Budget(bool reserve, out int leftAtClose, out int closeLaunches)
        {
            int left = 20; double last = -1e9; leftAtClose = -1; closeLaunches = 0;
            for (int t = 0; t < 900; t++)
            {
                double d = t < 300 ? 5.9 - 2.9 * t / 300.0 : t < 900 - 300 ? 3.0 : 3.0 - 2.7 * (t - 600) / 300.0;
                if (leftAtClose < 0 && d <= CloseNm) leftAtClose = left;
                bool go;
                if (reserve) go = IsLaunch(Decide(t - last, d, false, left, 0));
                else go = d < RangeNm && left > 0 && t - last >= 30;
                if (!go) continue;
                left--; last = t;
                if (d <= CloseNm) closeLaunches++;
            }
            if (leftAtClose < 0) leftAtClose = left;
            return left;
        }
    }
    /// <summary>T-297: per (own uid, track) the last tti measured while the track mark was moving. Pure.</summary>
    internal sealed class TrackLastTti
    {
        private sealed class Rec { public double TtiS, NotedS; }
        private readonly Dictionary<long, Rec> _r = new Dictionary<long, Rec>();
        private static long Key(int uid, int trackId) { return ((long)uid << 32) | (uint)trackId; }
        public int Count { get { return _r.Count; } }

        /// <summary>Stores +inf too (overrides an older closing measurement).</summary>
        public void Note(int uid, int trackId, double ttiS, double nowS)
        {
            _r[Key(uid, trackId)] = new Rec { TtiS = ttiS, NotedS = nowS };
        }

        /// <summary>EvasionDecoyMath.FrozenTti of the last note, or +inf when unknown.</summary>
        public double Residual(int uid, int trackId, double nowS)
        {
            Rec r;
            return _r.TryGetValue(Key(uid, trackId), out r) ? EvasionDecoyMath.FrozenTti(r.TtiS, r.NotedS, nowS) : double.PositiveInfinity;
        }

        public void Prune(ICollection<int> liveTrackIds)
        {
            List<long> dead = null;
            foreach (long k in _r.Keys) if (liveTrackIds == null || !liveTrackIds.Contains((int)(uint)(k & 0xFFFFFFFFL))) (dead ?? (dead = new List<long>())).Add(k);
            if (dead != null) foreach (long k in dead) _r.Remove(k);
        }

        public void Clear() { _r.Clear(); }
    }
}
