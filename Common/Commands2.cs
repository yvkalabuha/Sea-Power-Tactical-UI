using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>v0.4 commands: select, set_emcon, set_radar, set_roe, cease_fire, set_time_compression, events. Main thread only.</summary>
    internal sealed partial class GameApi
    {
        public float MaxBotTimeCompression = 20f;

        private void SelfCheckCommands(bool st)
        {
            bool sel = st & Has("select", "SeaPower.RenderPosition", "switchToObject", false, true);
            sel &= Has("select", "SeaPower.Taskforce", "_taskforceVessels", false, true);
            if (sel) Capabilities.Add("select");

            if (st & Has("set_emcon", "SeaPower.ObjectBase", "setEMCON", false, true)) Capabilities.Add("set_emcon");

            bool rd = st;
            rd &= Has("set_radar", "SeaPower.ObjectBase", "_obp", false, true);
            rd &= Has("set_radar", "SeaPower.ObjectBaseParameters", "_airSearchRadars", false, true);
            rd &= Has("set_radar", "SeaPower.ObjectBaseParameters", "_surfaceSearchRadars", false, true);
            rd &= Has("set_radar", "SeaPower.SensorSystemRadar", "Enable", false, true);
            rd &= Has("set_radar", "SeaPower.SensorSystemRadar", "Disable", false, true);
            rd &= Has("set_radar", "SeaPower.ObjectBase", "IsAirSearchRadarsOn", false, true);
            Has("set_radar", "SeaPower.ObjectBase", "IsSurfaceSearchRadarsOn", false, false);
            if (rd) Capabilities.Add("set_radar");

            bool roe = st;
            roe &= Has("set_roe", "SeaPower.ObjectBase", "SetWeaponStatus", false, true);
            roe &= FindType("SeaPower.ObjectBase+WeaponStatus") != null;
            Checks.Add(new Check { Capability = "set_roe", Member = "SeaPower.ObjectBase+WeaponStatus", Required = true, Present = FindType("SeaPower.ObjectBase+WeaponStatus") != null });
            Has("set_roe", "SeaPower.ObjectBase", "WeaponState", false, false);
            if (roe) Capabilities.Add("set_roe");

            if (st & Has("cease_fire", "SeaPower.ObjectBase", "CeaseFire", false, true)) Capabilities.Add("cease_fire");

            // v0.7 (M8): countermeasures, same calls as the game keys Shift+C (LaunchChaff) and Shift+N (noisemaker EngageTask)
            bool dc = st && Capabilities.Contains("weapons");
            dc &= Has("launch_decoy", "SeaPower.ObjectBase", "LaunchNoisemaker", false, true);
            dc &= Has("launch_decoy", "SeaPower.ObjectBase", "LaunchChaff", false, true);
            dc &= Has("launch_decoy", "SeaPower.ObjectBaseParameters", "_chaffWeapons", false, true);
            dc &= Has("launch_decoy", "SeaPower.WeaponSystemChaff", "readyToLaunch", false, true);
            Has("launch_decoy", "SeaPower.ObjectBase", "AcceptsOrdersFromPlayer", false, false);
            if (dc) Capabilities.Add("launch_decoy");

            // K4 (T-058): torpedo evasion in one order = course + telegraph (+ noisemaker), see EvadeTorpedoCmd
            if (Capabilities.Contains("set_course") && Capabilities.Contains("set_telegraph") && Capabilities.Contains("contacts"))
                Capabilities.Add("evade_torpedo");

            bool tm = true;
            tm &= Has("set_time_compression", "SeaPower.GameTime", "SetTimeCompression", true, true);
            Has("set_time_compression", "SeaPower.InputHandler", "IsInMessagePauseMode", false, false);
            Has("set_time_compression", "SeaPower.InputHandler", "IsInPauseMode", false, false);
            if (tm) Capabilities.Add("set_time_compression");

            Has("state", "SeaPower.ObjectBase", "_previousTelegraphSetting", false, false);
            Has("state", "SeaPower.ObjectBase", "StateMachine", false, false);
            Has("state", "SeaPower.StateMachine", "CurrentStateName", false, false);
            Has("state", "SeaPower.ObjectBase", "Compartments", false, false);
            Has("state", "SeaPower.ObjectBase", "Formation", false, false);
            Has("state", "SeaPower.Compartments", "_currentFires", false, false); // FireType[]: non-zero slot = active fire (0.5.0)
            SelfCheckEvents();
        }

        /// <summary>Invoke a method filling omitted trailing parameters with their defaults (like a normal C# call).</summary>
        private object CallOpt(object obj, string name, params object[] given)
        {
            var m = Find(obj.GetType(), name, false) as MethodInfo;
            if (m == null) throw new MissingMemberException(obj.GetType().Name, name);
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = i < given.Length ? given[i] : DefaultFor(ps[i]);
            return m.Invoke(obj, args);
        }

        private bool Confirmed(Dictionary<string, object> cmd)
        {
            return cmd.ContainsKey("confirm") && cmd["confirm"] is bool && (bool)cmd["confirm"];
        }

        private static string BoolArg(Dictionary<string, object> cmd, string key, out bool value)
        {
            value = false;
            if (!cmd.ContainsKey(key) || !(cmd[key] is bool)) return key + " (true/false) required";
            value = (bool)cmd[key];
            return null;
        }

        // ---------- select ----------
        private IEnumerable<object> OwnUnits()
        {
            var tf = GetStatic("SeaPower.Globals", "_playerTaskforce");
            foreach (var listName in new[] { "_taskforceVessels", "_taskforceSubmarines", "_taskforceAircraft", "_taskforceHelicopters" })
            {
                var list = Get(tf, listName) as IEnumerable;
                if (list == null) continue;
                foreach (var u in list) { if (u != null) yield return u; }
            }
        }

        private string SelectCmd(string id, Dictionary<string, object> cmd)
        {
            Need("select");
            if (!InMission()) return Err(id, "not_in_session", null);
            object found = null;
            if (cmd.ContainsKey("uid") && D(cmd["uid"]).HasValue)
            {
                found = FindOwnUnit((int)D(cmd["uid"]).Value);
                if (found == null) return Err(id, "unit_not_found", null);
            }
            else
            {
                string q = cmd.ContainsKey("name") ? cmd["name"] as string : null;
                if (string.IsNullOrWhiteSpace(q)) return Err(id, "bad_args", "uid or name required");
                q = q.Trim().ToLowerInvariant();
                var exact = new List<object>(); var part = new List<object>();
                foreach (var u in OwnUnits())
                {
                    string n = (RxGet(u, "Name") as string ?? "").ToLowerInvariant();
                    string c = (Get(u, "ClassName") as string ?? "").ToLowerInvariant();
                    if (n == q) exact.Add(u);
                    else if (n.Contains(q) || (c.Length > 0 && c.Contains(q))) part.Add(u);
                }
                var hits = exact.Count > 0 ? exact : part;
                if (hits.Count == 0) return Err(id, "unit_not_found", q);
                if (hits.Count > 1)
                {
                    var names = new StringBuilder();
                    foreach (var h in hits) names.Append(RxGet(h, "Name") as string).Append("; ");
                    return Err(id, "ambiguous", names.ToString());
                }
                found = hits[0];
            }
            var rp = RenderPositionInstance();
            if (rp == null) return Err(id, "unsupported", "RenderPosition");
            CallOpt(rp, "switchToObject", found, false);
            return Ok(id, "\"uid\":" + Json.Num(D(Get(found, "UniqueID")) ?? -1) + ",\"name\":" + Json.Str(RxGet(found, "Name") as string) +
                      ",\"note\":\"selection is applied by the game; see state.selected in the next frame\"");
        }

        // ---------- sensors ----------
        private string EmconCmd(string id, Dictionary<string, object> cmd)
        {
            Need("set_emcon");
            bool on; var e = BoolArg(cmd, "on", out on);
            if (e != null) return Err(id, "bad_args", e);
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            CallOpt(u, "setEMCON", on, true);
            return Ok(id, "\"emcon\":" + Json.Bool(RxGet(u, "Emcon") is bool && (bool)RxGet(u, "Emcon")));
        }

        private string RadarCmd(string id, Dictionary<string, object> cmd)
        {
            Need("set_radar");
            string kind = cmd.ContainsKey("kind") ? (cmd["kind"] as string ?? "").ToLowerInvariant() : "all";
            if (kind.Length == 0) kind = "all";
            if (kind != "air" && kind != "surface" && kind != "all") return Err(id, "bad_args", "kind: air|surface|all");
            bool on; var e = BoolArg(cmd, "on", out on);
            if (e != null) return Err(id, "bad_args", e);
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            var obp = Get(u, "_obp");
            int n = 0;
            var kinds = kind == "all" ? new[] { "air", "surface" } : new[] { kind };
            foreach (var k in kinds)
            {
                var radars = Get(obp, k == "air" ? "_airSearchRadars" : "_surfaceSearchRadars") as IEnumerable;
                int nk = 0;
                if (radars != null)
                    foreach (var r in radars) { CallOpt(r, on ? "Enable" : "Disable"); nk++; } // default args, like a plain C# call
                if (nk > 0) RxSet(u, k == "air" ? "IsAirSearchRadarsOn" : "IsSurfaceSearchRadarsOn", on); // UI sets the flag explicitly
                n += nk;
            }
            if (n == 0) return Err(id, "no_sensor", kind + " search radar");
            return Ok(id, "\"uid\":" + Json.Num(D(Get(u, "UniqueID")) ?? -1) + ",\"kind\":" + Json.Str(kind) + ",\"on\":" + Json.Bool(on) + ",\"radars\":" + n +
                      ",\"emcon\":" + Json.Bool(RxGet(u, "Emcon") is bool && (bool)RxGet(u, "Emcon")));
        }

        // ---------- fire control ----------
        private string RoeCmd(string id, Dictionary<string, object> cmd)
        {
            Need("set_roe");
            string st = cmd.ContainsKey("status") ? (cmd["status"] as string ?? "").ToLowerInvariant() : "";
            if (st != "tight" && st != "hold" && st != "free") return Err(id, "bad_args", "status: tight|hold|free");
            if (st == "free" && !Confirmed(cmd)) return Err(id, "confirm_required", "weapons free needs \"confirm\":true");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            var en = Enum.Parse(FindType("SeaPower.ObjectBase+WeaponStatus"), st == "tight" ? "Tight" : st == "hold" ? "Hold" : "Free");
            string previous = WeaponStatusName(u);
            if (st == "hold") CallOpt(u, "CeaseFire", false); // UI does this before Hold
            CallOpt(u, "SetWeaponStatus", en, true);
            // WeaponState (reactive) may lag a frame; _weaponStatus is the field SetWeaponStatus wrote. The truth is state.selected.weapon_status next frame.
            string applied = WeaponStatusName(u);
            return Ok(id, "\"weapon_status\":" + Json.Str(st) + ",\"previous\":" + Json.Str(previous) + ",\"applied\":" + Json.Str(applied));
        }

        private string WeaponStatusName(object u)
        {
            var f = Get(u, "_weaponStatus");
            if (f == null) f = RxGet(u, "WeaponState");
            return f == null ? null : f.ToString().ToLowerInvariant();
        }

        private string CeaseFireCmd(string id, Dictionary<string, object> cmd)
        {
            Need("cease_fire");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            CallOpt(u, "CeaseFire"); // InputHandler 'X' = objectBase.CeaseFire() with defaults
            int tasks = 0;
            var t = Get(u, "_currentEngageTasks") as ICollection;
            if (t != null) tasks = t.Count;
            return Ok(id, "\"engage_tasks\":" + tasks);
        }

        // ---------- countermeasures (M8) ----------
        private string LaunchDecoyCmd(string id, Dictionary<string, object> cmd)
        {
            Need("launch_decoy");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            var acc = Get(u, "AcceptsOrdersFromPlayer");
            if (acc is bool && !(bool)acc) return Err(id, "no_control", "unit does not accept orders");
            string kind = (cmd.ContainsKey("kind") ? cmd["kind"] as string : null) ?? "";
            kind = kind.ToLowerInvariant();
            if (kind == "noisemaker")
            {
                foreach (var d in DecoyRows(u))
                {
                    if (d.Type != "Noisemaker" || d.Count < 1) continue;
                    Call(u, "LaunchNoisemaker", d.Name);
                    return Ok(id, "\"kind\":\"noisemaker\",\"ammo\":" + Json.Str(d.Name) + ",\"left\":" + (d.Count - 1));
                }
                return Err(id, "no_decoy", "no noisemakers left on this unit");
            }
            if (kind == "chaff")
            {
                var list = Get(Get(u, "_obp"), "_chaffWeapons") as IEnumerable;
                int mounts = 0, ready = 0;
                if (list != null)
                    foreach (var ws in list)
                    {
                        if (ws == null) continue;
                        mounts++;
                        try { if ((bool)Call(ws, "readyToLaunch")) ready++; } catch { }
                    }
                if (mounts == 0) return Err(id, "no_decoy", "no chaff launchers on this unit");
                if (ready == 0) return Err(id, "not_ready", "chaff launchers are reloading or empty");
                Call(u, "LaunchChaff", true);
                return Ok(id, "\"kind\":\"chaff\",\"launchers\":" + ready);
            }
            return Err(id, "bad_args", "kind: noisemaker | chaff");
        }

        // ---------- torpedo evasion (K4) ----------
        // Game AI rule for non-player ships (TorpedoEvasion, docs/research/seapower-combat.md): keep the torpedo about
        // 100 deg off the bow, telegraph 3 while it is farther than 3.8 nm and 5 closer, noisemaker every ~30 s.
        // Player ships never evade by themselves, so the bot gives this order. Honest: the threat is a torpedo track
        // on the player's plotting table, or the torpedo the game itself announced (torpedo_inbound).
        private const double EvadeRelDeg = 100, EvadeFarNm = 3.8, EvadeLegNm = 20;

        private string EvadeTorpedoCmd(string id, Dictionary<string, object> cmd)
        {
            Need("evade_torpedo");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            double lat0, lon0, h;
            if (!OwnPos(u, out lat0, out lon0, out h)) return Err(id, "no_position", null);

            // 1. threat: given track, else nearest torpedo track, else the latest announced torpedo aimed at this unit
            double brg = -1, rng = -1; string source = null; int? track = null;
            object v = null;
            if (cmd.ContainsKey("track_id") && cmd["track_id"] != null)
            {
                var tid = D(cmd["track_id"]);
                if (!tid.HasValue) return Err(id, "bad_args", "track_id must be a number");
                v = FindContact((int)tid.Value);
                if (v == null || IsGone(v)) return Err(id, "unknown_target", "no such contact in the plotting table");
            }
            else
            {
                double best = double.MaxValue;
                var vehicles = Vehicles();
                if (vehicles != null)
                    foreach (var c in vehicles)
                    {
                        if (Kind(c) != "Torpedo" || Relation(c) == "friendly" || IsGone(c)) continue;
                        var p = TrackPos(c); if (p == null) continue;
                        double d = DistanceNm(lat0, lon0, p[0], p[1]);
                        if (d < best) { best = d; v = c; }
                    }
            }
            if (v != null)
            {
                var p = TrackPos(v);
                if (p == null) return Err(id, "no_position", "torpedo track has no position");
                brg = Bearing(lat0, lon0, p[0], p[1]); rng = DistanceNm(lat0, lon0, p[0], p[1]);
                source = "track"; var d = D(Get(v, "Id")); if (d.HasValue) track = (int)d.Value;
            }
            else
            {
                bool found = false; double ib = 0, ir = 0; int? it = null;
                LastInboundTorpedo(u, ref found, ref ib, ref ir, ref it);
                if (found) { brg = ib; rng = ir; source = "inbound"; track = it; }
            }
            if (source == null) return Err(id, "no_torpedo", "no torpedo track or announced torpedo for this unit");

            // 2. course: torpedo ~100 deg off the bow, on the side that needs the smaller turn
            double hdg = D(RxGet(u, "Heading")) ?? brg;
            double c1 = Norm360(brg + EvadeRelDeg), c2 = Norm360(brg - EvadeRelDeg);
            double course = AngleDiff(hdg, c1) <= AngleDiff(hdg, c2) ? c1 : c2;
            course = Math.Round(course);
            if (course >= 360) course -= 360;
            double lat, lon;
            Destination(lat0, lon0, course, EvadeLegNm, out lat, out lon);
            string wp = PlaceWaypoint(id, u, lat, lon, false, "");
            if (!wp.Contains("\"ok\":true")) return wp;

            // 3. speed (explicit telegraph wins), 4. noisemaker unless decoy:false
            int tg = rng > EvadeFarNm ? 3 : 5;
            var tArg = cmd.ContainsKey("telegraph") ? D(cmd["telegraph"]) : null;
            if (tArg.HasValue) { if (tArg.Value < -1 || tArg.Value > 5) return Err(id, "bad_args", "telegraph -1..5"); tg = (int)tArg.Value; }
            var tcmd = new Dictionary<string, object> { { "telegraph", (double)tg } };
            if (cmd.ContainsKey("uid")) tcmd["uid"] = cmd["uid"];
            string tr = TelegraphCmd(id, tcmd);
            bool tgOk = tr.Contains("\"ok\":true");

            string decoy = null;
            bool wantDecoy = !(cmd.ContainsKey("decoy") && cmd["decoy"] is bool && !(bool)cmd["decoy"]);
            if (wantDecoy && Capabilities.Contains("launch_decoy"))
            {
                try
                {
                    foreach (var d in DecoyRows(u))
                        if (d.Type == "Noisemaker" && d.Count >= 1) { Call(u, "LaunchNoisemaker", d.Name); decoy = d.Name; break; }
                }
                catch { decoy = null; }
            }
            return Ok(id, "\"uid\":" + Json.Num(D(Get(u, "UniqueID")) ?? -1) +
                ",\"source\":" + Json.Str(source) + ",\"torpedo_track_id\":" + (track.HasValue ? track.Value.ToString() : "null") +
                ",\"torpedo_bearing_deg\":" + Json.Num(Math.Round(brg, 1)) + ",\"torpedo_range_nm\":" + Json.Num(Math.Round(rng, 2)) +
                ",\"course\":" + Json.Num(course) + ",\"telegraph\":" + tg + ",\"telegraph_ok\":" + Json.Bool(tgOk) +
                ",\"noisemaker\":" + Json.Str(decoy));
        }

        private static double Norm360(double a) { a %= 360; return a < 0 ? a + 360 : a; }
        private static double AngleDiff(double a, double b) { double d = Math.Abs(Norm360(a) - Norm360(b)); return d > 180 ? 360 - d : d; }

        // ---------- time ----------
        private string TimeCmd(string id, Dictionary<string, object> cmd)
        {
            Need("set_time_compression");
            if (MessagePause()) return Err(id, "message_pause", "a mission message window is open");
            var x = cmd.ContainsKey("value") ? D(cmd["value"]) : (cmd.ContainsKey("x") ? D(cmd["x"]) : null);
            if (!x.HasValue || x.Value < 0.1 || x.Value > 5000) return Err(id, "bad_args", "value 0.1..5000 (game steps: 0.1 0.2 0.5 1 2 3 5 10 20 30 50 100 ...)");
            if (x.Value > MaxBotTimeCompression)
                return Err(id, "above_cap", "cap " + MaxBotTimeCompression + " (mod config MaxBotTimeCompression)");
            CallStatic("SeaPower.GameTime", "SetTimeCompression", (float)x.Value);
            SetPauseFlag(false);
            var tc = D(GetStatic("SeaPower.GameTime", "TimeCompression"));
            return Ok(id, "\"time_compression\":" + (tc.HasValue ? Json.Num(tc.Value) : "null"));
        }

        private string PauseCmd(string id, bool pause)
        {
            Need("pause");
            if (MessagePause()) return Err(id, "message_pause", "a mission message window is open");
            if (pause) { CallStatic("SeaPower.GameTime", "Pause", true); SetPauseFlag(true); }
            else { CallStatic("SeaPower.GameTime", "UnPause"); SetPauseFlag(false); } // UI also resets IsInPauseMode
            return Ok(id, "\"paused\":" + Json.Bool((bool)CallStatic("SeaPower.GameTime", "IsPaused")));
        }

        // ---------- set_telegraph (UI-equivalent) ----------
        private bool IsDeferredBy(string by)
        {
            return by == "ai_engage" || by == "ai_prep_engage" || by == "ai_collision" || by == "ai_evasion" || by == "ai_doctrine" || by == "air_ops";
        }

        private string TelegraphCmd(string id, Dictionary<string, object> cmd)
        {
            Need("set_telegraph");
            var t = cmd.ContainsKey("telegraph") ? D(cmd["telegraph"]) : null;
            if (!t.HasValue || t.Value < -1 || t.Value > 5) return Err(id, "bad_args", "telegraph -1..5 (BackFull..Flank)");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            int n = (int)t.Value;
            string by; string mj = ManeuverJson(u, out by);
            bool deferred = IsDeferredBy(by);
            if (deferred)
            {
                // the game is steering the ship (e.g. Aligning set telegraph 5): only remember the player's wish,
                // EndAlignment restores it. Calling setTelegraph now would fight the maneuver.
                SetField(u, "_previousTelegraphSetting", n);
            }
            else
            {
                Call(u, "setTelegraph", n);
                SetField(u, "_previousTelegraphSetting", n); // as ObjectSpeed (UI menu) does
            }
            return Ok(id, "\"telegraph\":" + n + ",\"deferred\":" + Json.Bool(deferred) + ",\"maneuver\":" + mj);
        }
    }
}
