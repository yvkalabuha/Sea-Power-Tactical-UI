using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    // T-342 phase C (ADR-0133 section 4; plan 23.2): the auto mode of a group. The flag is the player's: off by default, never restored from a save, switched off by every stop. The rules are the pure
    // StrikeAutoMath.Decide; this file reads the world into its input once a tick and executes the action: a queued replan, a launch (LaunchCore, the same checks as the button), a stop with the reason.
    internal sealed partial class StrikeController
    {
        /// <summary>Open question of the customer (plan 30, 1): does switching the flag on again after the stop "3 waves" give 3 more waves? Yes by default (the manual launch does the same). false = only a change of the target or a manual launch resets the count, and the flag then answers !waves.</summary>
        internal static bool AutoOnResetsWaves = true;

        private readonly Dictionary<string, string> _autoLogged = new Dictionary<string, string>();

        /// <summary>strike_auto {group, on}. Answer {group, auto:{on, wave, max}, plan_status}; errors !no_group, !bad_args|group, !bad_args|on, !no_target, !waves.</summary>
        public string SetAuto(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            object o;
            if (cmd == null || !cmd.TryGetValue("on", out o) || !(o is bool)) return Fail("bad_args", "on");
            bool on = (bool)o;
            double now = _g.Now;
            if (on && !g.Auto.On)
            {
                if (g.Target == null || g.Target.MainId == 0) return Fail("no_target");
                if (!AutoOnResetsWaves && g.Auto.Waves >= _p.AutoWaves) return Fail("waves");
                g.Auto.On = true; g.Auto.State = "wait"; g.Auto.Why = null; g.Auto.StopWhy = null; g.Auto.OnT = now; g.Auto.LastReplanT = -1;
                if (AutoOnResetsWaves) g.Auto.Waves = 0;
                AutoNote(g, "auto_on");
                _log("[Strike] strike auto grp=" + g.Name + " wave=" + I(g.Auto.Waves) + " max=" + I(_p.AutoWaves) + " action=on why=player");
            }
            else if (!on && g.Auto.On)
            {
                g.Auto.On = false; g.Auto.State = "off"; g.Auto.Why = null;
                DropQueuedAuto(g);
                _log("[Strike] strike auto grp=" + g.Name + " wave=" + I(g.Auto.Waves) + " max=" + I(_p.AutoWaves) + " action=off why=player");
            }
            string st = g.Timeline == null ? "none" : g.Timeline.Status;
            return Ack(g.Name, "\"auto\":{\"on\":" + Json.Bool(g.Auto.On) + ",\"wave\":" + I(g.Auto.Waves) + ",\"max\":" + I(_p.AutoWaves) + "},\"plan_status\":" + Q(st));
        }

        private void DropQueuedAuto(StrikeGroup g) { _replanQ.RemoveAll(kv => kv.Key == g && kv.Value.Purpose == "auto"); }

        private void AutoNote(StrikeGroup g, string note)
        {
            g.Auto.Notes.Add(note);
            while (g.Auto.Notes.Count > StrikeCalib.AutoNotesMax) g.Auto.Notes.RemoveAt(0);
        }

        /// <summary>The flag goes off with the reason (and the window says it). Not on = nothing to do. An execution that runs goes on; no new wave follows.</summary>
        internal void AutoStop(StrikeGroup g, string why)
        {
            if (g == null || !g.Auto.On) return;
            g.Auto.On = false; g.Auto.State = "stop"; g.Auto.Why = null; g.Auto.StopWhy = why;
            DropQueuedAuto(g);
            AutoNote(g, "auto_stop:" + why);
            _log("[Strike] strike auto grp=" + g.Name + " wave=" + I(g.Auto.Waves) + " max=" + I(_p.AutoWaves) + " action=stop why=" + why);
        }

        private void ResetAuto()
        {
            _replanQ.Clear(); _autoLogged.Clear();
            foreach (var g in _groups) { g.Auto = new StrikeAutoState(); g.Clear = null; }
        }

        private int StrikeMissilesOf(StrikeGroup g)
        {
            int n = 0;
            foreach (int uid in g.Members) { var a = Air(uid); if (a != null && a.Airborne && StrikeAirMath.RoleOf(a) == "striker") n += StrikeAirMath.UsableCount(a); }
            return n;
        }

        private StrikeAutoInput AutoInput(StrikeGroup g, double t)
        {
            int targets = 0;
            var ids = new List<int>(g.Target.Ships);
            if (g.Target.MainId != 0 && !ids.Contains(g.Target.MainId)) ids.Add(g.Target.MainId);
            foreach (int id in ids)
            {
                var s = Ship(id);
                if (s != null && StrikePlanMath.Exclusion(s) == null && !_killLogged.Contains(id)) targets++;
            }
            var x = new StrikeAutoInput
            {
                On = g.Auto.On, Waves = g.Auto.Waves, MaxWaves = _p.AutoWaves, Plan = g.Timeline == null ? "none" : g.Timeline.Status, ExecRunning = ExecRunning(g), ReplanQueued = ReplanQueued(g),
                Now = t, LastReplanT = g.Auto.LastReplanT, LiveDeck = DeckLiveCount(g), ClearStage = g.Clear == null ? null : g.Clear.Stage, TargetsLeft = targets, Members = g.Members.Count,
                Missiles = StrikeMissilesOf(g), Chosen = g.Chosen, ChosenByPlayer = g.Auto.ChosenByPlayer, Variants = new List<StrikeAutoVariant>(),
            };
            for (int i = 0; g.Variants != null && i < g.Variants.Count; i++)
            {
                var v = g.Variants[i];
                bool air = v.Assignments.Count > 0;
                foreach (int uid in v.Assignments.Select(a => a.Uid).Distinct()) { var a = Air(uid); if (a == null || !a.Airborne || !g.Members.Contains(uid)) air = false; }
                string block = null;
                if (v.Assignments.Count == 0) block = "no_rows";
                foreach (string w in v.Warnings)
                {
                    if (w.StartsWith("gate:", StringComparison.Ordinal)) block = "gate";
                    else if (w.StartsWith("launch_land:", StringComparison.Ordinal) && block == null) block = "launch_land";
                }
                if (block == null && (g.WaitWhy == "no_sea" || g.WaitWhy == "no_point")) block = "wait_no_sea";
                x.Variants.Add(new StrikeAutoVariant { Index = i, Kind = v.Kind, Red = v.Red, AllAirborne = air, Block = block });
            }
            return x;
        }

        private void TickAuto(double t)
        {
            foreach (var g in _groups.ToList())
            {
                if (!g.Auto.On) continue;
                try { AutoStep(g, t); } catch (Exception e) { _log("[Strike] strike auto failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private void AutoStep(StrikeGroup g, double t)
        {
            var x = AutoInput(g, t);
            var act = StrikeAutoMath.Decide(x, _p);
            g.Auto.State = act.Kind == "none" ? "off" : act.Kind;
            g.Auto.Why = act.Why;
            string key = act.Kind + "|" + act.Why + "|" + I(act.Variant);
            string last;
            if (act.Kind != "launch" && act.Kind != "stop" && (!_autoLogged.TryGetValue(g.Name, out last) || last != key))
            {
                _autoLogged[g.Name] = key;
                _log("[Strike] strike auto grp=" + g.Name + " wave=" + I(g.Auto.Waves) + " max=" + I(_p.AutoWaves) + " action=" + act.Kind + " why=" + (act.Why ?? "-"));
            }
            switch (act.Kind)
            {
                case "replan":
                    g.Auto.LastReplanT = t;
                    QueueReplan(g, new PlanOpts { Why = "auto:" + act.Why, Purpose = "auto" });
                    break;
                case "launch":
                    AutoLaunch(g, act, t);
                    break;
                case "stop":
                    AutoStop(g, act.Why);
                    break;
            }
        }

        private void AutoLaunch(StrikeGroup g, StrikeAutoAction act, double t)
        {
            var v = g.Variants[act.Variant];
            var main = v.PerTarget.FirstOrDefault(p => p.TrackId == g.Target.MainId && (p.Stage == 0 || p.Stage == 2)) ?? v.PerTarget.FirstOrDefault();
            double pSunk = main == null ? 0 : (main.PSunk >= 0 ? main.PSunk : main.P);
            string r = LaunchCore(g, act.Variant, "auto", true);
            if (IsErr(r))
            {
                string code = r.Substring(1); int bar = code.IndexOf('|'); if (bar >= 0) code = code.Substring(0, bar);
                AutoStop(g, "launch_err:" + code);
                return;
            }
            g.Auto.State = "launch";
            _autoLogged.Remove(g.Name);
            AutoNote(g, "auto_launch:" + I(g.Auto.Waves) + ":" + v.Kind);
            if (act.Red) AutoNote(g, "auto_red:" + F2d(pSunk) + ":" + F2d(_p.KillThreshold));
            _log("[Strike] strike auto grp=" + g.Name + " wave=" + I(g.Auto.Waves) + " max=" + I(_p.AutoWaves) + " action=launch why=" + (act.Why ?? "-") + " var=" + I(act.Variant) + " kind=" + v.Kind + " red=" + (act.Red ? "1" : "0")
                 + (act.Red ? " note=threshold_not_reached p=" + F2d(pSunk) + " th=" + F2d(_p.KillThreshold) : ""));
        }

        private static string F2d(double d) { return d.ToString("0.##", CultureInfo.InvariantCulture); }

        /// <summary>A queued plan of the auto failed (the group lost its aircraft, the target left): the flag stops with the reason, so it does not ask again every few seconds.</summary>
        private void AutoPlanFailed(StrikeGroup g, string err)
        {
            if (!g.Auto.On || err == null) return;
            if (err.StartsWith("!executing", StringComparison.Ordinal)) return;
            if (err.StartsWith("!no_aircraft", StringComparison.Ordinal)) AutoStop(g, "empty_group");
            else if (err.StartsWith("!no_target", StringComparison.Ordinal)) AutoStop(g, "no_targets");
            else if (err.StartsWith("!target_excluded", StringComparison.Ordinal)) AutoStop(g, "recheck");
            else AutoStop(g, "no_plan");
        }

        /// <summary>"auto" of a group for ui_state (ADR-0133 section 7).</summary>
        internal string AutoJson(StrikeGroup g)
        {
            var a = g.Auto;
            return "{\"on\":" + Json.Bool(a.On) + ",\"wave\":" + I(a.Waves) + ",\"max\":" + I(_p.AutoWaves) + ",\"state\":" + Q(a.State) + ",\"why\":" + (a.Why == null ? "null" : Q(a.Why)) + ",\"stop_why\":" + (a.StopWhy == null ? "null" : Q(a.StopWhy))
                   + ",\"chosen_by_player\":" + Json.Bool(a.ChosenByPlayer) + ",\"notes\":[" + string.Join(",", a.Notes.Select(Q)) + "]}";
        }
    }
}
