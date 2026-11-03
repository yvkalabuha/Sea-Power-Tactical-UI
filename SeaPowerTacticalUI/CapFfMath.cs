// T-333 (ADR-0129 section 9) stream E1: pure core of the friendly-fire guard for the auto attacks of CAP aircraft (no Unity, no reflection; selftest section "ff").
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>One auto-attack attempt as read from the game's EngageTask. Seeker: ir|arh|sarh|cmd|null. Others = positions of other hostile air tracks the shooter could take instead (Q3: shortens the hold).</summary>
    internal sealed class FfShot
    {
        public int Shooter; public bool Auto, CapAir; public bool TargetAir; public int TargetKey; public GeoPt From, To; public string Ammo, Seeker;
        public IList<GeoPt> Others;
    }
    /// <summary>An own / allied aircraft or helicopter in the air (no ships).</summary>
    internal sealed class FfFriend { public int Uid; public GeoPt Pos; }
    /// <summary>An air-to-air missile on board: the safe-seeker ones (sarh, cmd) can replace an ir / arh one.</summary>
    internal sealed class FfAlt { public string Ammo, Seeker; public double MaxNm; public int Left; }
    /// <summary>Op: allow|hold|swap; Why (on the first allow after a hold): clear|timeout; Log = write one line for this decision.</summary>
    internal struct FfDecision { public string Op, Why, SwapTo; public int Friend; public double FrNm, ConeDeg, HeldS; public bool Log; }

    /// <summary>Memory per (shooter, target): when the hold began, when it was released by timeout, whether the hold line was logged.</summary>
    internal sealed class FfBook
    {
        private sealed class Memo { public double FirstHold = -1, ReleasedAt = -1, LastSeen; public bool Logged; public int Friend; }
        private readonly Dictionary<long, Memo> _m = new Dictionary<long, Memo>();
        private static long Key(int shooter, int target) { return ((long)shooter << 32) ^ (uint)target; }

        private static bool Safe(string seeker, bool _) { return seeker == "sarh" || seeker == "cmd"; }

        public FfDecision Decide(FfShot s, IList<FfFriend> friends, IList<FfAlt> alts, bool swapOn, double now)
        {
            var d = new FfDecision { Op = "allow", Friend = -1 };
            if (s == null || !s.Auto || !s.CapAir || !s.TargetAir || (s.Seeker != "ir" && s.Seeker != "arh")) return d;
            long k = Key(s.Shooter, s.TargetKey);
            Memo m; _m.TryGetValue(k, out m);
            FfFriend fr = null; double frNm = 0, cone = 0;
            if (friends != null)
                foreach (var f in friends)
                {
                    if (f == null || f.Uid == s.Shooter) continue;
                    double n, c;
                    if (!CapFfMath.InCone(s.From, s.To, f.Pos, out n, out c)) continue;
                    if (fr == null || n < frNm) { fr = f; frNm = n; cone = c; }
                }
            if (fr == null)
            {
                if (m != null)
                {
                    bool wasHold = m.FirstHold >= 0 && m.ReleasedAt < 0;
                    double heldS = wasHold ? now - m.FirstHold : 0;
                    _m.Remove(k);   // the friend left the cone: a new approach starts a fresh hold (and ends a cooldown)
                    if (wasHold) { d.Why = "clear"; d.HeldS = heldS; d.Log = true; }
                }
                return d;
            }
            if (m == null) { m = new Memo(); _m[k] = m; }
            m.LastSeen = now; m.Friend = fr.Uid;
            d.Friend = fr.Uid; d.FrNm = frNm; d.ConeDeg = cone;
            if (m.ReleasedAt >= 0)
            {
                if (now - m.ReleasedAt < CapFfMath.CooldownS && now >= m.ReleasedAt) { d.HeldS = now - m.FirstHold; return d; }
                m.FirstHold = -1; m.ReleasedAt = -1; m.Logged = false;   // cooldown over: hold again
            }
            if (swapOn && alts != null)
            {
                double dist = AswMath.DistNm(s.From, s.To);
                FfAlt pick = null;
                foreach (var a in alts)
                {
                    if (a == null || !Safe(a.Seeker, true) || a.Left < 1 || a.MaxNm < dist || a.Ammo == s.Ammo) continue;
                    if (pick == null || (pick.Seeker != "cmd" && a.Seeker == "cmd")) pick = a;
                }
                if (pick != null) { d.Op = "swap"; d.SwapTo = pick.Ammo; d.Log = true; return d; }
            }
            if (m.FirstHold < 0) m.FirstHold = now;
            double held = now - m.FirstHold;
            d.HeldS = held;
            double limit = OtherClear(s, friends) ? CapFfMath.HoldShortS : CapFfMath.HoldMaxS;
            if (held >= limit)
            {
                m.ReleasedAt = now; d.Op = "allow"; d.Why = "timeout"; d.Log = true;
                return d;
            }
            d.Op = "hold";
            if (!m.Logged) { m.Logged = true; d.Log = true; }
            return d;
        }

        /// <summary>Q3: another hostile air track exists whose line from the shooter has no friend in the cone: the AI may shoot it, so the hold is short.</summary>
        private static bool OtherClear(FfShot s, IList<FfFriend> friends)
        {
            if (s.Others == null) return false;
            foreach (var o in s.Others)
            {
                bool blocked = false;
                if (friends != null)
                    foreach (var f in friends)
                    {
                        double n, c;
                        if (f != null && f.Uid != s.Shooter && CapFfMath.InCone(s.From, o, f.Pos, out n, out c)) { blocked = true; break; }
                    }
                if (!blocked) return true;
            }
            return false;
        }

        /// <summary>shooter uid -> seconds held now (for the window / cap_state). Entries not refreshed for 2.5 s are not holding any more; old memos are dropped.</summary>
        public List<KeyValuePair<int, double>> Holding(double now)
        {
            var l = new List<KeyValuePair<int, double>>();
            List<long> drop = null;
            foreach (var kv in _m)
            {
                var m = kv.Value;
                if (now - m.LastSeen > 2 * CapFfMath.CooldownS || now < m.LastSeen) { if (drop == null) drop = new List<long>(); drop.Add(kv.Key); continue; }
                if (m.FirstHold >= 0 && m.ReleasedAt < 0 && now - m.LastSeen <= 2.5) l.Add(new KeyValuePair<int, double>((int)(kv.Key >> 32), now - m.FirstHold));
            }
            if (drop != null) foreach (long k in drop) _m.Remove(k);
            l.Sort((a, b) => a.Key.CompareTo(b.Key));
            return l;
        }

        public void Clear() { _m.Clear(); }
    }

    internal static class CapFfMath
    {
        public const double RadiusNm = 2, ConeDeg = 10, HoldMaxS = 10, HoldShortS = 5, CooldownS = 60;
        /// <summary>R: swapping the missile is only safe with both fields (_ammoId, _ammunition) and no guiding alignment; switched on after the stand and the live check (T-163).</summary>
        public static bool SwapEnabled = false;

        /// <summary>"IRHoming"->ir, "ActiveRadarHoming"->arh, "SemiActiveRadarHoming"->sarh, "None" with RadioCommand|Datalink -> cmd, else null (fire stays allowed).</summary>
        public static string SeekerOf(string guidanceType, string midCourse)
        {
            if (guidanceType == null) return null;
            string g = guidanceType.Trim();
            if (string.Equals(g, "IRHoming", StringComparison.OrdinalIgnoreCase)) return "ir";
            if (string.Equals(g, "ActiveRadarHoming", StringComparison.OrdinalIgnoreCase)) return "arh";
            if (string.Equals(g, "SemiActiveRadarHoming", StringComparison.OrdinalIgnoreCase)) return "sarh";
            if (string.Equals(g, "None", StringComparison.OrdinalIgnoreCase) && midCourse != null
                && (string.Equals(midCourse.Trim(), "RadioCommand", StringComparison.OrdinalIgnoreCase) || string.Equals(midCourse.Trim(), "Datalink", StringComparison.OrdinalIgnoreCase))) return "cmd";
            return null;
        }

        /// <summary>The friend is within RadiusNm of the target, within ConeDeg of the shooter -> target line, and not further from the shooter than target + RadiusNm.</summary>
        public static bool InCone(GeoPt shooter, GeoPt target, GeoPt friend, out double frNm, out double coneDeg)
        {
            frNm = AswMath.DistNm(friend, target);
            double dt = AswMath.DistNm(shooter, target), df = AswMath.DistNm(shooter, friend);
            if (dt < 1e-9 || df < 1e-9) coneDeg = 0;
            else
            {
                double diff = Math.Abs(AswMath.BearingDeg(shooter, target) - AswMath.BearingDeg(shooter, friend)) % 360.0;
                coneDeg = diff > 180 ? 360 - diff : diff;
            }
            return frNm <= RadiusNm + 1e-9 && coneDeg <= ConeDeg + 1e-9 && df <= dt + RadiusNm + 1e-9;
        }

        // ---------- selftest ----------
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static GeoPt At(double eastNm, double northNm) { return new GeoPt(10.5 + northNm / 60.0, 11.0 + eastNm / 60.0); }
        private static FfShot Shot(string seeker, double shooterNorth = 0, double targetNorth = 10)
        {
            return new FfShot { Shooter = 1, Auto = true, CapAir = true, TargetAir = true, TargetKey = 7, From = At(0, shooterNorth), To = At(0, targetNorth), Ammo = "aim9", Seeker = seeker };
        }
        private static IList<FfFriend> Fr(double east, double north) { return new List<FfFriend> { new FfFriend { Uid = 2, Pos = At(east, north) } }; }
        private static FfAlt Alt(string ammo, string seeker, double nm, int left) { return new FfAlt { Ammo = ammo, Seeker = seeker, MaxNm = nm, Left = left }; }

        public static string SelfTest()
        {
            _err = null;
            bool keep = SwapEnabled;
            double n, c;
            // geometry
            True("cone in", InCone(At(0, 0), At(0, 10), At(1.4, 10), out n, out c) && Math.Abs(n - 1.4) < 1e-6 && c > 7.9 && c < 8.1);
            True("cone 12 deg", !InCone(At(0, 0), At(0, 5), At(1.2, 5), out n, out c) && c > 13 && c < 14);
            True("cone 2.1 nm", !InCone(At(0, 0), At(0, 10), At(0, 12.1), out n, out c) && n > 2.09);
            True("cone behind shooter", !InCone(At(0, 0), At(0, 10), At(0, -1), out n, out c) && c > 179);
            True("cone friend before target", InCone(At(0, 0), At(0, 10), At(0, 8.5), out n, out c) && Math.Abs(n - 1.5) < 1e-6);
            // seekers
            True("seeker ir", SeekerOf("IRHoming", "None") == "ir");
            True("seeker arh", SeekerOf("ActiveRadarHoming", "Datalink") == "arh");
            True("seeker sarh", SeekerOf("SemiActiveRadarHoming", null) == "sarh");
            True("seeker cmd radio", SeekerOf("None", "RadioCommand") == "cmd");
            True("seeker cmd datalink", SeekerOf("None", "Datalink") == "cmd");
            True("seeker none", SeekerOf("None", "None") == null && SeekerOf("None", null) == null && SeekerOf(null, null) == null && SeekerOf("Other", "Datalink") == null);
            // hold
            var b = new FfBook();
            var d = b.Decide(Shot("ir"), Fr(1.0, 11.1), null, false, 100);   // 1.5 nm from the target, ~5 deg
            True("hold ir", d.Op == "hold" && d.Friend == 2 && d.Log && d.FrNm < 2 && d.ConeDeg <= 10);
            d = b.Decide(Shot("ir"), Fr(1.0, 11.1), null, false, 101);
            True("hold ir quiet", d.Op == "hold" && !d.Log);
            True("hold arh", new FfBook().Decide(Shot("arh"), Fr(1.0, 11.1), null, false, 0).Op == "hold");
            // allow
            True("allow outside cone", new FfBook().Decide(Shot("ir", 0, 5), Fr(1.2, 5), null, false, 0).Op == "allow");
            True("allow far", new FfBook().Decide(Shot("ir"), Fr(0, 12.1), null, false, 0).Op == "allow");
            True("allow behind shooter", new FfBook().Decide(Shot("ir"), Fr(0, -3), null, false, 0).Op == "allow");
            True("allow sarh", new FfBook().Decide(Shot("sarh"), Fr(1.0, 11.1), null, false, 0).Op == "allow");
            True("allow cmd", new FfBook().Decide(Shot("cmd"), Fr(1.0, 11.1), null, false, 0).Op == "allow");
            True("allow null seeker", new FfBook().Decide(Shot(null), Fr(1.0, 11.1), null, false, 0).Op == "allow");
            var sh = Shot("ir"); sh.Auto = false;
            True("allow not auto", new FfBook().Decide(sh, Fr(1.0, 11.1), null, false, 0).Op == "allow");
            sh = Shot("ir"); sh.CapAir = false;
            True("allow not cap", new FfBook().Decide(sh, Fr(1.0, 11.1), null, false, 0).Op == "allow");
            sh = Shot("ir"); sh.TargetAir = false;
            True("allow not air target", new FfBook().Decide(sh, Fr(1.0, 11.1), null, false, 0).Op == "allow");
            True("allow no friends", new FfBook().Decide(Shot("ir"), null, null, false, 0).Op == "allow");
            True("allow friend is shooter", new FfBook().Decide(Shot("ir"), new List<FfFriend> { new FfFriend { Uid = 1, Pos = At(0, 10) } }, null, false, 0).Op == "allow");
            // limit 10 s, timeout, cooldown
            b = new FfBook();
            var fr = Fr(1.0, 11.1);
            b.Decide(Shot("ir"), fr, null, false, 100);
            d = b.Decide(Shot("ir"), fr, null, false, 109.9);
            True("hold at 9.9", d.Op == "hold" && Math.Abs(d.HeldS - 9.9) < 1e-6);
            True("holding list", b.Holding(110).Count == 1 && b.Holding(110)[0].Key == 1);
            d = b.Decide(Shot("ir"), fr, null, false, 110.1);
            True("timeout at 10.1", d.Op == "allow" && d.Why == "timeout" && d.Log);
            d = b.Decide(Shot("ir"), fr, null, false, 111);
            True("cooldown allow quiet", d.Op == "allow" && d.Why == null && !d.Log);
            True("not holding in cooldown", b.Holding(111).Count == 0);
            d = b.Decide(Shot("ir"), fr, null, false, 170);
            True("cooldown 59.9", d.Op == "allow");
            d = b.Decide(Shot("ir"), fr, null, false, 171);
            True("hold again after 60 s", d.Op == "hold" && d.Log && d.HeldS < 1e-6);
            // Q3: another hostile outside the cone shortens the hold to 5 s
            b = new FfBook();
            sh = Shot("ir"); sh.Others = new List<GeoPt> { At(20, 10) };
            b.Decide(sh, fr, null, false, 0);
            True("short hold 4.9", b.Decide(sh, fr, null, false, 4.9).Op == "hold");
            d = b.Decide(sh, fr, null, false, 5.1);
            True("short hold 5.1", d.Op == "allow" && d.Why == "timeout");
            b = new FfBook();
            sh = Shot("ir"); sh.Others = new List<GeoPt> { At(0.5, 10.2) };   // the other one is covered by the same friend: no shortening
            b.Decide(sh, fr, null, false, 0);
            True("other in cone keeps 10 s", b.Decide(sh, fr, null, false, 6).Op == "hold");
            // clear
            b = new FfBook();
            b.Decide(Shot("ir"), fr, null, false, 0);
            d = b.Decide(Shot("ir"), Fr(5, 11), null, false, 3);
            True("clear", d.Op == "allow" && d.Why == "clear" && d.Log && Math.Abs(d.HeldS - 3) < 1e-6);
            d = b.Decide(Shot("ir"), Fr(5, 11), null, false, 4);
            True("clear once", d.Op == "allow" && d.Why == null && !d.Log);
            d = b.Decide(Shot("ir"), fr, null, false, 5);
            True("hold restarts after clear", d.Op == "hold" && d.HeldS < 1e-6 && d.Log);
            // swap
            b = new FfBook();
            var alts = new List<FfAlt> { Alt("aim7", "sarh", 30, 2) };
            d = b.Decide(Shot("ir"), fr, alts, true, 0);
            True("swap", d.Op == "swap" && d.SwapTo == "aim7" && d.Log);
            True("swap off", new FfBook().Decide(Shot("ir"), fr, alts, false, 0).Op == "hold");
            True("swap out of range", new FfBook().Decide(Shot("ir"), fr, new List<FfAlt> { Alt("aim7", "sarh", 5, 2) }, true, 0).Op == "hold");
            True("swap none left", new FfBook().Decide(Shot("ir"), fr, new List<FfAlt> { Alt("aim7", "sarh", 30, 0) }, true, 0).Op == "hold");
            True("swap not a safe seeker", new FfBook().Decide(Shot("ir"), fr, new List<FfAlt> { Alt("aim9x", "ir", 30, 3), Alt("amraam", "arh", 30, 3) }, true, 0).Op == "hold");
            d = new FfBook().Decide(Shot("ir"), fr, new List<FfAlt> { Alt("aim7", "sarh", 30, 2), Alt("sparrowc", "cmd", 30, 2) }, true, 0);
            True("swap prefers cmd", d.Op == "swap" && d.SwapTo == "sparrowc");
            True("SwapEnabled default off", !keep);
            // clear all
            b = new FfBook(); b.Decide(Shot("ir"), fr, null, false, 0); b.Clear();
            True("clear book", b.Holding(1).Count == 0);
            return _err;
        }
    }
}
