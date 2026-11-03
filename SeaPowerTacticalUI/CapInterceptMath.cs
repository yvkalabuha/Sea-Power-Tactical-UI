// T-333 (ADR-0129) stream A: pure core of the zone intercept (no Unity, no reflection, selftest in the captest stand, section "icpt").
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Intercept settings of one patrol group (defaults = [Cap] keys; CapParams fields come with stream B, until then the defaults below).</summary>
    internal sealed class CapInterceptSpec
    {
        public bool On = true;
        public double MarginNm = 10;
        public int PerTarget = 2, Keep = 0;
        public string Targets = "air,helo";   // csv of air,helo,unknown
        public string Speed = "max";          // max | cruise
        public double LostS = 60;
        public CapInterceptSpec Clone() { return (CapInterceptSpec)MemberwiseClone(); }
        /// <summary>The [Cap] Intercept* defaults of a group.</summary>
        public static CapInterceptSpec FromParams(CapParams p)
        {
            if (p == null) return new CapInterceptSpec();
            return new CapInterceptSpec { On = p.Intercept, MarginNm = p.InterceptMarginNm, PerTarget = p.InterceptPerTarget, Keep = p.InterceptKeepOnStation,
                                          Targets = p.InterceptTargets, Speed = p.InterceptSpeed, LostS = p.InterceptLostS };
        }
    }

    /// <summary>A fit (or candidate) bird as the planner sees it. DashKn = speed of the planned dash (0 = unknown). Held / HeldRound: its current pair (-1 = none).</summary>
    internal sealed class CapIcptBird { public int Uid; public GeoPt Pos; public double DashKn; public int Held = -1, HeldRound; public int AiTarget = -1; public bool Fighting; }
    /// <summary>One assignment. Op: start | keep | reassign (From = the target it was taken from).</summary>
    internal sealed class CapIcptPair { public int Uid, Target, Round, From = -1; public double EtaS; public bool Lead; public string Op; }

    internal static class CapInterceptMath
    {
        public const double MinFuelS = 300, StallS = 30, GuideEveryS = 10, HandoverExtraNm = 5, EtaPenaltyS = 600, TailNm = 10, DefaultAamNm = 30;
        private const double FallbackKn = 400;

        private static bool CsvHas(string csv, string key)
        {
            if (string.IsNullOrEmpty(csv)) return false;
            foreach (string s in csv.Split(',')) if (string.Equals(s.Trim(), key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A hostile aircraft / helicopter by the csv, an unknown track only with "unknown"; a missile never.</summary>
        public static bool KindOk(CapContact c, string targetsCsv)
        {
            if (c == null || c.Missile) return false;
            if (c.Hostile) return CsvHas(targetsCsv, c.Helo ? "helo" : "air");
            if (c.Unknown) return CsvHas(targetsCsv, "unknown");
            return false;
        }

        public static bool InZone(CapContact c, CapZone z, double marginNm, string targets)
        {
            return KindOk(c, targets) && CapMath.DistToZoneNm(new GeoPt(c.Lat, c.Lon), z) <= marginNm + 1e-9;
        }

        /// <summary>null = fit; else the skip reason: leaving|uncontrollable|game|evading|game_zone|player|alert|missiles|fuel|strike.</summary>
        public static string Fit(CapBird b, bool alert, bool leaving, bool player, string fuelLowBy, double leaveInS, string strikeWhy, CapParams p)
        {
            if (leaving) return "leaving";
            if (!b.Controllable) return "uncontrollable";
            if (b.GameBingo || b.GameRtb || b.GameWinchester) return "game";
            if (b.Evading) return "evading";
            if (b.Where == "game_zone") return "game_zone";
            if (player) return "player";
            if (alert || b.Alert) return "alert";
            if (CapMath.MissileLeave(b, p) != null) return "missiles";
            if (fuelLowBy != null || leaveInS < MinFuelS) return "fuel";
            if (strikeWhy != null) return "strike";
            return null;
        }

        /// <summary>Time to meet the target at kn. Lead: dist(bird, ip) / kn; no lead: dist to the target / kn + EtaPenaltyS (ip = the target).</summary>
        public static double EtaS(GeoPt bird, double kn, CapContact t, out GeoPt ip, out bool lead)
        {
            if (!(kn > 1)) kn = FallbackKn;
            double rad = t.CourseDeg * Math.PI / 180.0, vx = t.SpeedKn * Math.Sin(rad), vy = t.SpeedKn * Math.Cos(rad), ix, iy;
            lead = CapMath.LeadPoint(GeoLon.DLon(bird.Lon, t.Lon) * 60.0 * AswMath.LonK(bird.Lat, t.Lat), (t.Lat - bird.Lat) * 60.0, vx, vy, kn, out ix, out iy);
            if (lead)
            {
                double ipLat = bird.Lat + iy / 60.0;   // T-362: K of the segment bird -> ip
                ip = new GeoPt(ipLat, bird.Lon + ix / (60.0 * AswMath.LonK(bird.Lat, ipLat)));
                return AswMath.DistNm(bird, ip) / kn * 3600.0;
            }
            ip = new GeoPt(t.Lat, t.Lon);
            return AswMath.DistNm(bird, ip) / kn * 3600.0 + EtaPenaltyS;
        }

        /// <summary>Most urgent first: distance to the zone, distance to its centre, id.</summary>
        public static List<CapContact> Order(IList<CapContact> targets, CapZone z)
        {
            var l = new List<CapContact>();
            if (targets != null) foreach (var c in targets) if (c != null) l.Add(c);
            GeoPt ctr = Center(z);
            l.Sort((a, b) =>
            {
                int r = CapMath.DistToZoneNm(new GeoPt(a.Lat, a.Lon), z).CompareTo(CapMath.DistToZoneNm(new GeoPt(b.Lat, b.Lon), z));
                if (r != 0) return r;
                r = AswMath.DistNm(ctr, new GeoPt(a.Lat, a.Lon)).CompareTo(AswMath.DistNm(ctr, new GeoPt(b.Lat, b.Lon)));
                return r != 0 ? r : a.Id.CompareTo(b.Id);
            });
            return l;
        }

        private static GeoPt Center(CapZone z)
        {
            if (z == null || z.Rect == null || z.Rect.Length < 4) return new GeoPt(0, 0);
            return new GeoPt((z.Rect[0] + z.Rect[2]) / 2, GeoLon.MidLon(z.Rect[1], z.Rect[3]));
        }

        private static double KnOf(CapIcptBird b) { return b.DashKn > 1 ? b.DashKn : FallbackKn; }

        /// <summary>Greedy by time to meet. Held pairs stay (sticky) while the bird is fit and the target still listed; claimed = birds other groups / alert raids / the game AI already count per target.
        /// Round 1 gives one bird per target (most urgent first), round 2.. the further ones up to PerTarget while more than Keep birds stay free.
        /// A new target without any bird and no free bird takes a round-2 bird of a doubled target (Op reassign).</summary>
        public static List<CapIcptPair> Assign(IList<CapContact> targets, CapZone z, IList<CapIcptBird> fit, IList<CapIcptPair> held, IDictionary<int, int> claimed, CapInterceptSpec s)
        {
            var res = new List<CapIcptPair>();
            if (s == null) s = new CapInterceptSpec();
            var ord = Order(targets, z);
            if (ord.Count == 0 || fit == null) return res;
            var byId = new Dictionary<int, CapContact>();
            foreach (var c in ord) byId[c.Id] = c;
            var fitByUid = new Dictionary<int, CapIcptBird>();
            foreach (var b in fit) if (b != null) fitByUid[b.Uid] = b;
            var count = new Dictionary<int, int>();
            foreach (var c in ord) { int cl; count[c.Id] = claimed != null && claimed.TryGetValue(c.Id, out cl) ? cl : 0; }
            var used = new HashSet<int>();
            if (held != null)
                foreach (var h in held)
                {
                    if (h == null || used.Contains(h.Uid) || !fitByUid.ContainsKey(h.Uid) || !byId.ContainsKey(h.Target)) continue;
                    used.Add(h.Uid); count[h.Target]++;
                    res.Add(new CapIcptPair { Uid = h.Uid, Target = h.Target, Round = h.Round, EtaS = h.EtaS, Lead = h.Lead, Op = "keep" });
                }
            var free = new List<CapIcptBird>();
            foreach (var b in fitByUid.Values) if (!used.Contains(b.Uid)) free.Add(b);
            free.Sort((a, b) => a.Uid.CompareTo(b.Uid));
            int per = Math.Max(1, s.PerTarget);
            for (int round = 1; round <= per; round++)
                foreach (var t in ord)
                {
                    if (count[t.Id] >= round) continue;
                    if (round > 1 && free.Count <= s.Keep) break;
                    if (free.Count == 0)
                    {
                        if (round == 1 && count[t.Id] == 0) TryReassign(res, t, fitByUid, count);
                        continue;
                    }
                    CapIcptBird best = null; double bestEta = 0, e; GeoPt ip, bip = default(GeoPt); bool lead, blead = false;
                    foreach (var b in free)
                    {
                        e = EtaS(b.Pos, KnOf(b), t, out ip, out lead);
                        if (best == null || e < bestEta - 1e-9) { best = b; bestEta = e; bip = ip; blead = lead; }
                    }
                    free.Remove(best); count[t.Id]++;
                    res.Add(new CapIcptPair { Uid = best.Uid, Target = t.Id, Round = round, EtaS = bestEta, Lead = blead, Op = "start" });
                }
            return res;
        }

        /// <summary>No free bird: the round-2 bird of a doubled target (two or more of ours) that reaches the new target soonest is taken over.</summary>
        private static void TryReassign(List<CapIcptPair> res, CapContact t, Dictionary<int, CapIcptBird> fitByUid, Dictionary<int, int> count)
        {
            var ours = new Dictionary<int, int>();
            foreach (var p in res) { int n; ours.TryGetValue(p.Target, out n); ours[p.Target] = n + 1; }
            CapIcptPair pick = null; double pickEta = 0, e; GeoPt ip, pip = default(GeoPt); bool lead, plead = false;
            foreach (var p in res)
            {
                if (p.Round < 2 || ours[p.Target] < 2) continue;
                var b = fitByUid[p.Uid];
                e = EtaS(b.Pos, KnOf(b), t, out ip, out lead);
                if (pick == null || e < pickEta - 1e-9 || (Math.Abs(e - pickEta) <= 1e-9 && p.Uid < pick.Uid)) { pick = p; pickEta = e; pip = ip; plead = lead; }
            }
            if (pick == null) return;
            count[pick.Target]--; count[t.Id]++;
            res.Remove(pick);
            res.Add(new CapIcptPair { Uid = pick.Uid, Target = t.Id, Round = 1, From = pick.Target, EtaS = pickEta, Lead = plead, Op = "reassign" });
        }

        /// <summary>null = continue; killed | lost | beyond_pursuit | leash. gone: "destroyed" | "lost" | null (the game track state). lostSince &lt; 0 = the track is there.
        /// (missiles / fuel / game_* come from Fit / LeaveReason in the controller.)</summary>
        public static string End(CapContact tgtOrNull, string gone, double lostSince, double now, GeoPt birdPos, CapZone z, CapInterceptSpec s, CapParams p)
        {
            double pursuit = p == null ? 25 : p.PursuitNm;
            if (gone == "destroyed") return "killed";
            if (tgtOrNull == null)
            {
                double lost = s == null ? 60 : s.LostS;
                if (lostSince >= 0 && now - lostSince >= lost - 1e-9) return "lost";
            }
            else if (CapMath.DistToZoneNm(new GeoPt(tgtOrNull.Lat, tgtOrNull.Lon), z) > pursuit) return "beyond_pursuit";
            if (CapMath.DistToZoneNm(birdPos, z) > pursuit) return "leash";
            return null;
        }

        /// <summary>Two points: the intercept point and a tail TailNm further along bird -> ip (so the bird does not stop and spam "waypoint reached").</summary>
        public static List<GeoPt> GuideRoute(GeoPt pos, CapContact t, double kn, out GeoPt ip, out bool lead)
        {
            EtaS(pos, kn, t, out ip, out lead);
            double brg = AswMath.DistNm(pos, ip) > 1e-6 ? AswMath.BearingDeg(pos, ip) : t.CourseDeg;
            return new List<GeoPt> { ip, AswMath.Offset(ip, brg, TailNm) };
        }

        // ---------- selftest ----------
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static GeoPt At(double northNm, double eastNm) { return new GeoPt(10.5 + northNm / 60.0, 11.0 + eastNm / 60.0); }   // zone south-west corner = (0,0)
        private static CapContact Ct(int id, double northNm, double eastNm, bool helo = false, double crs = 0, double kn = 0)
        {
            var g = At(northNm, eastNm);
            return new CapContact { Id = id, Lat = g.Lat, Lon = g.Lon, Hostile = true, Helo = helo, CourseDeg = crs, SpeedKn = kn };
        }
        private static CapIcptBird Bd(int uid, double northNm, double eastNm) { return new CapIcptBird { Uid = uid, Pos = At(northNm, eastNm), DashKn = 600 }; }
        private static int Cnt(List<CapIcptPair> l, int t, int round = 0) { int n = 0; foreach (var p in l) if (p.Target == t && (round == 0 || p.Round == round)) n++; return n; }
        private static CapIcptPair Of(List<CapIcptPair> l, int uid) { foreach (var p in l) if (p.Uid == uid) return p; return null; }

        public static string SelfTest()
        {
            _err = null;
            var z = new CapZone { Rect = new[] { 10.5, 11.0, 10.5 + 20 / 60.0, 11.0 + 30 / 60.0 } };   // 20 nm north x 30 nm east, corner (0,0)
            var p = new CapParams();
            // entry
            True("in 9.9", InZone(Ct(1, 20 + 9.9, 10), z, 10, "air,helo"));
            True("in 10.1", !InZone(Ct(1, 20 + 10.1, 10), z, 10, "air,helo"));
            True("margin 0 inside", InZone(Ct(1, 10, 10), z, 0, "air") && !InZone(Ct(1, 20.5, 10), z, 0, "air"));
            var msl = Ct(2, 10, 10); msl.Missile = true;
            True("missile never", !KindOk(msl, "air,helo,unknown"));
            True("helo by helo", KindOk(Ct(3, 10, 10, true), "air,helo") && !KindOk(Ct(3, 10, 10, true), "air"));
            var unk = Ct(4, 10, 10); unk.Hostile = false; unk.Unknown = true;
            True("unknown only with unknown", !KindOk(unk, "air,helo") && KindOk(unk, "air,unknown"));
            var friend = Ct(5, 10, 10); friend.Hostile = false;
            True("not hostile", !KindOk(friend, "air,helo,unknown"));
            // fit
            var ok = new CapBird { AamStart = 8, AamNow = 8 };
            True("fit ok", Fit(ok, false, false, false, null, 1e9, null, p) == null);
            True("fit missiles 1/8", Fit(new CapBird { AamStart = 8, AamNow = 1 }, false, false, false, null, 1e9, null, p) == "missiles");
            True("fit missiles 2/8 ok", Fit(new CapBird { AamStart = 8, AamNow = 2 }, false, false, false, null, 1e9, null, p) == null);
            True("fit fuel by", Fit(ok, false, false, false, "margin", 1e9, null, p) == "fuel");
            True("fit leave 299", Fit(ok, false, false, false, null, 299, null, p) == "fuel");
            True("fit leave 301", Fit(ok, false, false, false, null, 301, null, p) == null);
            True("fit strike", Fit(ok, false, false, false, null, 1e9, "strike", p) == "strike");
            True("fit leaving", Fit(ok, false, true, false, null, 1e9, null, p) == "leaving");
            True("fit evading", Fit(new CapBird { Evading = true }, false, false, false, null, 1e9, null, p) == "evading");
            True("fit alert", Fit(ok, true, false, false, null, 1e9, null, p) == "alert");
            True("fit player", Fit(ok, false, false, true, null, 1e9, null, p) == "player");
            True("fit game_zone", Fit(new CapBird { Where = "game_zone" }, false, false, false, null, 1e9, null, p) == "game_zone");
            True("fit game", Fit(new CapBird { GameBingo = true }, false, false, false, null, 1e9, null, p) == "game");
            True("fit uncontrollable", Fit(new CapBird { Controllable = false }, false, false, false, null, 1e9, null, p) == "uncontrollable");
            True("fit leaving first", Fit(new CapBird { Evading = true }, false, true, true, "floor", 0, "s", p) == "leaving");
            // eta, order
            GeoPt ip; bool lead;
            double e = EtaS(At(0, 0), 600, Ct(1, 100, 0, false, 180, 600), out ip, out lead);   // head-on, 100 nm: meet at 50 nm
            True("eta head-on lead", lead && Math.Abs(e - 300) < 1 && Math.Abs(AswMath.DistNm(At(0, 0), ip) - 50) < 0.1);
            e = EtaS(At(0, 0), 400, Ct(1, 100, 0, false, 0, 600), out ip, out lead);   // runs away faster
            True("eta no lead penalty", !lead && Math.Abs(e - (100 / 400.0 * 3600 + EtaPenaltyS)) < 1 && Math.Abs(AswMath.DistNm(At(100, 0), ip)) < 1e-6);
            var ord = Order(new List<CapContact> { Ct(7, 20 + 8, 5), Ct(3, 20 + 8, 5), Ct(9, 10, 10) }, z);
            True("order zone first, ties by id", ord[0].Id == 9 && ord[1].Id == 3 && ord[2].Id == 7);
            // assign: 2 targets x 4 birds
            var s = new CapInterceptSpec();
            var tg = new List<CapContact> { Ct(10, 25, 10), Ct(11, 28, 20) };
            var birds = new List<CapIcptBird> { Bd(1, 0, 0), Bd(2, 0, 10), Bd(3, 0, 20), Bd(4, 0, 30) };
            var r = Assign(tg, z, birds, null, null, s);
            True("2x4 round 1 count", r.Count == 4 && Cnt(r, 10, 1) == 1 && Cnt(r, 11, 1) == 1 && Cnt(r, 10, 2) == 1 && Cnt(r, 11, 2) == 1);
            var r1 = Assign(tg, z, new List<CapIcptBird> { Bd(1, 0, 0), Bd(2, 0, 30) }, null, null, s);
            True("2x2 one each", r1.Count == 2 && Cnt(r1, 10) == 1 && Cnt(r1, 11) == 1);
            True("round 1 by smaller eta", Of(r1, 1).Target == 10 && Of(r1, 2).Target == 11);
            // one target x 4: two on it, two free
            var r2 = Assign(new List<CapContact> { Ct(10, 25, 10) }, z, birds, null, null, s);
            True("1x4 two on target", r2.Count == 2 && Cnt(r2, 10, 1) == 1 && Cnt(r2, 10, 2) == 1);
            // 3 targets x 2 birds: two most urgent
            var tg3 = new List<CapContact> { Ct(20, 40, 10), Ct(21, 25, 10), Ct(22, 22, 10) };
            var r3 = Assign(tg3, z, new List<CapIcptBird> { Bd(1, 0, 0), Bd(2, 0, 5) }, null, null, s);
            True("3x2 most urgent", r3.Count == 2 && Cnt(r3, 22) == 1 && Cnt(r3, 21) == 1 && Cnt(r3, 20) == 0);
            // keep 2
            var sk = s.Clone(); sk.Keep = 2;
            var rk = Assign(new List<CapContact> { Ct(10, 25, 10) }, z, birds, null, null, sk);
            True("keep 2 of 4", rk.Count == 2);
            sk.Keep = 3;
            rk = Assign(new List<CapContact> { Ct(10, 25, 10) }, z, birds, null, null, sk);
            True("keep 3 of 4: round 1 only", rk.Count == 1 && rk[0].Round == 1);
            // claimed
            var rc = Assign(new List<CapContact> { Ct(10, 25, 10) }, z, birds, null, new Dictionary<int, int> { { 10, 2 } }, s);
            True("claimed 2 -> none", rc.Count == 0);
            rc = Assign(new List<CapContact> { Ct(10, 25, 10) }, z, birds, null, new Dictionary<int, int> { { 10, 1 } }, s);
            True("claimed 1 -> one", rc.Count == 1 && rc[0].Round == 2);
            // sticky
            var held = new List<CapIcptPair> { new CapIcptPair { Uid = 4, Target = 10, Round = 1, Op = "start" } };
            var rs = Assign(tg, z, birds, held, null, s);
            True("sticky kept", Of(rs, 4).Op == "keep" && Of(rs, 4).Target == 10 && Of(rs, 4).Round == 1);
            tg = new List<CapContact> { Ct(10, 25, 10), Ct(11, 28, 20) };
            var moved = new List<CapContact> { Ct(10, 25, 12), Ct(11, 28, 18) };
            var rs2 = Assign(moved, z, birds, rs, null, s);
            True("sticky after move", Of(rs2, 4).Op == "keep" && Of(rs2, 4).Target == 10 && rs2.Count == 4);
            var gone = Assign(new List<CapContact> { Ct(11, 28, 20) }, z, birds, rs, null, s);
            True("held target gone -> dropped", Cnt(gone, 10) == 0 && Cnt(gone, 11) == 2);
            var unfit = Assign(tg, z, new List<CapIcptBird> { Bd(1, 0, 0), Bd(2, 0, 10) }, rs, null, s);
            True("held bird unfit -> dropped", Of(unfit, 4) == null && unfit.Count == 2);
            // reassign: doubled pair on target 10, new target 12 appears, no free bird
            var two = new List<CapIcptBird> { Bd(1, 0, 0), Bd(2, 0, 10) };
            var held2 = new List<CapIcptPair> { new CapIcptPair { Uid = 1, Target = 10, Round = 1, Op = "start" }, new CapIcptPair { Uid = 2, Target = 10, Round = 2, Op = "start" } };
            var rr = Assign(new List<CapContact> { Ct(10, 25, 10), Ct(12, 24, 30) }, z, two, held2, null, s);
            var mv = Of(rr, 2);
            True("reassign", rr.Count == 2 && Of(rr, 1).Op == "keep" && mv.Op == "reassign" && mv.Target == 12 && mv.From == 10 && mv.Round == 1);
            // ties by uid
            var tie = Assign(new List<CapContact> { Ct(10, 25, 10) }, z, new List<CapIcptBird> { Bd(6, 0, 10), Bd(5, 0, 10) }, null, null, s);
            True("tie by uid", Of(tie, 5).Round == 1 && Of(tie, 6).Round == 2);
            // end
            var tc = Ct(10, 25, 10);
            True("end none", End(tc, null, -1, 100, At(5, 5), z, s, p) == null);
            True("end killed", End(tc, "destroyed", -1, 100, At(5, 5), z, s, p) == "killed");
            True("end killed track gone", End(null, "destroyed", 90, 100, At(5, 5), z, s, p) == "killed");
            True("end lost 59", End(null, "lost", 41, 100, At(5, 5), z, s, p) == null);
            True("end lost 61", End(null, "lost", 39, 100, At(5, 5), z, s, p) == "lost");
            True("end pursuit 24.9", End(Ct(10, 20 + 24.9, 10), null, -1, 0, At(5, 5), z, s, p) == null);
            True("end pursuit 25.1", End(Ct(10, 20 + 25.1, 10), null, -1, 0, At(5, 5), z, s, p) == "beyond_pursuit");
            True("end leash 24.9", End(tc, null, -1, 0, At(20 + 24.9, 10), z, s, p) == null);
            True("end leash 25.1", End(tc, null, -1, 0, At(20 + 25.1, 10), z, s, p) == "leash");
            // guide route
            var route = GuideRoute(At(0, 0), Ct(1, 100, 0, false, 180, 600), 600, out ip, out lead);
            True("route 2 pts", route.Count == 2 && lead && Math.Abs(AswMath.DistNm(route[0], route[1]) - TailNm) < 0.01
                && Math.Abs(AswMath.DistNm(At(0, 0), route[1]) - (AswMath.DistNm(At(0, 0), route[0]) + TailNm)) < 0.01);
            return _err;
        }
    }
}
