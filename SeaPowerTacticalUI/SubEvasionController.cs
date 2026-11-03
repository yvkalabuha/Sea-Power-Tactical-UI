using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Ui
{
    internal sealed class SubEvasionParams
    {
        public string Mode = "native";
        public bool DeepDive = true, NoisemakerAssist = true, Restore = true, Debug;
        public double NoisemakerIntervalS = 30;
    }

    /// <summary>One own submarine as the controller sees it (every member optional: null / -1 = unknown).</summary>
    internal sealed class SubUnit
    {
        public int Uid;
        public bool Alive;
        public string StateName;
        public int? StatePriority;
        public bool? PlayerOverride;
        public double HeadingDeg, Kn, DepthFt;
        public int? Preset, Telegraph;
        public bool? Panic, Cavitating, CavForbidden;
        public string Hiding;
        public int NoisemakersLeft = -1;
    }

    /// <summary>The game's ThreatingTorpedo of the submarine. Known = the player knows it (plotting table or announcement).</summary>
    internal sealed class SubThreat
    {
        public int TrackId;
        public double BrgDeg, DistNm;
        public bool Known = true;
        public double? TorpCourseDeg, TorpMaxDepthUnity, Preset6Ft, SeabedUnity;
    }

    /// <summary>What the restore puts back (own memory of the run, ADR-0071 item 4; ControlMemory is not used).</summary>
    internal sealed class SubSnapshot
    {
        public List<double[]> Route = new List<double[]>();
        public double? CourseDeg;
        public int? Telegraph, Preset;
        public double? DepthFt;
        public bool? CavForbidden, PlayerOverride;
    }

    /// <summary>T-101: what the controller needs from the game. Orders: the native state, waypoint, telegraph, depth preset, cavitation limit, one noisemaker, time compression.</summary>
    internal interface ISubEvasionGame
    {
        bool AutoEvadeOn(int uid);                 // ResolveFlag(u, "auto_evade")
        SubUnit Sub(int uid);                      // null = gone / not a submarine
        SubThreat Threat(int uid);                 // null = no live ThreatingTorpedo; Known=false = the player does not know it (NearestKnownTorpedo)
        bool NativeOk { get; }                     // capability sub_evasion_native
        bool OwnOk { get; }                        // set_telegraph present (waypoints are Common)
        bool StartNative(int uid);                 // setPlayerCommandOverride(false) if set, then StateMachine.setState(_state_torpedoEvasion)
        bool FinishNative(int uid);                // TorpedoEvasion.IsFinished = true (member optional) -> false
        SubSnapshot Remember(int uid);
        string Restore(int uid, SubSnapshot s, bool full);   // full=false: only PlayerOverride/CavForbidden; returns "route=.. tg=a->b depth=p->q|skipped"
        bool PlaceCourse(int uid, double courseDeg);          // point 20 nm ahead
        bool SetTelegraph(int uid, int step);
        bool SetPreset(int uid, int preset);
        bool SetCavitationForbidden(int uid, bool on);
        bool LaunchNoisemaker(int uid);
        void SetUnitAutoEvade(int uid);            // VB_AutoEvade=true at the unit level
        void TimeToNormal();                       // set_time_compression 1
        void Log(string line);
        void Event(string name, string dataJson);  // "auto_evade" with "kind":"sub"
    }

    /// <summary>T-101 / ADR-0071: runs of the submarine torpedo evasion (native wrapper / own fallback), the button and the automatic start.
    /// Never fire control, never the rules of engagement, never a counter-launch.</summary>
    internal sealed partial class SubEvasionController
    {
        private sealed class Run
        {
            public int Uid;
            public string Mode, By;
            public double StartedS, EnteredS, LastLaunchS = -1e9, LastCourseSent = -999, LastDist = double.MaxValue;
            public SubSnapshot Saved;
            public EvState S = new EvState();
            public bool Confirmed, PendingMod, CavChanged, DeepNoted;
            public int LastLeft = -1;
            public string TickKey;
            public int LastPresetSent = -1;
        }

        private readonly ISubEvasionGame _g;
        private readonly SubEvasionParams _p;
        private readonly Dictionary<int, Run> _runs = new Dictionary<int, Run>();
        private readonly Dictionary<int, double> _ended = new Dictionary<int, double>();
        private readonly Dictionary<int, double> _next = new Dictionary<int, double>();
        private readonly Dictionary<int, string> _lastSkip = new Dictionary<int, string>();
        private double _nowS;
        private int _started, _finished, _overridden, _released, _nmGame, _nmMod;

        public SubEvasionController(ISubEvasionGame g, SubEvasionParams p)
        {
            if (g == null) throw new ArgumentNullException("g");
            _g = g; _p = p ?? new SubEvasionParams();
        }

        public bool Holds(int uid) { return _runs.ContainsKey(uid); }

        public string ModeOf(int uid) { Run r; return _runs.TryGetValue(uid, out r) ? r.Mode : null; }

        private static string F(double d) { return d.ToString("0.#", CultureInfo.InvariantCulture); }

        private static string I(int d) { return d.ToString(CultureInfo.InvariantCulture); }

        private void Log(string s) { _g.Log("[Evasion] sub " + s); }

        // ------------------------------------------------------------------ start

        /// <summary>From AutoEvadeHook, once per submarine and pump pass; the controller keeps its own 1 s pace (mission time).</summary>
        public void Tick(int uid, double nowS)
        {
            _nowS = nowS;
            double next;
            if (_next.TryGetValue(uid, out next) && nowS < next && next - nowS <= 5) return;
            _next[uid] = nowS + SubEvasionMath.TickS;
            SubUnit u = _g.Sub(uid);
            Run r;
            _runs.TryGetValue(uid, out r);
            if (u == null || !u.Alive)
            {
                if (r != null) End(r, "unit_gone", nowS);
                return;
            }
            SubThreat th = _g.Threat(uid);
            if (r != null) { TickRun(r, u, th, nowS); return; }
            double ended;
            double since = _ended.TryGetValue(uid, out ended) ? nowS - ended : 1e9;
            string why = SubEvasionMath.StartDecision(_g.AutoEvadeOn(uid), th != null, th != null && th.Known, u.StateName, u.StatePriority, since, false);
            if (why == null)
            {
                if (!CanRun()) { SkipOnce(uid, "no_caps"); return; }
                _lastSkip.Remove(uid);
                StartRun(u, th, "auto", nowS);
                return;
            }
            if (why == "no_flag" || why == "no_threat" || why == "already") { _lastSkip.Remove(uid); return; }
            SkipOnce(uid, why);
        }

        private bool CanRun() { return _g.NativeOk || _g.OwnOk; }

        private void SkipOnce(int uid, string why)
        {
            string prev;
            if (_lastSkip.TryGetValue(uid, out prev) && prev == why) return;
            _lastSkip[uid] = why;
            Log("skip uid=" + I(uid) + " reason=" + why);
        }

        /// <summary>started | armed | already | skipped:&lt;why&gt;.</summary>
        public string Button(int uid, double nowS)
        {
            _nowS = nowS;
            SubUnit u = _g.Sub(uid);
            if (u == null || !u.Alive) return "skipped:no_unit";
            if (!CanRun()) return "skipped:unsupported";
            if (_runs.ContainsKey(uid)) return "already";
            _g.SetUnitAutoEvade(uid);
            _g.TimeToNormal();
            SubThreat th = _g.Threat(uid);
            string why = SubEvasionMath.StartDecision(true, th != null, th != null && th.Known, u.StateName, u.StatePriority, 1e9, true);
            if (why == "already") return "already";
            if (why == "no_threat" || why == "no_known_torpedo") return "armed";
            if (why != null) return "skipped:" + why;
            _lastSkip.Remove(uid);
            Run r = StartRun(u, th, "button", nowS);
            return r == null || !_runs.ContainsKey(uid) ? "skipped:start_failed" : "started";
        }

        private Run StartRun(SubUnit u, SubThreat th, string by, double nowS)
        {
            int uid = u.Uid;
            var r = new Run { Uid = uid, By = by, StartedS = nowS, EnteredS = nowS, LastLeft = u.NoisemakersLeft };
            r.S.Phase = "evading"; r.S.StartedS = nowS; r.S.LastSeenS = nowS;
            r.Saved = _g.Remember(uid) ?? new SubSnapshot();
            string mode = _p.Mode == "own" || !_g.NativeOk ? "own" : "native";
            if (mode == "own" && !_g.OwnOk) mode = "native";
            string note = null;
            if (mode == "native" && _g.OwnOk
                && SubEvasionMath.DeepDiveSwitch(_p.DeepDive, null, SubEvasionMath.CanOutdive(th.TorpMaxDepthUnity, th.Preset6Ft, th.SeabedUnity)) == "deep_dive_off")
            {
                mode = "own"; note = "deep_dive_off";
            }
            r.Mode = mode;
            _runs[uid] = r;
            _started++;
            Log("start uid=" + I(uid) + " torp=" + I(th.TrackId) + " brg=" + F(th.BrgDeg) + " dist=" + F(th.DistNm) + " mode=" + mode + " by=" + by
                + " state_before=" + (u.StateName ?? "?") + " prio=" + (u.StatePriority.HasValue ? I(u.StatePriority.Value) : "?"));
            _g.Event("auto_evade", "{\"uid\":" + uid + ",\"kind\":\"sub\",\"phase\":\"start\",\"mode\":" + Json.Str(mode) + ",\"by\":" + Json.Str(by) + "}");
            if (note != null) Log("switch_own uid=" + I(uid) + " reason=" + note);
            if (mode == "native")
            {
                if (!_g.StartNative(uid)) FailStart(r, u, th, nowS);
            }
            else OwnTick(r, u, th, nowS);
            return r;
        }

        /// <summary>The game did not take the native state: the own mode if there is one, else the run ends.</summary>
        private void FailStart(Run r, SubUnit u, SubThreat th, double nowS)
        {
            if (_g.OwnOk)
            {
                SwitchOwn(r, "start_failed", false);
                OwnTick(r, u, th, nowS);
            }
            else End(r, "start_failed", nowS);
        }

        private void SwitchOwn(Run r, string reason, bool finishNative)
        {
            if (finishNative) _g.FinishNative(r.Uid);
            r.Mode = "own";
            r.S = new EvState { Phase = "evading", StartedS = r.S.StartedS, LastSeenS = r.S.LastSeenS };
            r.LastCourseSent = -999;
            Log("switch_own uid=" + I(r.Uid) + " reason=" + reason);
        }

        // ------------------------------------------------------------------ tick of a run

        private void TickRun(Run r, SubUnit u, SubThreat th, double nowS)
        {
            if (r.Mode == "native")
            {
                if (!r.Confirmed)
                {
                    if (nowS - r.StartedS < SubEvasionMath.TickS - 1e-9) return;
                    if (u.StateName != SubEvasionMath.EvasionState) { FailStart(r, u, th, nowS); return; }
                    r.Confirmed = true;
                }
                string end = SubEvasionMath.NativeEnd(u.StateName);
                if (end != null) { End(r, end, nowS); return; }
                if (_g.OwnOk && SubEvasionMath.DeepDiveSwitch(_p.DeepDive, u.Preset, false) == "deep_dive_off")
                {
                    SwitchOwn(r, "deep_dive_off", true);
                    OwnTick(r, u, th, nowS);
                    return;
                }
                if (SubEvasionMath.DeepDiveSwitch(_p.DeepDive, u.Preset, false) != null && !r.DeepNoted)
                {
                    r.DeepNoted = true;
                    Log("deep_dive_off_not_possible uid=" + I(r.Uid) + " (no own mode): native keeps the game's behaviour");
                }
                Noisemakers(r, u, th, nowS);
                TickLog(r, u, th);
                return;
            }
            OwnTick(r, u, th, nowS);
        }

        private void OwnTick(Run r, SubUnit u, SubThreat th, double nowS)
        {
            bool known = th != null && th.Known;
            if (known)
            {
                r.S.LastSeenS = nowS; r.LastDist = th.DistNm;
                var tp = new EvTorp { TrackId = th.TrackId, BrgDeg = th.BrgDeg, DistNm = th.DistNm };
                bool outdive = SubEvasionMath.CanOutdive(th.TorpMaxDepthUnity, th.Preset6Ft, th.SeabedUnity);
                SubOwnOrder o = SubEvasionMath.Own(u.HeadingDeg, tp, th.TorpCourseDeg, r.S, _p.DeepDive, outdive);
                if (Math.Abs(EvasionMath.Diff(r.LastCourseSent, o.CourseDeg)) >= 3)
                {
                    if (_g.PlaceCourse(r.Uid, o.CourseDeg)) r.LastCourseSent = o.CourseDeg;
                }
                if (u.Telegraph != o.Telegraph) _g.SetTelegraph(r.Uid, o.Telegraph);
                if (u.Preset.HasValue ? u.Preset.Value != o.Preset : r.LastPresetSent != o.Preset)
                {
                    if (_g.SetPreset(r.Uid, o.Preset)) r.LastPresetSent = o.Preset;
                }
                if (o.CavitationOff && !r.CavChanged && u.CavForbidden != false)
                {
                    if (_g.SetCavitationForbidden(r.Uid, false)) r.CavChanged = true;
                }
            }
            if (EvasionMath.Done(r.S, nowS, r.LastDist, known)) { End(r, "finished", nowS); return; }
            Noisemakers(r, u, th, nowS);
            TickLog(r, u, th);
        }

        /// <summary>Shared noisemaker timer: any decrease of the count is a launch (game or mod) and resets it; the mod launches only as a backup.</summary>
        private void Noisemakers(Run r, SubUnit u, SubThreat th, double nowS)
        {
            if (u.NoisemakersLeft >= 0)
            {
                if (SubEvasionMath.LaunchSeen(r.LastLeft, u.NoisemakersLeft))
                {
                    if (!r.PendingMod)
                    {
                        _nmGame++; r.LastLaunchS = nowS;
                        Log("noisemaker uid=" + I(r.Uid) + " by=game left=" + I(u.NoisemakersLeft));
                    }
                }
                r.PendingMod = false;
                r.LastLeft = u.NoisemakersLeft;
            }
            if (!_p.NoisemakerAssist || u.NoisemakersLeft < 1 || th == null) return;
            double inState = r.Mode == "own" ? double.MaxValue : nowS - r.EnteredS;
            if (!SubEvasionMath.AssistNow(inState, nowS - r.LastLaunchS, _p.NoisemakerIntervalS, th.Known, th.DistNm)) return;
            r.LastLaunchS = nowS;
            if (_g.LaunchNoisemaker(r.Uid))
            {
                r.PendingMod = true; _nmMod++;
                Log("noisemaker uid=" + I(r.Uid) + " by=mod left=" + I(u.NoisemakersLeft - 1));
            }
        }

        private void TickLog(Run r, SubUnit u, SubThreat th)
        {
            string key = (u.Preset.HasValue ? I(u.Preset.Value) : "?") + "|" + (u.Telegraph.HasValue ? I(u.Telegraph.Value) : "?") + "|" + (u.Panic.HasValue ? u.Panic.Value.ToString() : "?");
            if (!_p.Debug && key == r.TickKey) return;
            r.TickKey = key;
            Log("tick uid=" + I(r.Uid) + " mode=" + r.Mode + " preset=" + (u.Preset.HasValue ? I(u.Preset.Value) : "?") + " depth_ft=" + F(u.DepthFt)
                + " tg=" + (u.Telegraph.HasValue ? I(u.Telegraph.Value) : "?") + " kn=" + F(u.Kn) + " panic=" + (u.Panic.HasValue ? u.Panic.Value.ToString().ToLowerInvariant() : "?")
                + " hiding=" + (u.Hiding ?? "?") + " cav=" + (u.Cavitating.HasValue ? u.Cavitating.Value.ToString().ToLowerInvariant() : "?")
                + (th != null ? " torp=" + I(th.TrackId) + " brg=" + F(th.BrgDeg) + " dist=" + F(th.DistNm) : ""));
        }

        // ------------------------------------------------------------------ end / release

        private void End(Run r, string reason, double nowS)
        {
            _runs.Remove(r.Uid);
            _ended[r.Uid] = nowS;
            if (reason == "finished") _finished++;
            else if (reason == "overridden") _overridden++;
            else if (reason == "released") _released++;
            Log("end uid=" + I(r.Uid) + " mode=" + r.Mode + " reason=" + reason + " after=" + F(Math.Max(0, nowS - r.StartedS)) + "s");
            _g.Event("auto_evade", "{\"uid\":" + r.Uid + ",\"kind\":\"sub\",\"phase\":\"end\",\"mode\":" + Json.Str(r.Mode) + ",\"reason\":" + Json.Str(reason) + "}");
            if (reason == "unit_gone" || reason == "mission_left") return;
            bool full = (reason == "finished" || reason == "released") && _p.Restore;
            SubSnapshot s = r.Saved;
            if (s != null && !r.CavChanged && s.CavForbidden.HasValue)
            {
                // the native state manages the cavitation limit itself (ADR-0071 item 7): only a limit the mod lifted is put back
                s = new SubSnapshot
                {
                    Route = s.Route, CourseDeg = s.CourseDeg, Telegraph = s.Telegraph, Preset = s.Preset, DepthFt = s.DepthFt,
                    CavForbidden = null, PlayerOverride = s.PlayerOverride,
                };
            }
            string res = s == null ? "skipped" : _g.Restore(r.Uid, s, full);
            Log("restore uid=" + I(r.Uid) + " full=" + (full ? "true" : "false") + " " + (res ?? "skipped"));
        }

        /// <summary>Ends runs whose submarine is gone.</summary>
        public void Sweep(double nowS)
        {
            _nowS = nowS;
            foreach (int uid in new List<int>(_runs.Keys))
            {
                SubUnit u = _g.Sub(uid);
                Run r;
                if ((u == null || !u.Alive) && _runs.TryGetValue(uid, out r)) End(r, "unit_gone", nowS);
            }
        }

        /// <summary>ReleaseRegistry participant ("Return control"): the native state is finished, the saved state is put back.</summary>
        public void Release(int uid)
        {
            Run r;
            if (!_runs.TryGetValue(uid, out r)) return;
            if (r.Mode == "native" && !_g.FinishNative(uid)) Log("release_native_kept uid=" + I(uid));
            End(r, "released", Math.Max(_nowS, r.StartedS));
        }

        public void LeaveMission()
        {
            foreach (Run r in new List<Run>(_runs.Values))
                Log("end uid=" + I(r.Uid) + " mode=" + r.Mode + " reason=mission_left");
            _runs.Clear(); _ended.Clear(); _next.Clear(); _lastSkip.Clear();
        }

        public string DiagJson(int limit)
        {
            var sb = new StringBuilder("{\"runs\":[");
            int n = 0;
            foreach (Run r in _runs.Values)
            {
                if (n >= limit) break;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"uid\":").Append(r.Uid).Append(",\"mode\":").Append(Json.Str(r.Mode)).Append(",\"by\":").Append(Json.Str(r.By))
                  .Append(",\"started_s\":").Append(Json.Num(r.StartedS)).Append(",\"confirmed\":").Append(r.Confirmed ? "true" : "false").Append('}');
            }
            sb.Append("],\"run_count\":").Append(_runs.Count)
              .Append(",\"started\":").Append(_started).Append(",\"finished\":").Append(_finished).Append(",\"overridden\":").Append(_overridden)
              .Append(",\"released\":").Append(_released).Append(",\"noisemaker_game\":").Append(_nmGame).Append(",\"noisemaker_mod\":").Append(_nmMod).Append('}');
            return sb.ToString();
        }
    }
}
