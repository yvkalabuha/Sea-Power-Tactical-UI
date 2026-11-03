using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class StrikeController
    {
        private static string F1(double d) { return d.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string I(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        // the only wall clock of the controller: the duration of a plan / a warm-up call for the log (the execution itself runs on the game clock)
        private static long TimerStart() { return Stopwatch.GetTimestamp(); }
        private static double TimerMs(long t0) { return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; }

        /// <summary>group: recount the plan of the group (on command only; the auto mode asks for it through the queue, never inside the tick). Ack {plan_id, variants:[{kind, score, missiles, targets, T, eta_s, warnings}], ms, tof_src}.</summary>
        public string Plan(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            object chooseObj;
            if (cmd.TryGetValue("choose", out chooseObj)) return Choose(g, chooseObj);
            if (ExecRunning(g)) return Fail("executing");
            PlanOpts clearOpts = null;
            if (g.Clear != null && StrikeAutoMath.ClearActive(g.Clear.Stage))
            {
                if (g.Clear.Stage == "wait_sunk" && g.Clear.RewaveOpen) clearOpts = RewaveOpts(g, "button");   // the button recounts the rewave by the neighbour
                else return Fail("clearing");
            }
            else if (g.Clear != null) clearOpts = MainOpts(g, "button", "button");   // the clearing ended without the sinking: the plan is by the main target with the stage-2 aircraft, no new clear_pause
            double shiftS = -1;
            object shiftObj;
            if (cmd != null && cmd.TryGetValue("shift_s", out shiftObj))
            {
                if (!(shiftObj is double) || (double)shiftObj < 0 || (double)shiftObj > 3600) return Fail("bad_args", "shift_s");
                shiftS = (double)shiftObj;
            }
            var opts = clearOpts ?? new PlanOpts { Why = "button", Purpose = "button" };
            opts.ShiftS = shiftS;
            return PlanCore(g, opts);
        }

        /// <summary>T-342 C: the plan of a group for the button (Queued = false: the cold warm-up and the simulator are allowed) or from the queue (Queued: only the cache and the measured scale of the time of flight, no EstimateShot).
        /// Target / Only / Exclude / NoDeck / NoClearPause make the plans of the clearing (the rewave by the neighbour, stage 2 by the main target) without touching the target of the group.</summary>
        internal string PlanCore(StrikeGroup g, PlanOpts o)
        {
            if (o == null) o = new PlanOpts();
            long tAll = TimerStart();
            if (ExecRunning(g)) return Fail("executing");
            if (g.Timeline != null && g.Timeline.Status == "cancelled") Finish(g, _g.Now);   // the missiles of a cancelled strike are counted as they are
            var target = o.Target ?? g.Target;
            if (target == null || target.MainId == 0) return Fail("no_target");
            var ships = _g.Ships() ?? new List<StrikeShip>();
            if (o.Purpose == "clear_stage2" && g.Clear != null) ships = ships.Where(s => s != null && s.TrackId != g.Clear.NeighbourId).ToList();   // the neighbour is sunk: its zone is gone
            var main = ships.FirstOrDefault(s => s != null && s.TrackId == target.MainId);
            if (main == null && o.Target == null && target.Mode == "spread")
            {
                // T-346 D2: the main target of a spread set is sunk (gone from the list of ships): the main one is the first of the survivors; no auto stop (the player did not change the target)
                var promoted = StrikePlanMath.PromoteMain(target, ships, id => _killLogged.Contains(id), StrikeCalib.TonBandFrac);
                if (promoted != null)
                {
                    _log("[Strike] strike target " + g.Name + " promote main=" + I(promoted.MainId) + " from=" + I(target.MainId) + " why=main_gone");
                    g.Target = promoted; target = promoted;
                    main = ships.FirstOrDefault(s => s != null && s.TrackId == target.MainId);
                }
            }
            if (main == null) return Fail("no_target");
            string why = StrikePlanMath.Exclusion(main);
            if (why != null) return Fail("target_excluded", why);
            var air = new List<StrikeAircraft>();
            foreach (int uid in g.Members)
            {
                if (o.Only != null && !o.Only.Contains(uid)) continue;
                if (o.Exclude != null && o.Exclude.Contains(uid)) continue;
                var a = Air(uid); if (a != null && StrikeAirMath.RoleOf(a) == "striker") air.Add(a);
            }
            bool deckOnly = false;
            if (air.Count == 0)
            {
                // T-342 B: a group without an aircraft in the air still gets a plan "from the decks" when a deck can offer a strike loadout for this target
                if (o.NoDeck || !DeckCanOffer(g, main, air)) return Fail("no_aircraft");
                deckOnly = true;
            }
            var ews = new List<StrikeAircraft>();
            foreach (int uid in g.Members)
            {
                if (o.Exclude != null && o.Exclude.Contains(uid)) continue;
                var a = Air(uid); if (a != null && StrikeAirMath.RoleOf(a) == "ew") ews.Add(a);
            }
            double tMin = -1, shiftS = 0;
            if (o.ShiftS >= 0)
            {
                shiftS = o.ShiftS;
                double prevT = g.Chosen >= 0 && g.Variants != null && g.Chosen < g.Variants.Count ? g.Variants[g.Chosen].T : _g.Now;
                tMin = Math.Max(prevT, _g.Now) + shiftS;
            }
            var airborne = air.Where(a => a.Airborne).ToList();
            double now = _g.Now;
            GeoPt from = air.Count == 0 ? new GeoPt(main.Lat, main.Lon) : airborne.Count == 0
                ? new GeoPt(air.Average(a => a.Lat), air.Average(a => a.Lon))
                : new GeoPt(airborne.Average(a => a.Lat), airborne.Average(a => a.Lon));
            int sim = 0, rough = 0, prof = 0, profEx = 0;   // T-350: prof = calls answered by a flight profile, profEx = of them below its measured part
            if (!o.Queued)
                foreach (string wa in _tofs.NeedWarm(air.SelectMany(a => a.Missiles.Where(StrikeAirMath.IsStrikeMissile).Select(m => m.Ammo)).Where(a => !_warmFailed.Contains(a)))) WarmOne(wa);   // review of phase B: one cold call per missile type, not inside the plan
            Func<StrikeAircraft, StrikeMissile, StrikeShip, double, double> tof = (a, m, s, r) =>
            {
                double pt; bool pex;
                if (StrikeTofCache.TryProfile(m.Ammo, r, out pt, out pex)) { prof++; if (pex) profEx++; return pt; }   // T-350: the flight profile first (no cache, no scale, no TofScaleSup)
                if (o.Queued) { if (_tofs.HasSim(m.Ammo)) sim++; else rough++; return CheapTof(a, m, s, r); }   // T-342 C: the queue never asks the simulator
                double roughS = StrikeTotMath.RoughTofS(r, m.SpeedKn > 0 ? m.SpeedKn : 540);
                double alt = m.MinLaunchAltFt > 0 ? m.MinLaunchAltFt : a.CruiseAltFt;   // the missile leaves low: that is the altitude the cache and the game are asked for
                double sup = m.SpeedKn >= StrikeCalib.SupersonicKn ? StrikeCalib.TofScaleSup : 1.0;   // T-342: the residual bias of the supersonic missiles (the fit of E1 / E1b)
                double cached;
                if (_tofs.TryGet(m.Ammo, r, s.SpeedKn, alt, out cached)) { sim++; return cached * sup; }
                // a missile that was warmed up: the rough formula times its measured scale (a cold EstimateShot costs 15-50 ms, the plan has 20 for everything)
                if (_tofs.HasSim(m.Ammo)) { sim++; return roughS * _tofs.Scale(m.Ammo, r) * sup; }
                var sp = new GeoPt(s.Lat, s.Lon);
                var ap = new GeoPt(a.Lat, a.Lon);
                double brg = AswMath.BearingDeg(sp, ap);
                var launch = AswMath.Offset(sp, AswMath.DistNm(sp, ap) < 1e-6 ? 180 : brg, r);
                double t;
                var aim = StrikeGeoMath.Lead(s, roughS);
                bool ok = false;
                try { ok = _g.Tof(a.Uid, m, launch.Lat, launch.Lon, alt, a.CruiseKn, s, aim.Lat, aim.Lon, out t); }
                catch (Exception) { t = 0; }
                if (ok && t > 0) { sim++; _tofs.Put(m.Ammo, r, s.SpeedKn, alt, t, roughS); TofCheck(m.Ammo, t, roughS); return t * sup; }
                rough++;
                return roughS * _tofs.Scale(m.Ammo, r) * sup;
            };
            ApplyPrior(ships);
            bool bonusKnown; double bonus = Bonus(out bonusKnown);
            var inp = new StrikePlanInput { Air = air, Ships = ships, Target = target, From = from, Now = now, P = _p, TofS = tof, TMin = tMin, Difficulty = Diff(), Goal = g.Goal ?? "sink", DamageBonus = bonus, BonusKnown = bonusKnown, KillThreshold = _p.KillThreshold, NoClearPause = o.NoClearPause, IsLand = p => { try { return _g.IsLand(p); } catch (Exception) { return false; } } };
            long t0 = TimerStart();
            var variants = StrikePlanMath.Variants(inp);
            double ms = TimerMs(t0);
            string src = rough == 0 && sim > 0 ? "sim" : "rough";
            var airMap = new Dictionary<int, StrikeAircraft>();
            foreach (var a in air) airMap[a.Uid] = a;
            if (ews.Count > 0)
                foreach (var v in variants)
                {
                    try { StrikeEwMath.PlanEw(v, ews, ships, target, g.WaitPt, now, _p, inp.IsLand, airMap); }
                    catch (Exception e) { _log("[Strike] strike ew plan failed: " + e.GetType().Name + ": " + e.Message); }
                    foreach (var a in ews) if (!a.Airborne) v.Warnings.Add("ew_not_airborne:" + I(a.Uid));
                }
            g.ShiftS = shiftS;
            if (g.Wave > 1) foreach (var wv in variants) wv.Warnings.Add("later_wave");   // E1: the air defence of a damaged target is depleted (live: 5 and 7 leaks of 11 at sigma 0.15 in the second wave on the Bunker Hill)
            if (g.Clear != null && o.Purpose == "clear_rewave") foreach (var wv in variants) wv.Warnings.Add("clear_rewave:" + I(g.Clear.NeighbourId));   // the draft is a new wave by the neighbour, the button launches it
            else if (g.Clear != null && o.Purpose == "clear_main") foreach (var wv in variants) wv.Warnings.Add("clear_main:" + I(g.Clear.NeighbourId));   // the main target without the clearing
            g.Variants = variants;
            g.Chosen = variants.Count > 0 ? 0 : -1;
            g.Auto.ChosenByPlayer = false;   // T-342 C: a choice of the player is valid for the plan he chose from only
            g.TofSrc = src;
            g.Timeline = new StrikeTimeline { PlanId = ++_planId, CreatedT = now, Status = "draft", Snapshot = Snapshot(target, ships, air, now) };
            if (variants.Count > 0 && variants[0].Ew.Count > 0) g.Timeline.TDetect = variants[0].Ew.Where(e => e.TDetect > 0).Select(e => e.TDetect).DefaultIfEmpty(-1).Min();
            if (variants.Count > 0) foreach (var ep in variants[0].Ew) LogEwPlan(g, ep, now);
            int pending = g.DeckKeys.Count;
            if (pending > 0) foreach (var v in variants) v.Warnings.Add("deck_pending:" + I(pending));
            RecomputeWait(g, "plan");
            LogKillPlan(g, g.Timeline.PlanId, variants, bonus, bonusKnown);
            if (!o.NoDeck)
                try { if (!ExecRunning(g)) DeckAfterPlan(g, inp, variants); } catch (Exception e) { _log("[Strike] strike deck plan failed: " + e.GetType().Name + ": " + e.Message); }
            string line = "[Strike] strike plan " + g.Name + " id=" + I(g.Timeline.PlanId) + " variants=" + I(variants.Count);
            if (variants.Count > 0)
            {
                var v = variants[0];
                var mt = v.PerTarget.FirstOrDefault();
                line += " kind=" + v.Kind + " n=" + I(v.MissilesUsed) + " p=" + (mt == null ? "0" : F1(mt.P * 100) + "%") + " T=" + F1(v.T - now) + " spread_s=" + F1(v.PerTarget.Count == 0 ? 0 : v.PerTarget.Max(t => t.SpreadS));
            }
            _log(line + " ms=" + F1(ms) + " tof_src=" + src + " tof_prof=" + I(prof) + " tof_extrap=" + I(profEx) + " clearing=" + (inp.ClearingNote ?? "none") + " diff=" + F3(inp.Difficulty) + " calib=" + StrikeCalib.CalibId);
            if (ms > _p.PlanBudgetMs) _log("[Strike] strike plan over budget ms=" + F1(ms) + " budget=" + F1(_p.PlanBudgetMs));
            if (o.Queued) _log("[Strike] strike replan grp=" + g.Name + " why=" + (o.Why ?? "-") + " purpose=" + (o.Purpose ?? "-") + " ms=" + F1(TimerMs(tAll)) + " waited_s=" + F1(Math.Max(0, now - o.RequestedT)));
            var items = variants.Select(v => VariantJson(v, now));
            string extra = "\"plan_id\":" + I(g.Timeline.PlanId) + ",\"variants\":[" + string.Join(",", items) + "],\"ms\":" + N(ms) + ",\"tof_src\":" + Q(src) + ",\"tof_prof\":" + I(prof) + ",\"tof_extrap\":" + I(profEx)
                           + (variants.Count == 0 ? ",\"why\":" + Q(airborne.Count == 0 ? "no_airborne" : "no_plan") : "");
            return Ack(g.Name, extra);
        }

        private string Choose(StrikeGroup g, object o)
        {
            if (g.Variants == null || g.Variants.Count == 0) return Fail("no_plan");
            if (!(o is double) || !IsInt((double)o) || (int)(double)o < 0 || (int)(double)o >= g.Variants.Count) return Fail("bad_args", "choose");
            g.Chosen = (int)(double)o;
            g.Auto.ChosenByPlayer = true;
            _log("[Strike] strike choose " + g.Name + " chosen=" + I(g.Chosen) + " kind=" + g.Variants[g.Chosen].Kind);
            return Ack(g.Name, "\"chosen\":" + I(g.Chosen));
        }

        /// <summary>group: the plan is dropped (the variants, the timeline); the group stays and goes on waiting at its point.</summary>
        public string Cancel(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            if (g.Timeline != null && g.Timeline.Status == "executing") return CancelExec(g, "player");
            if (g.Timeline != null && g.Timeline.Status == "cancelled") { Finish(g, _g.Now); return Ack(g.Name, null); }
            EwOffAll(g, "cancel");
            DeckAbortAll(g, "cancel");
            AutoStop(g, "cancel"); ClearEnd(g, "cancel"); DropQueued(g);
            g.Variants = new List<StrikeVariant>(); g.Chosen = -1; g.Timeline = null;
            RecomputeWait(g, "cancel");
            _log("[Strike] strike cancel " + g.Name);
            return Ack(g.Name, null);
        }

        private static string VariantJson(StrikeVariant v, double now)
        {
            return "{\"kind\":" + Q(v.Kind) + ",\"goal\":" + Q(v.Goal ?? "breach") + ",\"red\":" + Json.Bool(v.Red) + ",\"score\":" + N(v.Score) + ",\"missiles\":" + I(v.MissilesUsed) + ",\"targets\":" + I(v.TargetsHit)
                   + ",\"T\":" + N(v.T) + ",\"eta_s\":" + N(v.T - now) + ",\"estimate\":" + Json.Bool(v.Estimate)
                   + ",\"warnings\":[" + string.Join(",", v.Warnings.Select(Q)) + "],\"ew\":[" + string.Join(",", v.Ew.Select(e => EwPlanJson(e, now))) + "]" + StagesJson(v) + "}";
        }

        /// <summary>clear_pause only (ADR-0133 section 7): the neighbour, the estimate of T of stage 2 and the two stages. Nothing hidden: the numbers are the card.</summary>
        private static string StagesJson(StrikeVariant v)
        {
            if (v.Kind != "clear_pause") return "";
            var sb = new StringBuilder(",\"neighbour\":" + I(v.ClearNeighbour) + ",\"t_stage2\":" + (v.TStage2 < 0 ? "null" : N(v.TStage2)) + ",\"stages\":[");
            int k = 0;
            foreach (var t in v.PerTarget.Where(x => x.Stage > 0).OrderBy(x => x.Stage))
            {
                if (k++ > 0) sb.Append(',');
                sb.Append("{\"tgt\":" + I(t.TrackId) + ",\"stage\":" + I(t.Stage) + ",\"n\":" + I(t.N) + ",\"p_sunk\":" + N(t.PSunk >= 0 ? t.PSunk : t.P));
                if (t.Stage == 2 && t.PIfAlive >= 0) sb.Append(",\"p_if_alive\":" + N(t.PIfAlive));
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static StrikeSnapshot Snapshot(StrikeTargetSet target, IList<StrikeShip> ships, IList<StrikeAircraft> air, double now)
        {
            var snap = new StrikeSnapshot { T = now };
            foreach (int id in target.Ships.Count > 0 ? target.Ships : new List<int> { target.MainId })
            {
                var s = ships.FirstOrDefault(x => x != null && x.TrackId == id);
                if (s != null) snap.Targets.Add(new StrikeSnapTarget { Id = s.TrackId, Lat = s.Lat, Lon = s.Lon, CourseDeg = s.CourseDeg, SpeedKn = s.SpeedKn });
            }
            snap.Targets.Sort((a, b) => (a.Id == target.MainId ? 0 : 1).CompareTo(b.Id == target.MainId ? 0 : 1));
            foreach (var s in ships)
                if (s != null && StrikePlanMath.Exclusion(s) == null && s.Ad.Ksub > 0) snap.AdShipIds.Add(s.TrackId);
            foreach (var a in air) snap.Air.Add(new StrikeSnapAir { Uid = a.Uid, Missiles = StrikeAirMath.UsableCount(a) });
            return snap;
        }

        private void MarkStale(StrikeGroup g, string why)
        {
            if (g.Timeline == null || (g.Timeline.Status != "draft" && g.Timeline.Status != "armed")) return;
            g.Timeline.Status = "stale"; g.Timeline.StaleWhy = why;
            _log("[Strike] strike stale " + g.Name + " why=" + why);
        }

        private void CheckStale(double now)
        {
            List<StrikeGroup> todo = _groups.Where(g => g.Timeline != null && (g.Timeline.Status == "draft" || g.Timeline.Status == "armed")).ToList();
            if (todo.Count == 0) return;
            var ships = _g.Ships() ?? new List<StrikeShip>();
            var air = _g.Aircraft() ?? new List<StrikeAircraft>();
            foreach (var g in todo)
            {
                string why = StrikeStaleMath.Why(g.Timeline.Snapshot, ships, air, _p, now);
                if (why != null) MarkStale(g, why);
            }
        }
    }
}
