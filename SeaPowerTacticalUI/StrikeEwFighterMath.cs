using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase D (ADR-0133 section 6, plan 33-35): the jam of hostile fighters by the jammer aircraft. Pure arithmetic (no game, no Unity): which tracks are a threat to the strike group, which jammer takes which one
    // (one cone per aircraft, the fighters in the same cone are covered on the way), where a jammer may stand (R_t + a margin from every fighter, outside the SAM zones), and when a jammer may jam fighters at all.
    // Closing speed is the shared helper AswMath.RadialClosingKn (T-347, the metric of the game world); EtaS is the formula of AirGuardMath.EtaS, copied here so that the stand needs no AirGuard file.
    internal static class StrikeEwFighterMath
    {
        private static GeoPt Pos(StrikeFighter f) { return new GeoPt(f.Lat, f.Lon); }
        private static double Wrap180(double a) { a = AswMath.Norm360(a); return a > 180 ? a - 360 : a; }
        private static string I(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        /// <summary>An identified hostile air track of a fighter class that is not a helicopter and has a missile radius.</summary>
        public static bool Eligible(StrikeFighter f)
        {
            return f != null && f.Identified && f.Hostile && !f.Helicopter && f.IsFighter && f.RNm > 0;
        }

        /// <summary>The radial speed of the track itself towards a point, kn (&gt; 0 = approaching). The same helper as AirGuardMath.TrackClosingKn.</summary>
        public static double ClosingKn(StrikeFighter f, GeoPt p)
        {
            double c = f.CourseDeg * Math.PI / 180.0;
            double tn = f.SpeedKn * Math.Cos(c), te = f.SpeedKn * Math.Sin(c);
            return AswMath.RadialClosingKn(Pos(f), p, tn, te, f.Globe, Math.Max(f.SpeedKn, 1));
        }

        /// <summary>Seconds until the track is inside rNm (AirGuardMath.EtaS): 0 inside, +inf when it does not approach.</summary>
        public static double EtaS(double dNm, double rNm, double closingKn)
        {
            if (closingKn <= 0) return dNm <= rNm ? 0 : double.PositiveInfinity;
            if (dNm <= rNm) return 0;
            return (dNm - rNm) / closingKn * 3600.0;
        }

        /// <summary>The threats to the strikers (no memory): the entry rule only.</summary>
        public static List<StrikeFighterThreat> Threats(IList<StrikeFighter> fs, IList<StrikeAircraft> strikers, StrikeParams p) { return Threats(fs, strikers, p, null, 0); }

        /// <summary>The threats to the strikers: EtaS to the radius R_t of the track against the nearest striker is at most EwFighterEtaS, or the striker is already inside R_t. Sorted by EtaS, then by distance.
        /// T-346 D3 (hysteresis): a track that was a threat in prev stays while inside, or EtaS &lt;= EwFighterEtaS x EwFighterExitMult, or StillThreat; it goes after EwFighterGoneS without any of these.
        /// SinceT / EtaInS keep the moment and the ETA of the entry, EtaS is the current one.</summary>
        public static List<StrikeFighterThreat> Threats(IList<StrikeFighter> fs, IList<StrikeAircraft> strikers, StrikeParams p, IList<StrikeFighterThreat> prev, double now)
        {
            var res = new List<StrikeFighterThreat>();
            if (fs == null || strikers == null || strikers.Count == 0 || p.EwFighterEtaS <= 0) return res;
            foreach (var f in fs)
            {
                if (!Eligible(f)) continue;
                StrikeFighterThreat best = null;
                foreach (var s in strikers)
                {
                    if (s == null) continue;
                    var sp = new GeoPt(s.Lat, s.Lon);
                    double d = AswMath.DistNm(Pos(f), sp, f.Globe), cl = ClosingKn(f, sp), eta = EtaS(d, f.RNm, cl);
                    if (best == null || eta < best.EtaS || (eta == best.EtaS && d < best.DistNm))
                        best = new StrikeFighterThreat { F = f, DistNm = d, ClosingKn = cl, EtaS = eta, Inside = d <= f.RNm, NearUid = s.Uid };
                }
                if (best == null) continue;
                bool enter = best.Inside || (!double.IsInfinity(best.EtaS) && best.EtaS <= p.EwFighterEtaS);
                var old = prev == null ? null : prev.FirstOrDefault(t => t.F != null && t.F.TrackId == f.TrackId);
                if (old == null)
                {
                    if (!enter) continue;
                    best.SinceT = now; best.EtaInS = best.EtaS;
                    res.Add(best);
                    continue;
                }
                best.SinceT = old.SinceT; best.EtaInS = old.EtaInS;
                bool stay = enter || (!double.IsInfinity(best.EtaS) && best.EtaS <= p.EwFighterEtaS * StrikeCalib.EwFighterExitMult) || StillThreat(f, strikers);
                if (stay) best.OutSinceT = -1;
                else
                {
                    best.OutSinceT = old.OutSinceT >= 0 ? old.OutSinceT : now;
                    if (now - best.OutSinceT >= StrikeCalib.EwFighterGoneS) continue;
                }
                res.Add(best);
            }
            return res.OrderBy(t => t.EtaS).ThenBy(t => t.DistNm).ToList();
        }

        /// <summary>T-346 D3: why a threat left the list: lost (the track is gone or no longer a fighter target) | receding (it flies away) | far (too far or too late).</summary>
        public static string GoneWhy(StrikeFighter f, StrikeFighterThreat last)
        {
            if (f == null || !Eligible(f) || last == null) return "lost";
            if (last.DistNm > StrikeCalib.EwFighterFarMult * f.RNm) return "far";
            return last.ClosingKn <= 0 ? "receding" : "far";
        }

        /// <summary>T-346: the roles of an aircraft class ("Fighter,Bomber") name a fighter.</summary>
        public static bool RoleIsFighter(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return false;
            return csv.IndexOf("Fighter", StringComparison.OrdinalIgnoreCase) >= 0 || csv.IndexOf("Interceptor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The fighter is still a threat to the strikers: it approaches the nearest one and is not farther than EwFighterFarMult x R_t.</summary>
        public static bool StillThreat(StrikeFighter f, IList<StrikeAircraft> strikers)
        {
            if (!Eligible(f) || strikers == null) return false;
            foreach (var s in strikers)
            {
                if (s == null) continue;
                var sp = new GeoPt(s.Lat, s.Lon);
                double d = AswMath.DistNm(Pos(f), sp, f.Globe);
                if (d <= f.RNm) return true;
                if (d <= StrikeCalib.EwFighterFarMult * f.RNm && ClosingKn(f, sp) > 0) return true;
            }
            return false;
        }

        /// <summary>Which jammer takes which threat: urgent threats first, a free jammer (the fighters-role first) takes the most urgent one that is not covered yet, the other threats in its cone are covered on the way.
        /// A jammer keeps its current target while the target is a threat, unless another threat is more urgent by EwFighterPreemptS and the jammer has held its target EwFighterSwitchS.</summary>
        public static List<StrikeFighterJob> Assign(IList<StrikeEwJammer> js, IList<StrikeFighterThreat> ts, double now, StrikeParams p, out List<int> uncovered)
        {
            var jobs = new List<StrikeFighterJob>();
            uncovered = new List<int>();
            if (ts == null || ts.Count == 0) return jobs;
            var order = (js ?? new List<StrikeEwJammer>()).Where(j => j != null && j.Free).OrderBy(j => j.Role == "fighters" ? 0 : 1).ThenBy(j => j.Uid).ToList();
            var chosen = new Dictionary<int, StrikeFighterThreat>();
            Func<StrikeEwJammer, StrikeFighterThreat, StrikeFighterThreat, bool> inCone = (j, aim, t) => StrikeEwMath.InCone(j.Pos, Pos(aim.F), Pos(t.F), j.ConeDeg, p.EwConeMarginDeg);
            foreach (var j in order)
            {
                var cur = j.Current > 0 ? ts.FirstOrDefault(t => t.F.TrackId == j.Current) : null;
                if (cur != null) chosen[j.Uid] = cur;
            }
            var top = ts[0];
            foreach (var j in order)
            {
                StrikeFighterThreat cur;
                if (!chosen.TryGetValue(j.Uid, out cur) || cur == top) continue;
                bool held = j.CurrentSinceT < 0 || now - j.CurrentSinceT >= StrikeCalib.EwFighterSwitchS;
                bool taken = chosen.Values.Contains(top);
                if (held && !taken && cur.EtaS - top.EtaS >= StrikeCalib.EwFighterPreemptS && !inCone(j, cur, top)) { chosen[j.Uid] = top; break; }
            }
            Func<StrikeFighterThreat, bool> covered = t => chosen.Values.Contains(t) || order.Any(j => { StrikeFighterThreat c; return chosen.TryGetValue(j.Uid, out c) && inCone(j, c, t); });
            foreach (var j in order)
            {
                if (chosen.ContainsKey(j.Uid)) continue;
                var t = ts.FirstOrDefault(x => !covered(x));
                if (t != null) chosen[j.Uid] = t;
            }
            foreach (var j in order)
            {
                StrikeFighterThreat c;
                if (!chosen.TryGetValue(j.Uid, out c)) continue;
                var job = new StrikeFighterJob { Uid = j.Uid, TargetId = c.F.TrackId };
                foreach (var t in ts) if (t != c && inCone(j, c, t)) job.Cover.Add(t.F.TrackId);
                jobs.Add(job);
            }
            foreach (var t in ts)
                if (!jobs.Any(jb => jb.TargetId == t.F.TrackId || jb.Cover.Contains(t.F.TrackId))) uncovered.Add(t.F.TrackId);
            return jobs;
        }

        /// <summary>Every threat is at least R_t + standoff away from the jammer (no threats = safe).</summary>
        public static bool SafeFrom(GeoPt jam, IList<StrikeFighterThreat> ts, double standoffNm)
        {
            if (ts == null) return true;
            foreach (var t in ts) if (AswMath.DistNm(jam, Pos(t.F)) < t.F.RNm + standoffNm) return false;
            return true;
        }

        /// <summary>The same condition, for the return from the retreat: every listed fighter (threats or not) is farther than R_t + standoff.</summary>
        public static bool FightersFar(GeoPt jam, IList<StrikeFighterThreat> ts, double standoffNm) { return SafeFrom(jam, ts, standoffNm); }

        /// <summary>The point behind the group on the axis "main fighter -> centre of the group" (turned up to 30 degrees either way) from which every fighter is R_t + standoff away and no hostile ship reaches (SamMaxNm + StandoffNm).
        /// why: ok | between (the fighter stands between the jammer and the group, or the group is already inside R_t) | no_point.</summary>
        public static GeoPt? StandoffPt(GeoPt jam, GeoPt groupC, IList<StrikeFighterThreat> ts, IList<StrikeShip> hostiles, StrikeParams p, Func<GeoPt, bool> isLand, out string why)
        {
            why = "ok";
            if (ts == null || ts.Count == 0) return jam;
            var main = ts[0].F;
            var mp = Pos(main);
            if (AswMath.DistNm(mp, groupC) <= main.RNm) { why = "between"; return null; }
            double axis = AswMath.BearingDeg(mp, groupC);
            foreach (double off in new double[] { 0, 5, -5, 10, -10, 15, -15, 20, -20, 25, -25, 30, -30 })
                for (double back = p.EwBehindNm; back <= p.EwBehindNm + 80; back += 1)
                {
                    var pt = AswMath.Offset(groupC, axis + off, back);
                    if (PointOk(pt, ts, hostiles, p, isLand)) return pt;
                }
            double dj = AswMath.DistNm(jam, mp), dg = AswMath.DistNm(jam, groupC);
            why = dj < dg && Math.Abs(Wrap180(AswMath.BearingDeg(jam, mp) - AswMath.BearingDeg(jam, groupC))) < 60 ? "between" : "no_point";
            return null;
        }

        private static bool PointOk(GeoPt pt, IList<StrikeFighterThreat> ts, IList<StrikeShip> hostiles, StrikeParams p, Func<GeoPt, bool> isLand)
        {
            foreach (var t in ts) if (AswMath.DistNm(pt, Pos(t.F)) < t.F.RNm + p.FighterStandoffNm) return false;
            if (hostiles != null)
                foreach (var h in hostiles)
                    if (h != null && h.SamMaxNm > 0 && AswMath.DistNm(pt, new GeoPt(h.Lat, h.Lon)) < h.SamMaxNm + p.StandoffNm) return false;
            return isLand == null || !isLand(pt);
        }

        /// <summary>transit | attack | retreat. The ships-role jammer attacks the ships in "jamming"; the fighters-role (the reserve) is in transit mode all the time it works.</summary>
        public static string Mode(string state, string role)
        {
            if (state == "guard" || state == "retreat") return "retreat";
            if (state == "jamming" && role != "fighters") return "attack";
            return "transit";
        }

        /// <summary>Whether the jammer may jam fighters in this state: ships-role only on the way (wait | to_station | on_station); the fighters-role (the reserve) in every working state, including "jamming" (the ships).</summary>
        public static bool CanJamFighters(string state, string role)
        {
            if (state == "wait" || state == "to_station" || state == "on_station") return true;
            return state == "jamming" && role == "fighters";
        }

        // ---------------------------------------------------------------- self test

        private static StrikeFighter Fg(int id, GeoPt at, double courseDeg, double kn = 480, double r = 38, string cls = "F-14A")
        {
            return new StrikeFighter { TrackId = id, ClassIni = "usn_f-14a", ClassName = cls, Lat = at.Lat, Lon = at.Lon, CourseDeg = courseDeg, SpeedKn = kn, RNm = r, RSrc = "class", Identified = true, Hostile = true, IsFighter = true, FighterSrc = "role" };
        }

        private static StrikeAircraft Sk(int uid, GeoPt at) { return new StrikeAircraft { Uid = uid, Lat = at.Lat, Lon = at.Lon, Airborne = true }; }

        private static StrikeEwJammer Jm(int uid, GeoPt at, string role = "ships", bool free = true, int cur = 0, double since = -1) { return new StrikeEwJammer { Uid = uid, Pos = at, ConeDeg = 30, Role = role, Free = free, Current = cur, CurrentSinceT = since }; }

        public static string SelfTest(string part = "all")
        {
            bool all = part == "all";
            string e;
            if ((all || part == "threat") && (e = TThreat()) != null) return "threat: " + e;
            if ((all || part == "assign") && (e = TAssign()) != null) return "assign: " + e;
            if ((all || part == "standoff") && (e = TStandoff()) != null) return "standoff: " + e;
            if ((all || part == "mode") && (e = TMode()) != null) return "mode: " + e;
            if ((all || part == "budget") && (e = TBudget()) != null) return "budget: " + e;
            if ((all || part == "hyst") && (e = THyst()) != null) return "hyst: " + e;
            if ((all || part == "role") && (e = TRole()) != null) return "role: " + e;
            if ((all || part == "lat") && (e = TLat()) != null) return "lat: " + e;
            return null;
        }

        private static readonly GeoPt C0 = new GeoPt(39.0, 10.0);

        // T-347: the helper of the world metric, then the strike ETA on the beta globe (LCC) and on the stable flat world at 0 / 45 / 60 / 70 deg
        private static string TLat()
        {
            if (Math.Abs(AswMath.LonScale(60, 60, true) - 0.5) > 1e-12 || AswMath.LonScale(60, 60, false) != 1.0 || AswMath.LonScale(89.9, 89.9, true) != AswMath.MinCosLat) return "LonScale";
            var pa = new[] { new GeoPt(39, 10), new GeoPt(40.5, 11.25), new GeoPt(-20, 3) };
            var pb = new[] { new GeoPt(39.3, 10.7), new GeoPt(41, 9), new GeoPt(-20.5, 3.4) };
            for (int i = 0; i < pa.Length; i++)
                if (AswMath.DistNm(pa[i], pb[i], false) != AswMath.DistNm(pa[i], pb[i])) return "DistNm(false) must be bit-identical " + i;
            if (AswMath.RadialClosingKn(pa[0], pa[0], 100, 0, true, 7) != 7) return "RadialClosingKn coincide";
            bool dec;
            if (!AswMath.GlobeFromMetric(60, 30, 60, out dec) || !dec) return "GlobeFromMetric globe";
            if (AswMath.GlobeFromMetric(60, 60, 60, out dec) || !dec) return "GlobeFromMetric flat";
            AswMath.GlobeFromMetric(3, 59.9, 60, out dec); if (dec) return "GlobeFromMetric near the equator is undecided";
            AswMath.GlobeFromMetric(double.NaN, 30, 60, out dec); if (dec) return "GlobeFromMetric NaN";
            AswMath.GlobeFromMetric(60, 0, 60, out dec); if (dec) return "GlobeFromMetric zero";
            var o60 = AswMath.OffsetNm(new GeoPt(60, 10), 0, 10, true);
            if (Math.Abs(o60.Lon - 10 - 10.0 / 30.0) > 1e-9 || o60.Lat != 60) return "OffsetNm 60 deg";
            var strikers = new List<StrikeAircraft>();
            var p = new StrikeParams();
            // T-348: the standoff point of the fighters-jam is R_t + standoff away in TRUE nm on the beta globe
            foreach (double slat in new[] { 0.0, 45, 60, 70 })
                foreach (bool sg in new[] { true, false })
                    using (AswMath.WorldScope(sg))
                    {
                        var grp = new GeoPt(slat, 10);
                        var fpos = AswMath.Offset(grp, 90, 40);
                        var fth = new StrikeFighterThreat { F = Fg(50, fpos, 270) };
                        string swhy;
                        var spt = StandoffPt(AswMath.Offset(grp, 270, 20), grp, new[] { fth }, new List<StrikeShip>(), p, null, out swhy);
                        if (!spt.HasValue) return "standoff lat " + slat + " " + swhy;
                        double sd = sg ? AswMath.GreatCircleNm(spt.Value, fpos) : AswMath.DistNm(spt.Value, fpos);
                        if (sd < 48 * 0.985 || sd > 48 * 1.015 + 1) return "standoff dist " + sd + " lat " + slat + (sg ? " globe" : " flat");
                    }
            foreach (double lat in new[] { 0.0, 45, 60, 70 })
                foreach (double az in new[] { 45.0, 90 })
                    foreach (bool globe in new[] { true, false })
                    {
                        var P = new GeoPt(lat, 10);
                        double r = az * Math.PI / 180.0;
                        var at = AswMath.OffsetNm(P, 80 * Math.Cos(r), 80 * Math.Sin(r), globe);
                        var f = Fg(1, at, az + 180); f.Globe = globe;
                        var th = Threats(new[] { f }, new List<StrikeAircraft> { Sk(1, P) }, new StrikeParams { EwFighterEtaS = 2000 });
                        string tag = " lat " + lat + " az " + az + (globe ? " globe" : " flat");
                        if (th.Count != 1) return "threat missing" + tag;
                        double tol = globe ? 0.015 : 1e-6 / 315.0;
                        if (Math.Abs(th[0].EtaS - 315) > 315 * tol) return "eta " + th[0].EtaS + tag;
                        if (Math.Abs(ClosingKn(f, P) - 480) > 4.8) return "closing " + ClosingKn(f, P) + tag;
                        var tg = Fg(2, at, az + 90); tg.Globe = globe;
                        if (Math.Abs(ClosingKn(tg, P)) > 5) return "tangent " + ClosingKn(tg, P) + tag;
                        if (lat == 60 && globe)
                        {
                            var old = Fg(3, at, az + 180);   // the beta track with the old (flat) rule
                            var tho = Threats(new[] { old }, new List<StrikeAircraft> { Sk(1, P) }, new StrikeParams { EwFighterEtaS = 5000 });
                            if (tho.Count != 1 || tho[0].EtaS <= 600) return "the flat rule on the beta globe must be wrong " + (tho.Count == 1 ? tho[0].EtaS : -1);
                        }
                    }
            return null;
        }

        private static string TThreat()
        {
            var p = new StrikeParams();
            var strikers = new List<StrikeAircraft> { Sk(1, C0) };
            var north80 = AswMath.Offset(C0, 0, 80);
            var inb = Fg(10, north80, 180);
            var th = Threats(new[] { inb }, strikers, p);
            if (th.Count != 1) return "inbound 80 nm not a threat";
            if (Math.Abs(th[0].EtaS - 315) > 4) return "eta " + th[0].EtaS;
            // the same numbers as AirGuardMath: closing 480 kn on a track that flies at the point; EtaS (80-38)/480*3600
            if (Math.Abs(ClosingKn(inb, C0) - 480) > 1 || Math.Abs(EtaS(80, 38, 480) - 315) > 1e-9 || EtaS(30, 38, 100) != 0 || !double.IsPositiveInfinity(EtaS(80, 38, -5))) return "formulas";
            var tight = new StrikeParams { EwFighterEtaS = 300 };
            if (Threats(new[] { inb }, strikers, tight).Count != 0) return "eta limit 300";
            if (Threats(new[] { inb }, strikers, new StrikeParams { EwFighterEtaS = 0 }).Count != 0) return "EwFighterEtaS 0 must switch the function off";
            if (Threats(new[] { Fg(11, north80, 0) }, strikers, p).Count != 0) return "a fighter flying away";
            var inside = Threats(new[] { Fg(12, AswMath.Offset(C0, 90, 20), 0) }, strikers, p);
            if (inside.Count != 1 || !inside[0].Inside || inside[0].EtaS != 0) return "inside R_t";
            Func<Action<StrikeFighter>, int> n = mod => { var f = Fg(13, north80, 180); mod(f); return Threats(new[] { f }, strikers, p).Count; };
            if (n(f => f.Identified = false) != 0) return "unidentified";
            if (n(f => f.Hostile = false) != 0) return "not hostile";
            if (n(f => f.Helicopter = true) != 0) return "helicopter";
            if (n(f => f.IsFighter = false) != 0) return "not a fighter class";
            if (n(f => f.RNm = 0) != 0) return "no missile radius";
            if (Threats(new[] { inb }, new List<StrikeAircraft>(), p).Count != 0) return "no strikers";
            var order = Threats(new[] { Fg(21, AswMath.Offset(C0, 0, 100), 180), Fg(22, AswMath.Offset(C0, 90, 70), 270) }, strikers, p);
            if (order.Count != 2 || order[0].F.TrackId != 22) return "order by eta";
            if (StillThreat(Fg(30, AswMath.Offset(C0, 0, 100), 180), strikers) != false) return "farther than 2 x R_t is no threat";
            if (!StillThreat(Fg(31, AswMath.Offset(C0, 0, 70), 180), strikers)) return "closing within 2 x R_t";
            if (StillThreat(Fg(32, AswMath.Offset(C0, 0, 70), 0), strikers)) return "receding is no threat";
            return null;
        }

        private static string TRole()
        {
            if (!RoleIsFighter("Fighter,Bomber") || !RoleIsFighter("Interceptor") || !RoleIsFighter("fighter")) return "fighter roles";
            if (RoleIsFighter("Bomber,SEAD") || RoleIsFighter(null) || RoleIsFighter("")) return "not fighter roles";
            return null;
        }

        /// <summary>T-346 D3: the closing speed of a CAP fighter jumps, the threat does not flap.</summary>
        private static string THyst()
        {
            var p = new StrikeParams();
            var strikers = new List<StrikeAircraft> { Sk(1, C0) };
            // d = 33.3 nm, R_t = 27: the gap 6.3 nm. closing 41 kn = ETA 553 s (enters), 100 kn = 227 s, -50 kn = infinite, 60 kn = 378 s
            var at = AswMath.Offset(C0, 0, 33.3);
            Func<double, StrikeFighter> mk = kn => { var f = Fg(10, at, kn >= 0 ? 180 : 0, Math.Abs(kn), 27); return f; };
            IList<StrikeFighterThreat> cur = new List<StrikeFighterThreat>();
            double[] seq = { 41, 100, -50, 60 };
            int enters = 0, exits = 0; double now = 0;
            for (int i = 0; i < 12; i++, now += 5)
            {
                var fs = new[] { mk(seq[i % 4]) };
                bool had = cur.Count > 0;
                cur = Threats(fs, strikers, p, cur, now);
                if (!had && cur.Count > 0) enters++;
                if (had && cur.Count == 0) exits++;
            }
            if (enters != 1 || exits != 0) return "flapping: enters " + enters + " exits " + exits;
            if (Math.Abs(cur[0].EtaInS - 553) > 5 || cur[0].SinceT != 0) return "entry eta " + cur[0].EtaInS + " since " + cur[0].SinceT;
            // the entry rule is the old one: 100 kn first (227 s) enters; a far slow track (ETA 1000 s) does not
            if (Threats(new[] { Fg(11, at, 180, 25, 27) }, strikers, p, null, 0).Count != 0) return "far track enters";
            // leaving: the track turns away for good; the threat stays for EwFighterGoneS and then goes
            var away = new[] { Fg(10, at, 0, 300, 27) };
            double t0 = now;
            for (; now < t0 + StrikeCalib.EwFighterGoneS - 1; now += 5)
            {
                cur = Threats(away, strikers, p, cur, now);
                if (cur.Count != 1) return "left early at " + (now - t0);
            }
            if (cur[0].OutSinceT != t0) return "out since " + cur[0].OutSinceT;
            cur = Threats(away, strikers, p, cur, t0 + StrikeCalib.EwFighterGoneS);
            if (cur.Count != 0) return "stays after the gone time";
            if (GoneWhy(away[0], new StrikeFighterThreat { F = away[0], DistNm = 33.3, ClosingKn = -300 }) != "receding") return "why receding";
            if (GoneWhy(away[0], new StrikeFighterThreat { F = away[0], DistNm = 80, ClosingKn = 300 }) != "far") return "why far";
            if (GoneWhy(null, new StrikeFighterThreat()) != "lost") return "why lost";
            // coming back inside the stay condition resets the out time
            cur = Threats(new[] { mk(41) }, strikers, p, null, 100);
            cur = Threats(away, strikers, p, cur, 105);
            if (cur[0].OutSinceT != 105) return "out start";
            cur = Threats(new[] { mk(100) }, strikers, p, cur, 110);
            if (cur[0].OutSinceT != -1) return "out reset";
            return null;
        }

        private static string TAssign()
        {
            var p = new StrikeParams();
            var ts = new List<StrikeFighterThreat>();
            var jam = AswMath.Offset(C0, 180, 60);   // south of the group; the fighters come from the north
            Func<int, double, double, StrikeFighterThreat> th = (id, brg, eta) => new StrikeFighterThreat { F = Fg(id, AswMath.Offset(C0, brg, 80), 180), EtaS = eta, DistNm = 80 };
            // one jammer, two threats 5 degrees apart (seen from the jammer almost the same line): the urgent one is the target, the other is covered
            var a = th(1, 0, 100); var b = th(2, 3, 200);
            List<int> unc;
            var jobs = Assign(new[] { Jm(5, jam) }, new List<StrikeFighterThreat> { a, b }, 1000, p, out unc);
            if (jobs.Count != 1 || jobs[0].TargetId != 1 || !jobs[0].Cover.Contains(2) || unc.Count != 0) return "one cone, two threats " + jobs.Count;
            // 90 degrees apart: the second is not covered
            var c = th(3, 90, 200);
            jobs = Assign(new[] { Jm(5, jam) }, new List<StrikeFighterThreat> { a, c }, 1000, p, out unc);
            if (jobs.Count != 1 || jobs[0].Cover.Count != 0 || unc.Count != 1 || unc[0] != 3) return "90 degrees apart " + string.Join(",", unc);
            // two jammers: the fighters-role takes the most urgent, the ships-role the next
            jobs = Assign(new[] { Jm(6, jam), Jm(7, jam, "fighters") }, new List<StrikeFighterThreat> { a, c }, 1000, p, out unc);
            var j7 = jobs.FirstOrDefault(j => j.Uid == 7); var j6 = jobs.FirstOrDefault(j => j.Uid == 6);
            if (j7 == null || j6 == null || j7.TargetId != 1 || j6.TargetId != 3 || unc.Count != 0) return "two jammers";
            // a jammer that is not free (the attack) gets no job
            jobs = Assign(new[] { Jm(6, jam, "ships", false) }, new List<StrikeFighterThreat> { a }, 1000, p, out unc);
            if (jobs.Count != 0 || unc.Count != 1) return "not free";
            // the current target is kept: a new threat 40 s more urgent does not take it; 61 s more urgent and 16 s held does
            var cur = th(4, 90, 300); var neu = th(5, 0, 260);
            jobs = Assign(new[] { Jm(6, jam, "ships", true, 4, 990) }, new List<StrikeFighterThreat> { neu, cur }, 1000, p, out unc);
            if (jobs[0].TargetId != 4) return "current target kept " + jobs[0].TargetId;
            var neu2 = th(5, 0, 239);
            jobs = Assign(new[] { Jm(6, jam, "ships", true, 4, 990) }, new List<StrikeFighterThreat> { neu2, cur }, 1000, p, out unc);
            if (jobs[0].TargetId != 4) return "held for less than the switch time " + jobs[0].TargetId;
            jobs = Assign(new[] { Jm(6, jam, "ships", true, 4, 984) }, new List<StrikeFighterThreat> { neu2, cur }, 1000, p, out unc);
            if (jobs[0].TargetId != 5) return "switch after 16 s and 61 s earlier " + jobs[0].TargetId;
            return null;
        }

        private static string TStandoff()
        {
            var p = new StrikeParams();
            var f = Fg(1, AswMath.Offset(C0, 0, 80), 180);
            var ts = new List<StrikeFighterThreat> { new StrikeFighterThreat { F = f, DistNm = 80, EtaS = 315 } };
            var jam = AswMath.Offset(f.Lat == 0 ? C0 : new GeoPt(f.Lat, f.Lon), 180, 45);
            if (SafeFrom(jam, ts, 10)) return "45 nm from R_t 38 + 10 must be unsafe";
            if (!SafeFrom(AswMath.Offset(new GeoPt(f.Lat, f.Lon), 180, 49), ts, 10)) return "49 nm is safe";
            if (!FightersFar(jam, new List<StrikeFighterThreat>(), 10)) return "no fighters = far";
            string why;
            var pt = StandoffPt(jam, C0, ts, new List<StrikeShip>(), p, null, out why);
            if (!pt.HasValue || why != "ok") return "point " + why;
            if (AswMath.DistNm(pt.Value, new GeoPt(f.Lat, f.Lon)) < 48) return "point too close to the fighter";
            if (AswMath.DistNm(pt.Value, C0) > p.EwBehindNm + 80 + 1e-6) return "point is not behind the group";
            // a SAM ship on the axis behind the group: no point at all
            var ship = new StrikeShip { TrackId = 9, Lat = C0.Lat - 0.3, Lon = C0.Lon, SamMaxNm = 90, Hostile = true };
            pt = StandoffPt(jam, C0, ts, new List<StrikeShip> { ship }, p, null, out why);
            if (pt.HasValue || why != "no_point") return "SAM zones everywhere " + why;
            // the point is outside the zone when there is a ship away from the axis
            var far = new StrikeShip { TrackId = 10, Lat = C0.Lat, Lon = C0.Lon + 3, SamMaxNm = 20, Hostile = true };
            pt = StandoffPt(jam, C0, ts, new List<StrikeShip> { far }, p, null, out why);
            if (!pt.HasValue || AswMath.DistNm(pt.Value, new GeoPt(far.Lat, far.Lon)) < 30) return "outside the zone of the ship";
            // the fighter between the jammer and the group, the group already inside R_t
            var near = new List<StrikeFighterThreat> { new StrikeFighterThreat { F = Fg(2, AswMath.Offset(C0, 0, 30), 180), DistNm = 30, EtaS = 0, Inside = true } };
            var jamNorth = AswMath.Offset(C0, 0, 50);
            if (StandoffPt(jamNorth, C0, near, new List<StrikeShip>(), p, null, out why).HasValue || why != "between") return "between " + why;
            // two threats from opposite sides: a point safe from both, or no point
            var two = new List<StrikeFighterThreat> { ts[0], new StrikeFighterThreat { F = Fg(3, AswMath.Offset(C0, 180, 80), 0), DistNm = 80, EtaS = 400 } };
            pt = StandoffPt(jam, C0, two, new List<StrikeShip>(), p, null, out why);
            if (pt.HasValue && !SafeFrom(pt.Value, two, 10)) return "a point unsafe from the second fighter";
            // land
            pt = StandoffPt(jam, C0, ts, new List<StrikeShip>(), p, q => AswMath.DistNm(q, C0) < 20, out why);
            if (!pt.HasValue || AswMath.DistNm(pt.Value, C0) < 20) return "land";
            return null;
        }

        private static string TMode()
        {
            if (Mode("jamming", "ships") != "attack" || CanJamFighters("jamming", "ships")) return "ships in jamming";
            if (Mode("to_station", "ships") != "transit" || !CanJamFighters("to_station", "ships")) return "ships to_station";
            if (Mode("on_station", "fighters") != "transit" || !CanJamFighters("on_station", "fighters")) return "fighters on_station";
            if (Mode("jamming", "fighters") != "transit" || !CanJamFighters("jamming", "fighters")) return "fighters in jamming";
            if (Mode("guard", "ships") != "retreat" || Mode("retreat", "fighters") != "retreat" || CanJamFighters("guard", "fighters") || CanJamFighters("retreat", "ships")) return "retreat";
            foreach (string st in new[] { "back", "lost", "no_station", "off" })
                if (CanJamFighters(st, "ships") || CanJamFighters(st, "fighters")) return "terminal " + st;
            return null;
        }

        private static string TBudget()
        {
            var p = new StrikeParams();
            var fs = new List<StrikeFighter>();
            for (int i = 0; i < 12; i++) fs.Add(Fg(100 + i, AswMath.Offset(C0, i * 30, 70 + 3 * i), (i * 30 + 180) % 360));
            var strikers = new List<StrikeAircraft>();
            for (int i = 0; i < 8; i++) strikers.Add(Sk(i + 1, AswMath.Offset(C0, i * 45, 3)));
            var js = new[] { Jm(1, AswMath.Offset(C0, 180, 60)), Jm(2, AswMath.Offset(C0, 150, 60), "fighters"), Jm(3, AswMath.Offset(C0, 210, 60)) };
            double best = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < 20; k++)
                {
                    var th = Threats(fs, strikers, p);
                    List<int> unc;
                    Assign(js, th, 1000, p, out unc);
                    string why;
                    StandoffPt(js[0].Pos, C0, th, new List<StrikeShip>(), p, null, out why);
                }
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / 20);
            }
            Console.WriteLine("strike ewf budget ms=" + best.ToString("0.00", CultureInfo.InvariantCulture));
            return best <= 2 ? null : "over 2 ms: " + best;
        }
    }
}
