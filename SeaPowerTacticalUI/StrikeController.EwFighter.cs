using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase D (plan 34.3, 43): the jam of hostile fighters, a layer over the states of the jammers (StrikeController.Ew.cs). After "Launch" (the group has EwExec) a jammer that may jam fighters
    // (StrikeEwFighterMath.CanJamFighters) takes the most urgent identified fighter that threatens the strikers (JamTask by the object of the track through IStrikeGame.JamObject), keeps at least R_t + FighterStandoffNm from every
    // such fighter (or goes to the standoff point behind the group, or retreats with the message when there is none), and switches the jam off EwFighterClearS after the last threat. The ships-role jammer does it on the way only
    // (in "jamming" it jams the ships); the reserve (role fighters) helps with the ships like the others and moves to a fighter at once, in any working state, and goes back to the ships after the clear time.
    // The state of the fighter table is read once per EwFighterTickS for all groups; nothing here asks for a plan, a time of flight or a hidden state.
    internal sealed partial class StrikeController
    {
        private IList<StrikeFighter> _fighters = new List<StrikeFighter>();
        private double _fightersT = double.NegativeInfinity;

        private sealed class FighterCtx
        {
            public List<StrikeFighterThreat> Threats = new List<StrikeFighterThreat>();
            public List<StrikeFighterThreat> Near = new List<StrikeFighterThreat>();   // every eligible fighter (threat or not): for the return from the retreat
            public List<StrikeAircraft> Strikers = new List<StrikeAircraft>();
            public GeoPt Center; public bool HasCenter;
        }

        internal IList<StrikeFighter> FightersNow(double now)
        {
            if (now < _fightersT || now - _fightersT >= StrikeCalib.EwFighterTickS)
            {
                _fightersT = now;
                try { _fighters = _g.Fighters() ?? new List<StrikeFighter>(); } catch (Exception) { _fighters = new List<StrikeFighter>(); }
            }
            return _fighters;
        }

        private FighterCtx FighterPrep(StrikeGroup g, double now)
        {
            var ctx = new FighterCtx();
            if (_p.EwFighterEtaS <= 0) { FighterThreatLog(g, ctx, now); return ctx; }
            double la = 0, lo = 0;
            foreach (int uid in g.Members)
            {
                var m = Air(uid);
                if (m == null || !m.Airborne || StrikeAirMath.RoleOf(m) != "striker") continue;
                ctx.Strikers.Add(m); la += m.Lat; lo += m.Lon;
            }
            if (ctx.Strikers.Count > 0) { ctx.Center = new GeoPt(la / ctx.Strikers.Count, lo / ctx.Strikers.Count); ctx.HasCenter = true; }
            else if (g.WaitPt.HasValue) { ctx.Center = g.WaitPt.Value; ctx.HasCenter = true; }
            if (!ctx.HasCenter) { FighterThreatLog(g, ctx, now); return ctx; }
            var fs = FightersNow(now);
            var near = ctx.Strikers.Count > 0 ? ctx.Strikers : new List<StrikeAircraft> { new StrikeAircraft { Uid = 0, Lat = ctx.Center.Lat, Lon = ctx.Center.Lon, Airborne = true } };
            ctx.Threats = StrikeEwFighterMath.Threats(fs, near, _p, g.FighterThreats, now);   // T-346 D3: with the memory of the group (hysteresis)
            foreach (var f in fs)
                if (StrikeEwFighterMath.Eligible(f))
                    ctx.Near.Add(new StrikeFighterThreat { F = f, DistNm = AswMath.DistNm(ctx.Center, new GeoPt(f.Lat, f.Lon)) });
            FighterThreatLog(g, ctx, now);
            return ctx;
        }

        /// <summary>The threats of the group for the view and the journal: a new threat and a gone one are logged once. T-346 D3: the entry line carries the ETA of the entry (= eta_in_s of the view),
        /// the gone line the current ETA, the time held and the reason.</summary>
        private void FighterThreatLog(StrikeGroup g, FighterCtx ctx, double now)
        {
            var before = new HashSet<int>(g.FighterThreats.Select(t => t.F.TrackId));
            foreach (var t in ctx.Threats)
                if (!before.Contains(t.F.TrackId))
                    _log("[Strike] strike ew fighter threat grp=" + g.Name + " tgt=" + I(t.F.TrackId) + " cls=" + (t.F.ClassName ?? "-") + " dist_nm=" + F1(t.DistNm) + " rt_nm=" + F1(t.F.RNm) + " eta_s=" + (t.EtaInS < 0 || double.IsInfinity(t.EtaInS) ? "-" : F1(t.EtaInS)));
            var cur = new HashSet<int>(ctx.Threats.Select(t => t.F.TrackId));
            foreach (var t in g.FighterThreats)
            {
                if (cur.Contains(t.F.TrackId)) continue;
                var f = _fighters.FirstOrDefault(x => x.TrackId == t.F.TrackId);
                _log("[Strike] strike ew fighter gone grp=" + g.Name + " tgt=" + I(t.F.TrackId) + " eta_s=" + (double.IsInfinity(t.EtaS) ? "-" : F1(t.EtaS)) + " held_s=" + F1(Math.Max(0, now - t.SinceT)) + " why=" + StrikeEwFighterMath.GoneWhy(f, t));
            }
            g.FighterThreats = ctx.Threats;
        }

        private void EwNote(StrikeGroup g, string note)
        {
            if (g.EwNotes.Contains(note)) return;
            g.EwNotes.Add(note);
            while (g.EwNotes.Count > StrikeCalib.AutoNotesMax) g.EwNotes.RemoveAt(0);
        }

        /// <summary>Before Decide: the standoff (a point or the retreat), the mode. Returns fighterUnsafe (no point to keep away from the fighter) and fightersFar (for the return from the retreat).</summary>
        private void FighterPre(StrikeGroup g, StrikeEwState s, StrikeAircraft a, GeoPt pos, FighterCtx ctx, IList<StrikeShip> hostiles, double now, out bool unsafeF, out bool far)
        {
            unsafeF = false; far = true;
            string mode = StrikeEwFighterMath.Mode(s.State, s.Role);
            if (mode != s.Mode) { _log("[Strike] strike ew mode uid=" + I(s.Uid) + " from=" + s.Mode + " to=" + mode); s.Mode = mode; }
            if (!ctx.HasCenter) { FighterClearStandoff(g, s, now); return; }
            far = ctx.Near.Count == 0 || StrikeEwFighterMath.FightersFar(pos, ctx.Near, _p.FighterStandoffNm);
            bool working = s.State == "to_station" || s.State == "on_station" || s.State == "jamming";
            if (!working) { FighterClearStandoff(g, s, now); return; }   // a retreating jammer keeps the reason it retreated for
            if (ctx.Threats.Count == 0 || StrikeEwFighterMath.SafeFrom(pos, ctx.Threats, _p.FighterStandoffNm)) { FighterClearStandoff(g, s, now); if (s.FighterWhy != null && s.FighterWhy.StartsWith("no_cover", StringComparison.Ordinal)) s.FighterWhy = null; return; }
            string why;
            var pt = StrikeEwFighterMath.StandoffPt(pos, ctx.Center, ctx.Threats, hostiles, _p, p => LandSafe(p), out why);
            if (!pt.HasValue)
            {
                unsafeF = true;
                var main = ctx.Threats[0].F;
                string w = "no_cover:" + I(main.TrackId);
                if (s.FighterWhy != w)
                {
                    s.FighterWhy = w;
                    _log("[Strike] strike ew fighter no_cover uid=" + I(s.Uid) + " tgt=" + I(main.TrackId) + " cls=" + (main.ClassName ?? "-") + " why=" + why);
                    EwNote(g, "ewf_no_cover:" + I(s.Uid) + ":" + (main.ClassName ?? "-"));
                }
                return;
            }
            if (s.FighterWhy != null && s.FighterWhy.StartsWith("no_cover", StringComparison.Ordinal)) s.FighterWhy = null;
            bool moved = !s.StandoffPt.HasValue || AswMath.DistNm(s.StandoffPt.Value, pt.Value) > 3;
            if (s.Sent == "standoff" && !moved) return;
            if (now < s.StandoffNextT) return;
            s.StandoffNextT = now + StrikeCalib.EwStandoffS;
            string err = _g.Goto(s.Uid, new List<GeoPt> { pt.Value }, 0);
            _log("[Strike] strike ew fighter standoff uid=" + I(s.Uid) + " pt=" + Pt(pt.Value) + " -> " + (err ?? "ok"));
            if (err == null) { s.StandoffPt = pt.Value; s.Sent = "standoff"; }
        }

        private void FighterClearStandoff(StrikeGroup g, StrikeEwState s, double now)
        {
            if (s.Sent != "standoff" && !s.StandoffPt.HasValue) return;
            bool wasSent = s.Sent == "standoff";
            s.StandoffPt = null;
            if (!wasSent) return;
            s.Sent = null;   // EwKeep sends the route to the station again; a jammer that was already on the station is put into its orbit
            if (s.State == "on_station" || s.State == "jamming")
            {
                var pl = s.Plan;
                if (pl != null) { string err = _g.Orbit(s.Uid, pl.StationPt, _p.EwOrbitNm); _log("[Strike] strike ew fighter standoff_end uid=" + I(s.Uid) + " -> " + (err ?? "ok")); }
            }
        }

        /// <summary>After Decide of every jammer of the group: who jams whom, the switches, the clear time, the return to the ships.</summary>
        private void AssignFighters(StrikeGroup g, FighterCtx ctx, double now)
        {
            if (!ctx.HasCenter) ctx = new FighterCtx();
            var js = new List<StrikeEwJammer>();
            var byUid = new Dictionary<int, StrikeAircraft>();
            foreach (var s in g.EwExec)
            {
                var a = Air(s.Uid);
                if (a == null || s.Plan == null || !a.Airborne || !StrikeEwFighterMath.CanJamFighters(s.State, s.Role) || s.FighterWhy == "jam_refused") continue;
                byUid[s.Uid] = a;
                js.Add(new StrikeEwJammer { Uid = s.Uid, Pos = new GeoPt(a.Lat, a.Lon), ConeDeg = a.Ew != null && a.Ew.ConeDeg > 0 ? a.Ew.ConeDeg : 30, Role = s.Role, Free = true, Current = s.JamKind == "object" ? s.FighterId : 0, CurrentSinceT = s.FighterOnT });
            }
            List<int> unc;
            var jobs = StrikeEwFighterMath.Assign(js, ctx.Threats.Where(t => t.OutSinceT < 0 || js.Any(j => j.Current == t.F.TrackId)).ToList(), now, _p, out unc);   // T-346 D3: a threat held only by the hysteresis is not given to a new jammer, but the current jam target stays while the threat is in the list (clear counts from its exit)
            if (!(unc.SequenceEqual(g.FighterUncovered)))
            {
                g.FighterUncovered = unc;
                if (unc.Count > 0) { _log("[Strike] strike ew fighter uncovered grp=" + g.Name + " n=" + I(unc.Count) + " ids=[" + string.Join(",", unc.Select(I)) + "]"); EwNote(g, "ewf_uncovered:" + I(unc.Count)); }
            }
            foreach (var s in g.EwExec.ToList())
            {
                var job = jobs.FirstOrDefault(j => j.Uid == s.Uid);
                StrikeAircraft a;
                if (!byUid.TryGetValue(s.Uid, out a)) continue;
                var pos = new GeoPt(a.Lat, a.Lon);
                if (job != null)
                {
                    var thr = ctx.Threats.First(t => t.F.TrackId == job.TargetId);
                    if (s.JamKind == "object" && s.FighterId == job.TargetId) { s.FighterCover = job.Cover; s.FighterLastThreatT = now; continue; }
                    if (now < s.FighterNextT) continue;
                    if (!StrikeEwFighterMath.SafeFrom(pos, ctx.Threats, _p.FighterStandoffNm)) continue;   // the standoff comes first: the stations are not switched on inside R_t + the margin
                    if (s.JamKind == "object") EwFighterOff(g, s, "switch");
                    EwFighterOn(g, s, thr, job.Cover, now);
                    continue;
                }
                if (s.JamKind != "object") continue;
                // no job: the clear time counts from the last moment the current target was still a threat
                var f = _fighters.FirstOrDefault(x => x.TrackId == s.FighterId);
                if (f != null && StrikeEwFighterMath.StillThreat(f, ctx.Strikers)) s.FighterLastThreatT = now;
                if (s.FighterLastThreatT < 0) s.FighterLastThreatT = now;
                if (now - s.FighterLastThreatT >= StrikeCalib.EwFighterClearS) EwFighterOff(g, s, "clear");
            }
            // the reserve that was jamming the ships goes back to them
            foreach (var s in g.EwExec)
                if (s.State == "jamming" && !s.On && s.JamKind == null && s.Plan != null && now >= s.NextTryT && now >= s.FighterNextT && Air(s.Uid) != null)
                {
                    string err = EwOn(g, s, s.Plan);
                    if (err != null) s.NextTryT = now + _p.EwRetryS;
                    else { s.OnT = now; _log("[Strike] strike ew fighter ships_resume uid=" + I(s.Uid)); }
                }
            FighterShipsLost(g);
        }

        private string EwFighterOn(StrikeGroup g, StrikeEwState s, StrikeFighterThreat t, IList<int> cover, double now)
        {
            if (s.On && s.JamKind != "object") EwOff(g, s, "fighter");   // the reserve leaves the ships first (one station = one task)
            string err = null;
            try { err = _g.JamObject(s.Uid, t.F.TrackId, true); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
            _log("[Strike] strike ew fighter on uid=" + I(s.Uid) + " tgt=" + I(t.F.TrackId) + " cls=" + (t.F.ClassName ?? "-") + " dist_nm=" + F1(t.DistNm) + " rt_nm=" + F1(t.F.RNm) + " cover=[" + string.Join(",", cover.Select(I)) + "] -> " + (err ?? "ok"));
            if (err == null)
            {
                s.On = true; s.JamKind = "object"; s.FighterId = t.F.TrackId; s.FighterCover = new List<int>(cover); s.FighterOnT = now; s.FighterLastThreatT = now; s.FighterNextT = now + StrikeCalib.EwFighterSwitchS; s.FighterTries = 0;
                EwNote(g, "ewf_on:" + I(s.Uid) + ":" + I(t.F.TrackId));
                return null;
            }
            try { _g.JamObject(s.Uid, t.F.TrackId, false); } catch (Exception) { }   // a refused order may have left the stations on
            s.On = false; s.JamKind = null;
            s.FighterTries++; s.FighterNextT = now + _p.EwRetryS;
            if (s.FighterTries >= 3)
            {
                s.FighterWhy = "jam_refused";
                _log("[Strike] strike ew fighter refused uid=" + I(s.Uid) + " why=" + err);
            }
            return err;
        }

        /// <summary>Switches the jam of a fighter off (cease + IsOn false); the reason: clear | switch | attack_ships | guard | retreat | back | off | lost | left_group | cancel | loaded | strike_over | fighter.</summary>
        private void EwFighterOff(StrikeGroup g, StrikeEwState s, string why)
        {
            if (s.JamKind != "object") return;
            string err = null;
            try { err = _g.JamObject(s.Uid, s.FighterId, false); } catch (Exception e) { err = "failed:" + e.GetType().Name; }
            _log("[Strike] strike ew fighter off uid=" + I(s.Uid) + " tgt=" + I(s.FighterId) + " -> " + (err ?? "ok") + " why=" + why);
            EwNote(g, "ewf_off:" + I(s.Uid) + ":" + why);
            s.On = false; s.JamKind = null; s.FighterId = 0; s.FighterCover = new List<int>(); s.FighterNextT = _g.Now + StrikeCalib.EwFighterSwitchS;
            if (s.ShipsLost.Count > 0) { s.ShipsLost = new List<int>(); }
        }

        /// <summary>The ships a jammer covered and nobody else jams while it is on a fighter (shown in the window).</summary>
        private void FighterShipsLost(StrikeGroup g)
        {
            foreach (var s in g.EwExec)
            {
                var lost = new List<int>();
                if (s.JamKind == "object" && s.State == "jamming" && s.Plan != null)
                    foreach (int id in s.Plan.CoverIds)
                        if (!g.EwExec.Any(o => o != s && o.State == "jamming" && o.On && o.JamKind == "point" && o.Plan != null && o.Plan.CoverIds.Contains(id))) lost.Add(id);
                if (lost.SequenceEqual(s.ShipsLost)) continue;
                _log("[Strike] strike ew fighter " + (lost.Count > 0 ? "ships_lost" : "ships_back") + " uid=" + I(s.Uid) + " n=" + I(lost.Count) + " ids=[" + string.Join(",", lost.Select(I)) + "]");
                if (lost.Count > 0) EwNote(g, "ewf_ships_lost:" + I(s.Uid) + ":" + I(lost.Count));
                s.ShipsLost = lost;
            }
        }

        /// <summary>The fighter fields of a jammer row of the window (the same state as FighterStateJson).</summary>
        private void EwFighterRow(StrikeEwRow r, StrikeEwState s, StrikeAircraft a)
        {
            r.Mode = s.Mode; r.Role = s.Role; r.JamKind = s.JamKind; r.FighterId = s.FighterId; r.FighterWhy = s.FighterWhy; r.ShipsLost = new List<int>(s.ShipsLost);
            if (a != null) r.JamPos = new GeoPt(a.Lat, a.Lon);
            var f = s.JamKind == "object" ? _fighters.FirstOrDefault(x => x.TrackId == s.FighterId) : null;
            if (f != null)
            {
                r.FighterCls = f.ClassName; r.FighterRtNm = f.RNm; r.FighterIrOnly = f.IrOnly; r.FighterPt = new GeoPt(f.Lat, f.Lon);
                if (a != null) r.FighterDistNm = AswMath.DistNm(new GeoPt(a.Lat, a.Lon), new GeoPt(f.Lat, f.Lon));
            }
        }

        private string FighterGroupJson(StrikeGroup g)
        {
            var sb = new System.Text.StringBuilder("\"fighter_threats\":[");
            int n = 0;
            foreach (var t in g.FighterThreats)
            {
                if (n++ > 0) sb.Append(',');
                var jam = g.EwExec.FirstOrDefault(s => s.JamKind == "object" && (s.FighterId == t.F.TrackId || s.FighterCover.Contains(t.F.TrackId)));
                sb.Append("{\"tgt\":" + I(t.F.TrackId) + ",\"cls\":" + Q(t.F.ClassName) + ",\"dist_nm\":" + N(t.DistNm) + ",\"rt_nm\":" + N(t.F.RNm) + ",\"eta_s\":" + (double.IsInfinity(t.EtaS) ? "null" : N(t.EtaS))
                          + ",\"eta_in_s\":" + (t.EtaInS < 0 || double.IsInfinity(t.EtaInS) ? "null" : N(t.EtaInS)) + ",\"since_t\":" + N(t.SinceT) + ",\"closing_kn\":" + N(t.ClosingKn) + ",\"ir_only\":" + Json.Bool(t.F.IrOnly) + ",\"jammed_by\":" + (jam == null ? "null" : I(jam.Uid)) + "}");
            }
            sb.Append("],\"fighter_uncovered\":[").Append(string.Join(",", g.FighterUncovered.Select(I))).Append("],\"ew_notes\":[").Append(string.Join(",", g.EwNotes.Select(Q))).Append(']');
            return sb.ToString();
        }

        private string FighterStateJson(StrikeEwState s)
        {
            var f = s.JamKind == "object" ? _fighters.FirstOrDefault(x => x.TrackId == s.FighterId) : null;
            string fj = "null";
            if (s.JamKind == "object")
            {
                var a = Air(s.Uid);
                double d = f != null && a != null ? AswMath.DistNm(new GeoPt(a.Lat, a.Lon), new GeoPt(f.Lat, f.Lon)) : -1;
                fj = "{\"tgt\":" + I(s.FighterId) + ",\"cls\":" + Q(f == null ? null : f.ClassName) + ",\"dist_nm\":" + (d < 0 ? "null" : N(d)) + ",\"rt_nm\":" + (f == null ? "null" : N(f.RNm)) + ",\"on_t\":" + N(s.FighterOnT) + ",\"ir_only\":" + Json.Bool(f != null && f.IrOnly) + "}";
            }
            return ",\"mode\":" + Q(s.Mode) + ",\"role\":" + Q(s.Role) + ",\"jam_kind\":" + (s.JamKind == null ? "null" : Q(s.JamKind)) + ",\"fighter\":" + fj + ",\"fighters\":[" + string.Join(",", s.FighterCover.Select(I)) + "]"
                   + ",\"fighter_why\":" + (s.FighterWhy == null ? "null" : Q(s.FighterWhy)) + ",\"ships_lost\":[" + string.Join(",", s.ShipsLost.Select(I)) + "]";
        }
    }
}
