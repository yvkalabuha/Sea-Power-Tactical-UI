using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Ui
{
    internal sealed class AirEvasionParams
    {
        public bool Enabled = true, OptionHint = true, Debug;
        public double AutoTtiS = 20, AutoMinNm = 3, AutoCooldownS = 10, ThreatNm = 40, BeamMaxS = 180;
    }

    internal sealed class AeUnit
    {
        public int Uid;
        public bool Alive, IsAircraft, InFlight, IsLarge, AttackRun, PlayerControl, ChaffReady, Globe;
        public double Lat, Lon, HeadingDeg, Kn;
        public int Telegraph, MaxTelegraph;
        public string Roe, StateName;
    }

    internal sealed class AeMissile
    {
        public string Key;
        public double Lat, Lon, Kn, CourseDeg;
        public bool TargetsUnit, Lost, IgnoredByGame;
    }

    internal sealed class AeThreat { public int TrackId; public double BearingDeg, RangeNm; }

    internal sealed class AeSaved
    {
        public List<double[]> Route = new List<double[]>();
        public double? CourseDeg;
        public int? Telegraph;
        public double? AltUnits;
    }

    /// <summary>T-100: what the controller needs from the game. Orders are only: native state, speed step, altitude preset, waypoint, chaff.</summary>
    internal interface IAirEvasionGame
    {
        AeUnit Own(int uid);
        AeMissile Missile(int uid);                        // ThreateningMissile of the unit, null = none
        AeThreat NearestHostileAir(int uid, double maxNm); // player's plot only (ADR-0038)
        IList<int> AutoUnits();                            // own in-flight aircraft with VB_AutoEvadeAir resolved on
        bool NativeOk { get; }                             // capability air_evasion_native
        bool ForceNative(int uid);                         // setState(_state_advancedMissileEvasion); false = not done
        bool FinishNative(int uid);                        // IsFinished = true; false = member missing
        bool SetTelegraph(int uid, int step);
        bool SetAltPreset(int uid, int preset);
        double[] PlaceCourse(int uid, double courseDeg, double legNm);   // placed {lat, lon} or null
        bool LaunchChaff(int uid);
        AeSaved Remember(int uid, List<double[]> routeOverride);          // routeOverride = route before an after-launch turn
        string Restore(int uid, AeSaved s);                               // null = ok, else first error
        bool RouteChangedByOthers(int uid, double[] placed);
        List<double[]> PostLaunchSupersede(int uid, bool restoreRoute);   // null = no after-launch turn
        bool? OptionEvasionOn { get; }                                    // null = unknown
        void Hint(string key);
        void Log(string line);
        void Event(string name, string dataJson);
    }

    /// <summary>T-100 / ADR-0060: runs of the aircraft evasion (native / combo_missile / beam), button and automatic start.</summary>
    internal sealed partial class AirEvasionController
    {
        private const string NativeState = "AdvancedMissileEvasion";

        private sealed class Run
        {
            public int Uid;
            public string Mode, By, MissileKey;
            public double StartedS, LastThreatS, LastCourse;
            public double[] Placed;
            public AeSaved Saved;
            public bool NativeChecked, ChaffDone, HoldHintDone, ReplacePending;
        }

        private readonly IAirEvasionGame _g;
        private readonly AirEvasionParams _p;
        private readonly Dictionary<int, Run> _runs = new Dictionary<int, Run>();
        private readonly Dictionary<string, double> _ended = new Dictionary<string, double>();   // uid:key -> end time
        private readonly HashSet<string> _muted = new HashSet<string>();
        private readonly Dictionary<int, double[]> _lastRange = new Dictionary<int, double[]>(); // uid -> {range, nowS}
        private bool _optionHinted;

        public AirEvasionController(IAirEvasionGame g, AirEvasionParams p)
        {
            if (g == null) throw new ArgumentNullException("g");
            _g = g; _p = p ?? new AirEvasionParams();
        }

        public int RunCount { get { return _runs.Count; } }

        public bool Holds(int uid) { return _runs.ContainsKey(uid); }

        public string ModeOf(int uid) { Run r; return _runs.TryGetValue(uid, out r) ? r.Mode : null; }

        public void RequestReplace(int uid) { Run r; if (_runs.TryGetValue(uid, out r)) r.ReplacePending = true; }

        private static string F(double d) { return d.ToString("0.#", CultureInfo.InvariantCulture); }

        private static string MKey(int uid, string key) { return uid.ToString(CultureInfo.InvariantCulture) + ":" + key; }

        private static bool NativeActive(AeUnit u) { return u.StateName == NativeState || u.StateName == "EvadeMissile"; }

        private static bool Threatening(AeMissile m) { return m != null && !m.Lost; }

        // ------------------------------------------------------------------ button

        public string Button(int uid, double nowS, out string mode)
        {
            mode = null;
            if (!_p.Enabled) { Skipped(uid, "unsupported", "button"); return "unsupported"; }
            AeUnit u = _g.Own(uid);
            AeMissile m = u != null && u.Alive ? _g.Missile(uid) : null;
            AeThreat th = u != null && u.Alive && u.IsAircraft ? _g.NearestHostileAir(uid, _p.ThreatNm) : null;
            var inp = new AeButtonIn
            {
                IsAircraft = u != null && u.Alive && u.IsAircraft,
                AttackRun = u != null && u.AttackRun,
                Already = _runs.ContainsKey(uid) || (u != null && NativeActive(u)),
                HasMissile = Threatening(m),
                IsLarge = u != null && u.IsLarge,
                NativeOk = _g.NativeOk,
                HasThreat = th != null,
            };
            string d = AirEvasionMath.ButtonMode(inp);
            if (d.StartsWith("skip:", StringComparison.Ordinal))
            {
                string why = d.Substring(5);
                Skipped(uid, why, "button");
                return why;
            }
            mode = StartRun(u, d, "button", m, th, nowS);
            return null;
        }

        private void Skipped(int uid, string why, string by)
        {
            _g.Log("[AirEvade] skipped uid=" + uid + " reason=" + why + " by=" + by);
            _g.Event("air_evasion", "{\"uid\":" + uid + ",\"state\":\"skipped\",\"reason\":" + Json.Str(why) + ",\"by\":" + Json.Str(by) + "}");
        }

        private string StartRun(AeUnit u, string mode, string by, AeMissile m, AeThreat th, double nowS)
        {
            int uid = u.Uid;
            OptionHintOnce();
            List<double[]> superseded = _g.PostLaunchSupersede(uid, mode == "native");
            var run = new Run { Uid = uid, Mode = mode, By = by, MissileKey = m != null ? m.Key : null, StartedS = nowS, LastThreatS = nowS };
            run.Saved = _g.Remember(uid, superseded) ?? new AeSaved();
            _runs[uid] = run;
            if (mode == "native" && !_g.ForceNative(uid))
            {
                _g.Log("[AirEvade] native_failed uid=" + uid + " reason=force");
                run.Mode = mode = "combo_missile";
            }
            if (mode != "native") Maneuver(run, u, m, th);
            _g.Log("[AirEvade] start uid=" + uid + " mode=" + mode + " by=" + by + (m != null ? " missile=" + m.Key : "")
                + (th != null ? " threat=" + th.TrackId + " range=" + F(th.RangeNm) : ""));
            _g.Event("air_evasion", "{\"uid\":" + uid + ",\"state\":\"start\",\"mode\":" + Json.Str(mode) + ",\"by\":" + Json.Str(by) + "}");
            return mode;
        }

        private void OptionHintOnce()
        {
            if (!_p.OptionHint || _optionHinted) return;
            if (_g.OptionEvasionOn == false) { _optionHinted = true; _g.Hint("option_off"); }
        }

        /// <summary>Combo / beam actuation: max speed, lowest altitude, beam course, chaff once.</summary>
        private void Maneuver(Run run, AeUnit u, AeMissile m, AeThreat th)
        {
            int uid = run.Uid;
            _g.SetTelegraph(uid, u.MaxTelegraph);
            _g.SetAltPreset(uid, 0);
            double bearing = Bearing(u, m, th);
            double course = AirEvasionMath.BeamCourse(u.HeadingDeg, bearing);
            run.Placed = _g.PlaceCourse(uid, course, AirEvasionMath.LegNm);
            run.LastCourse = course;
            Chaff(run, u);
        }

        private static double Bearing(AeUnit u, AeMissile m, AeThreat th)
        {
            if (m != null) return AirEvasionMath.BearingDeg(u.Lat, u.Lon, m.Lat, m.Lon, u.Globe);
            return th != null ? th.BearingDeg : u.HeadingDeg;
        }

        private void Chaff(Run run, AeUnit u)
        {
            if (run.ChaffDone || run.HoldHintDone) return;
            if (string.Equals(u.Roe, "hold", StringComparison.OrdinalIgnoreCase)) { run.HoldHintDone = true; _g.Hint("chaff_hold"); return; }
            if (!u.ChaffReady) return;
            run.ChaffDone = true;
            _g.LaunchChaff(run.Uid);
        }

        // ------------------------------------------------------------------ tick

        public void Tick(double nowS)
        {
            if (!_p.Enabled) return;
            foreach (int uid in new List<int>(_runs.Keys))
            {
                Run r;
                if (_runs.TryGetValue(uid, out r)) TickRun(r, nowS);
            }
            TickAuto(nowS);
            PruneMuted();
        }

        private void TickRun(Run r, double nowS)
        {
            AeUnit u = _g.Own(r.Uid);
            if (u == null || !u.Alive) { End(r, "unit_gone", nowS, false); return; }
            AeMissile m = _g.Missile(r.Uid);
            if (r.Mode == "native") { TickNative(r, u, m, nowS); return; }
            if (r.ReplacePending)
            {
                r.ReplacePending = false;
                double c = AirEvasionMath.BeamCourse(u.HeadingDeg, Bearing(u, Threatening(m) ? m : null, _g.NearestHostileAir(r.Uid, _p.ThreatNm)));
                r.Placed = _g.PlaceCourse(r.Uid, c, AirEvasionMath.LegNm);
                r.LastCourse = c;
            }
            else if (r.Placed != null && _g.RouteChangedByOthers(r.Uid, r.Placed)) { End(r, "overridden", nowS, false); return; }
            bool missile = Threatening(m);
            if (r.Mode == "beam")
            {
                if (missile) { Upgrade(r, u, m); return; }
                AeThreat th = _g.NearestHostileAir(r.Uid, _p.ThreatNm);
                if (th != null) r.LastThreatS = nowS;
                string end = AirEvasionMath.BeamEnd(nowS, r.StartedS, r.LastThreatS, _p.BeamMaxS, false);
                if (end != null) { End(r, end, nowS, true); return; }
                Steer(r, u, null, th);
            }
            else
            {
                if (!missile) { End(r, "missile_gone", nowS, true); return; }
                if (m.IgnoredByGame) { Mute(r.Uid, m.Key); End(r, "cease_fire", nowS, true); return; }
                Steer(r, u, m, null);
            }
            Chaff(r, u);
            if (_p.Debug) _g.Log("[AirEvade] tick uid=" + r.Uid + " mode=" + r.Mode + " course=" + F(r.LastCourse));
        }

        private void Steer(Run r, AeUnit u, AeMissile m, AeThreat th)
        {
            double c = AirEvasionMath.BeamCourse(u.HeadingDeg, Bearing(u, m, th));
            if (!AirEvasionMath.NeedReplace(r.LastCourse, c)) return;
            r.Placed = _g.PlaceCourse(r.Uid, c, AirEvasionMath.LegNm);
            r.LastCourse = c;
        }

        /// <summary>A missile appeared during a beam run: hand over to the native state (or combo), the saved state stays.</summary>
        private void Upgrade(Run r, AeUnit u, AeMissile m)
        {
            r.MissileKey = m.Key;
            if (_g.NativeOk && !u.IsLarge && _g.ForceNative(r.Uid)) r.Mode = "native";
            else { r.Mode = "combo_missile"; Steer(r, u, m, null); }
            r.NativeChecked = false;
            _g.Log("[AirEvade] upgrade uid=" + r.Uid + " mode=" + r.Mode + " missile=" + m.Key);
            _g.Event("air_evasion", "{\"uid\":" + r.Uid + ",\"state\":\"upgrade\",\"mode\":" + Json.Str(r.Mode) + "}");
        }

        private void TickNative(Run r, AeUnit u, AeMissile m, double nowS)
        {
            if (u.StateName == NativeState) { r.NativeChecked = true; return; }
            if (!r.NativeChecked)
            {
                if (Threatening(m) && !m.IgnoredByGame)
                {
                    _g.Log("[AirEvade] native_failed uid=" + r.Uid + " state=" + u.StateName);
                    r.Mode = "combo_missile";
                    Maneuver(r, u, m, null);
                    _g.Event("air_evasion", "{\"uid\":" + r.Uid + ",\"state\":\"native_failed\"}");
                    return;
                }
                End(r, "state_exit", nowS, false);
                return;
            }
            if (m != null && m.IgnoredByGame) { Mute(r.Uid, m.Key); End(r, "cease_fire", nowS, false); return; }
            End(r, "state_exit", nowS, false);
        }

        private void TickAuto(double nowS)
        {
            IList<int> list = _g.AutoUnits();
            if (list == null) return;
            foreach (int uid in list)
            {
                AeUnit u = _g.Own(uid);
                if (u == null || !u.Alive || !u.IsAircraft) continue;
                AeMissile m = _g.Missile(uid);
                if (!Threatening(m)) { _lastRange.Remove(uid); continue; }
                double range = AirEvasionMath.DistNm(u.Lat, u.Lon, m.Lat, m.Lon, u.Globe), closing;
                double[] prev;
                if (m.Kn > 0) closing = AirEvasionMath.ClosingKn(u.Lat, u.Lon, u.HeadingDeg, u.Kn, m.Lat, m.Lon, m.CourseDeg, m.Kn, u.Globe);
                else if (_lastRange.TryGetValue(uid, out prev)) closing = AirEvasionMath.ClosingFromRanges(prev[0], range, nowS - prev[1]);
                else closing = 0;
                _lastRange[uid] = new[] { range, nowS };
                double ended;
                string key = MKey(uid, m.Key);
                var inp = new AeAutoIn
                {
                    FlagOn = true, HasMissile = true, MissileAtUnit = m.TargetsUnit, IgnoredByGame = m.IgnoredByGame,
                    Muted = _muted.Contains(key), NativeActive = NativeActive(u), AttackRun = u.AttackRun, PlayerControl = u.PlayerControl,
                    Running = _runs.ContainsKey(uid), RangeNm = range, ClosingKn = closing, NowS = nowS,
                    TtiLimitS = _p.AutoTtiS, MinNm = _p.AutoMinNm, CooldownS = _p.AutoCooldownS,
                    SameMissileEndedS = _ended.TryGetValue(key, out ended) ? (double?)ended : null,
                };
                string d = AirEvasionMath.AutoDecision(inp);
                if (d != "start") continue;
                string mode = AirEvasionMath.ButtonMode(new AeButtonIn { IsAircraft = true, HasMissile = true, IsLarge = u.IsLarge, NativeOk = _g.NativeOk });
                StartRun(u, mode, "auto", m, null, nowS);
                _g.Log("[AirEvade] auto uid=" + uid + " tti=" + F(AirEvasionMath.Tti(range, closing)) + " range=" + F(range));
            }
        }

        // ------------------------------------------------------------------ end / release

        private void End(Run r, string reason, double nowS, bool restore)
        {
            _runs.Remove(r.Uid);
            string err = null;
            if (restore && r.Saved != null) err = _g.Restore(r.Uid, r.Saved);
            if (r.MissileKey != null) _ended[MKey(r.Uid, r.MissileKey)] = nowS;
            _g.Log("[AirEvade] end uid=" + r.Uid + " mode=" + r.Mode + " reason=" + reason + (restore ? " restored=" + (err ?? "ok") : ""));
            _g.Event("air_evasion", "{\"uid\":" + r.Uid + ",\"state\":\"end\",\"mode\":" + Json.Str(r.Mode) + ",\"reason\":" + Json.Str(reason) + "}");
        }

        private void Mute(int uid, string key)
        {
            if (key != null) _muted.Add(MKey(uid, key));
        }

        private void PruneMuted()
        {
            if (_muted.Count == 0) return;
            foreach (string k in new List<string>(_muted))
            {
                int i = k.IndexOf(':');
                int uid;
                if (i <= 0 || !int.TryParse(k.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out uid)) { _muted.Remove(k); continue; }
                AeMissile m = _g.Missile(uid);
                if (m == null || m.Lost || MKey(uid, m.Key) != k) _muted.Remove(k);
            }
        }

        /// <summary>ReleaseRegistry participant: ends the run "released", restores combo/beam, finishes the native state, mutes the missile.</summary>
        public void Release(int uid)
        {
            Run r;
            if (!_runs.TryGetValue(uid, out r)) return;
            Mute(uid, r.MissileKey);
            if (r.Mode == "native")
            {
                if (!_g.FinishNative(uid)) _g.Log("[AirEvade] release_native_kept uid=" + uid);
                End(r, "released", 0, false);
            }
            else End(r, "released", 0, true);
        }

        public void LeaveMission()
        {
            _runs.Clear(); _ended.Clear(); _muted.Clear(); _lastRange.Clear();
            _optionHinted = false;
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
                  .Append(",\"missile\":").Append(Json.Str(r.MissileKey ?? "")).Append(",\"started_s\":").Append(Json.Num(r.StartedS))
                  .Append(",\"course\":").Append(Json.Num(r.LastCourse)).Append('}');
            }
            sb.Append("],\"run_count\":").Append(_runs.Count).Append(",\"muted\":").Append(_muted.Count)
              .Append(",\"cooldowns\":").Append(_ended.Count).Append('}');
            return sb.ToString();
        }
    }
}
