// T-333 (ADR-0129) stream B: zone intrusion intercept of a patrol group. The planner is CapInterceptMath (pure); this file keeps the state, guides the birds
// (as the alert pair: LeadPoint route every 10 s, one dash, hand-over to the game AI inside AAM range + 5 nm), ends the intercept and puts the bird back on its lane.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class CapController
    {
        /// <summary>One bird's intercept. Phase: guide | handed | fight | lost.</summary>
        private sealed class Icpt
        {
            public int Target, Round; public string Phase = "guide";
            public double Since, LastGuide = -1e9, HandedAt = -1, LostSince = -1, EtaS = -1, EtaAt, DashKn;
            public GeoPt Ip; public bool HasIp, DashDone, RouteErr;
        }

        /// <summary>target track id -> birds that already serve it (other groups, alert raids, game AI); cleared at the start of every Tick.</summary>
        private readonly Dictionary<int, int> _icptClaims = new Dictionary<int, int>();
        private HashSet<int> _capAir = new HashSet<int>();   // swapped as a whole once per Tick: the friendly-fire guard reads it from the engage-task hook
        /// <summary>The friendly-fire guard (set by CapFeature); only read for the window / cap_state.</summary>
        internal CapFfGuard FfGuard;

        /// <summary>The unit is a member of a CAP group (patrol or alert) and flies. Hot path of the engage-task hook: one HashSet read.</summary>
        internal bool IsCapAir(int uid) { var s = _capAir; return s != null && s.Contains(uid); }

        private void RebuildCapAir()
        {
            var s = new HashSet<int>();
            foreach (var g in _groups) foreach (var m in g.Members) if (m.Airborne) s.Add(m.Uid);
            _capAir = s;
        }

        /// <summary>[Cap] InterceptLostS changed live: every group takes it (the default of a new group comes with the next CapParams clone).</summary>
        internal void SetInterceptLostS(double s)
        {
            _p.InterceptLostS = s;
            foreach (var g in _groups) { if (g.I != null) g.I.LostS = s; g.P.InterceptLostS = s; }
        }

        private static double Num0(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : v; }

        // ================================================= planner (once per group and tick) ==========================================
        private void InterceptTick(Grp g, Dictionary<int, CapBird> birds, IList<CapContact> contacts, double now)
        {
            var s = g.I ?? (g.I = CapInterceptSpec.FromParams(g.P));
            if (!s.On || g.Roe == "hold")
            {
                foreach (var m in g.Members)
                {
                    if (m.Icpt == null) continue;
                    CapBird b0; birds.TryGetValue(m.Uid, out b0);
                    EndIntercept(g, m, b0, !s.On ? "off" : "roe_hold");
                }
                if (!g.IcptOffLogged) { g.IcptOffLogged = true; Log("intercept off group=" + g.Name + " why=" + (!s.On ? "disabled" : "roe_hold")); }
                return;
            }
            g.IcptOffLogged = false;
            if (g.Zone.Rect == null || g.Zone.Rect.Length < 4) return;

            var heldTargets = new HashSet<int>(); bool anyHeld = false;
            foreach (var m in g.Members) if (m.Icpt != null) { heldTargets.Add(m.Icpt.Target); anyHeld = true; }
            var targets = new List<CapContact>(); var targetIds = new HashSet<int>();
            if (contacts != null)
                foreach (var c in contacts)
                {
                    if (c == null) continue;
                    bool inZone = CapInterceptMath.InZone(c, g.Zone, s.MarginNm, s.Targets);
                    // a target a bird already chases stays one until it is beyond the pursuit radius (sticky: it is not dropped when it leaves the margin)
                    bool chased = !inZone && heldTargets.Contains(c.Id) && CapInterceptMath.KindOk(c, s.Targets) && CapMath.DistToZoneNm(new GeoPt(c.Lat, c.Lon), g.Zone) <= g.P.PursuitNm;
                    if (inZone || chased) { targets.Add(c); targetIds.Add(c.Id); }
                }
            if (targets.Count == 0 && !anyHeld) { if (g.IcptWaitLogged.Count > 0) g.IcptWaitLogged.Clear(); return; }

            var claimed = new Dictionary<int, int>(_icptClaims);
            var fit = new List<CapIcptBird>(); var held = new List<CapIcptPair>();
            foreach (var m in g.Members)
            {
                if (!m.Airborne || m.Alert) continue;
                CapBird raw;
                if (!birds.TryGetValue(m.Uid, out raw)) continue;
                if (m.Leaving != null) { if (m.Icpt != null) EndIntercept(g, m, raw, "leaving"); continue; }
                var b = raw.Copy();
                b.AamStart = m.AamStart; b.LongStart = m.LongStart; b.Alert = false;
                if (!string.IsNullOrEmpty(m.Where)) b.Where = m.Where;
                string why = CapInterceptMath.Fit(b, false, false, false, m.FuelLowBy, m.LeaveInS < 0 ? 1e9 : m.LeaveInS, StrikeWhy(b), g.P);
                if (why != null) { if (m.Icpt != null) EndIntercept(g, m, raw, why); continue; }
                if (m.Icpt != null)
                {
                    if (!targetIds.Contains(m.Icpt.Target)) continue;   // the track is gone: the bird flies on to the last point, InterceptAirborne ends it (killed / lost)
                    held.Add(new CapIcptPair { Uid = m.Uid, Target = m.Icpt.Target, Round = m.Icpt.Round, EtaS = m.Icpt.EtaS, Op = "keep" });
                }
                else if (raw.TargetId >= 0 && targetIds.Contains(raw.TargetId))
                {
                    int n; claimed.TryGetValue(raw.TargetId, out n); claimed[raw.TargetId] = n + 1;   // the game AI already fights this one with the bird: it counts for the target, it is not re-tasked
                    continue;
                }
                fit.Add(new CapIcptBird { Uid = m.Uid, Pos = new GeoPt(raw.Lat, raw.Lon), DashKn = IcptKn(m, raw, s), Held = m.Icpt != null ? m.Icpt.Target : -1, HeldRound = m.Icpt != null ? m.Icpt.Round : 0,
                                          AiTarget = raw.TargetId, Fighting = raw.Engaged || raw.AiActive });
            }

            var res = CapInterceptMath.Assign(targets, g.Zone, fit, held, claimed, s);
            var covered = new HashSet<int>();
            foreach (var p in res)
            {
                covered.Add(p.Target);
                Mem m = MemOf(g, p.Uid); if (m == null) continue;
                if (p.Op == "keep") continue;
                var old = m.Icpt;
                m.Icpt = new Icpt { Target = p.Target, Round = p.Round, Since = now, EtaS = p.EtaS, EtaAt = now, DashDone = old != null && old.DashDone, DashKn = old != null ? old.DashKn : 0 };
                try { _g.IcptSeed(p.Target); } catch (Exception) { }
                if (old != null) DropTargetCache(old.Target);
                Log("intercept " + (p.Op == "reassign" ? "reassign" : "start") + " uid=" + m.Uid + " group=" + g.Name + " target=" + p.Target + " round=" + p.Round + " eta_s=" + F(p.EtaS) + " " + (p.Lead ? "lead" : "pursuit")
                    + (p.Op == "reassign" ? " from=" + p.From : "") + " birds=" + fit.Count + " targets=" + targets.Count);
                Evt("cap_intercept", "{\"group\":" + Q(g.Name) + ",\"op\":" + Q(p.Op == "reassign" ? "reassign" : "start") + ",\"unit\":" + m.Uid + ",\"target\":" + p.Target + ",\"round\":" + p.Round + "}");
            }
            foreach (var h in held) covered.Add(h.Target);
            foreach (var t in targets)
            {
                int cl; _icptClaims.TryGetValue(t.Id, out cl);
                if (covered.Contains(t.Id) || claimed.ContainsKey(t.Id) || cl > 0) { g.IcptWaitLogged.Remove(t.Id); continue; }
                if (g.IcptWaitLogged.Add(t.Id)) Log("intercept wait group=" + g.Name + " target=" + t.Id + " fit=" + fit.Count + " (no free fit bird)");
            }
            g.IcptWaitLogged.RemoveWhere(id => !targetIds.Contains(id));
            foreach (var m in g.Members) if (m.Icpt != null) { int n; _icptClaims.TryGetValue(m.Icpt.Target, out n); _icptClaims[m.Icpt.Target] = n + 1; }
        }

        /// <summary>The speed the planner counts for the dash: the fastest step (the first dash is checked against fuel in InterceptAirborne), or the patrol speed.</summary>
        private double IcptKn(Mem m, CapBird b, CapInterceptSpec s)
        {
            if (m.Icpt != null && m.Icpt.DashKn > 0) return m.Icpt.DashKn;
            double kn = b.PatrolKn > 100 ? b.PatrolKn : 420;
            if (s.Speed != "max") return kn;
            try { var steps = _g.ThrottleSteps(m.Uid); if (steps != null) foreach (var x in steps) if (x.Kn > kn) kn = x.Kn; } catch (Exception) { }
            return kn;
        }

        // ================================================= in the air =================================================================
        /// <summary>True = the bird is handled by the intercept (no leash, no station speed, no lane route this tick).</summary>
        private bool InterceptAirborne(Grp g, Mem m, CapBird b, IList<CapContact> contacts, double now)
        {
            var ic = m.Icpt;
            if (ic == null) return false;
            var s = g.I ?? (g.I = CapInterceptSpec.FromParams(g.P));
            var pos = new GeoPt(b.Lat, b.Lon);
            if (m.Slot >= 0) b.Where = "zone";   // the slot stays held (the relief planner sees a holder, no relief is lifted for a chase)
            CapContact tgt = null;
            if (contacts != null) foreach (var c in contacts) if (c != null && c.Id == ic.Target) { tgt = c; break; }
            string gone = null;
            if (tgt == null)
            {
                if (ic.LostSince < 0) { ic.LostSince = now; Log("intercept target lost uid=" + m.Uid + " target=" + ic.Target + " (flies to the last point, ends after " + F(s.LostS) + " s)"); }
                try { gone = _g.TrackGone(ic.Target); } catch (Exception) { gone = null; }
            }
            else ic.LostSince = -1;
            if (tgt != null && !CapInterceptMath.KindOk(tgt, s.Targets)) { EndIntercept(g, m, b, "not_hostile"); return false; }
            string why = CapInterceptMath.End(tgt, gone, ic.LostSince, now, pos, g.Zone, s, g.P);
            if (why != null) { EndIntercept(g, m, b, why); return false; }
            if (b.Evading) return true;
            if (tgt == null) { ic.Phase = "lost"; return true; }

            double d = AswMath.DistNm(pos, new GeoPt(tgt.Lat, tgt.Lon));
            double aam = b.AamMaxNm > 0 ? b.AamMaxNm : CapInterceptMath.DefaultAamNm;
            bool fighting = b.Engaged || b.AiActive || (b.HasTarget && b.TargetId == ic.Target);
            if (fighting)
            {
                if (ic.Phase != "fight") { ic.Phase = "fight"; Log("intercept fight uid=" + m.Uid + " target=" + ic.Target + " nm=" + F(d) + " state=" + (b.AiState ?? "-")); }
                ic.HandedAt = now;
                return true;
            }
            bool force = false;
            if (ic.Phase == "fight" || ic.Phase == "handed")
            {
                if (now - ic.HandedAt < CapInterceptMath.StallS) return true;
                ic.Phase = "guide"; force = true;   // 30 s without a fight: guide again once, even inside the hand-over range
                Log("intercept reguide uid=" + m.Uid + " target=" + ic.Target + " nm=" + F(d));
            }
            if (!force && d <= aam + CapInterceptMath.HandoverExtraNm)
            {
                ic.Phase = "handed"; ic.HandedAt = now;
                Log("intercept handover uid=" + m.Uid + " target=" + ic.Target + " nm=" + F(d) + " aam_nm=" + F(aam));
                return true;
            }
            ic.Phase = "guide";
            if (now - ic.LastGuide < CapInterceptMath.GuideEveryS) return true;
            ic.LastGuide = now;
            if (!ic.DashDone)
            {
                ic.DashDone = true;
                if (s.Speed == "max" && !m.Dash)
                {
                    int cur = -1; IList<CapStep> steps = null;
                    try { cur = _g.Throttle(m.Uid); steps = _g.ThrottleSteps(m.Uid); } catch (Exception) { }
                    if (cur > 0 && steps != null && steps.Count > 0)
                    {
                        string note;
                        int pick = CapMath.DashStep(steps, cur, d, b.ReserveNm, b.MaxRangeNm, double.MaxValue, g.P, out note);
                        string e = pick != cur ? _g.SetThrottle(m.Uid, pick) : "same";
                        if (e == null)
                        {
                            m.Dash = true; m.PrevThrottle = cur; m.DashStep = pick;
                            foreach (var x in steps) if (x.Step == pick) ic.DashKn = x.Kn;
                            Log("intercept speed uid=" + m.Uid + " step " + cur + "->" + pick + " kn=" + F(ic.DashKn) + " " + note);
                        }
                    }
                }
            }
            double kn = ic.DashKn > 0 ? ic.DashKn : (b.PatrolKn > 100 ? b.PatrolKn : 420);
            GeoPt ip; bool lead;
            var route = CapInterceptMath.GuideRoute(pos, tgt, kn, out ip, out lead);
            string err = _g.Patrol(m.Uid, route, (int)g.P.PatrolAltFt, m.Dash && m.DashStep > 0 ? m.DashStep : 0);
            ic.Ip = ip; ic.HasIp = true; ic.EtaS = AswMath.DistNm(pos, ip) / Math.Max(1, kn) * 3600.0; ic.EtaAt = now;
            if (err != null) { if (!ic.RouteErr) { ic.RouteErr = true; Log("intercept route not accepted uid=" + m.Uid + " target=" + ic.Target + " " + err); } }
            else ic.RouteErr = false;
            Dbg("intercept guide uid=" + m.Uid + " target=" + ic.Target + " nm=" + F(d) + " " + (lead ? "lead" : "pursuit") + " -> " + (err ?? "ok"));
            return true;
        }

        /// <summary>The end of one bird's intercept: the dash step goes back, a leash / pursuit end breaks the game's chase, and the bird takes its lane again (same slot, route signature cleared).</summary>
        private void EndIntercept(Grp g, Mem m, CapBird b, string why, bool restoreThrottle = true)
        {
            var ic = m.Icpt;
            if (ic == null) return;
            m.Icpt = null;
            if (restoreThrottle) RestoreThrottle(m);
            if (why == "beyond_pursuit" || why == "leash") { try { _g.BreakPursuit(m.Uid); } catch (Exception) { } }
            m.RouteSig = null; m.LastCircleRouteAt = -1e9; m.LastLeash = _now;
            DropTargetCache(ic.Target);
            Log("intercept end uid=" + m.Uid + " group=" + g.Name + " target=" + ic.Target + " why=" + why + " round=" + ic.Round + " dur_s=" + F(_now - ic.Since) + " slot=" + m.Slot);
            Evt("cap_intercept", "{\"group\":" + Q(g.Name) + ",\"op\":\"end\",\"unit\":" + m.Uid + ",\"target\":" + ic.Target + ",\"reason\":" + Q(why) + "}");
        }

        /// <summary>Forget the track cache of a target nobody chases any more.</summary>
        private void DropTargetCache(int target)
        {
            foreach (var g in _groups) foreach (var m in g.Members) if (m.Icpt != null && m.Icpt.Target == target) return;
            try { _g.IcptDrop(target); } catch (Exception) { }
        }

        /// <summary>The intercept fields of a cap_order (the names CapOrderCmd.IcptGiven holds): the group's settings change, the others stay.</summary>
        private void ApplyIcptOrder(Grp g, CapOrderCmd cmd)
        {
            if (g.I == null) g.I = CapInterceptSpec.FromParams(g.P);
            if (cmd.IcptGiven == null || cmd.IcptGiven.Count == 0 || cmd.Icpt == null) return;
            var src = cmd.Icpt; var gv = cmd.IcptGiven; var s = g.I;
            if (gv.Contains("on")) s.On = src.On;
            if (gv.Contains("margin")) s.MarginNm = src.MarginNm;
            if (gv.Contains("per_target")) s.PerTarget = src.PerTarget;
            if (gv.Contains("keep")) s.Keep = src.Keep;
            if (gv.Contains("targets")) s.Targets = src.Targets;
            if (gv.Contains("speed")) s.Speed = src.Speed;
        }

        // ================================================= window / cap_state ==========================================================
        private void FillIcptView(Grp g, Mem m, CapMemberView mv, double ffNow, Dictionary<int, double> ffHold)
        {
            double h;
            if (ffHold != null && ffHold.TryGetValue(m.Uid, out h)) mv.Ff = h;
            var ic = m.Icpt;
            if (ic == null) return;
            mv.Icpt = ic.Phase; mv.IcptTarget = ic.Target; mv.IcptRound = ic.Round;
            mv.IcptEtaS = ic.Phase == "guide" && ic.EtaS >= 0 ? Math.Max(0, ic.EtaS - (_now - ic.EtaAt)) : -1;
            mv.IcptLostS = ic.LostSince >= 0 ? Math.Max(0, _now - ic.LostSince) : -1;
            if (ic.HasIp) { mv.HasIp = true; mv.IpLat = ic.Ip.Lat; mv.IpLon = ic.Ip.Lon; }
        }

        private Dictionary<int, double> FfHoldMap()
        {
            var map = new Dictionary<int, double>();
            try { var fg = FfGuard; if (fg != null) foreach (var kv in fg.Book.Holding(_g.Now)) map[kv.Key] = kv.Value; } catch (Exception) { }
            return map;
        }

        private static void AppendIcptJson(StringBuilder sb, CapGroupView gv, List<CapMemberView> members)
        {
            var s = gv.Icpt; if (s == null) return;
            sb.Append(",\"intercept\":{\"on\":").Append(Json.Bool(s.On)).Append(",\"margin_nm\":").Append(Json.Num(s.MarginNm)).Append(",\"per_target\":").Append(s.PerTarget)
              .Append(",\"keep\":").Append(s.Keep).Append(",\"speed\":").Append(Json.Str(s.Speed)).Append(",\"lost_s\":").Append(Json.Num(s.LostS)).Append(",\"targets\":[");
            bool f = true;
            foreach (string t in (s.Targets ?? "").Split(',')) { string x = t.Trim(); if (x.Length == 0) continue; if (!f) sb.Append(','); f = false; sb.Append(Json.Str(x)); }
            sb.Append("]}");
            var byT = new SortedDictionary<int, List<int>>();
            foreach (var m in members) if (m.Icpt != null) { List<int> l; if (!byT.TryGetValue(m.IcptTarget, out l)) { l = new List<int>(); byT[m.IcptTarget] = l; } l.Add(m.Uid); }
            sb.Append(",\"intercepts\":[");
            bool first = true;
            foreach (var kv in byT)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"target\":").Append(kv.Key).Append(",\"uids\":[").Append(string.Join(",", kv.Value.ConvertAll(u => u.ToString(CultureInfo.InvariantCulture)).ToArray())).Append("]}");
            }
            sb.Append(']');
        }
    }
}
