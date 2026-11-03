using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-091 "Release control": remembers the ROE and the route before the first XO command (hooks around each combo step)
    /// and gives them back on restore_control, together with the mod's own automation flags (auto_evade, post_launch) and the
    /// workshop mod "Automatic SAR" (if it runs the unit). Speed, radars and EMCON are not restored.
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly ControlMemory _ctlMem = new ControlMemory();

        // ---------- hooks (declared in ActionJournalGame.cs) ----------

        partial void BeforeComboStep(object u, ActionStep st)
        {
            try
            {
                if (u == null || st == null) return;
                if (st.Cmd == "set_roe")
                {
                    TacticsEnsureMission();
                    _ctlMem.RememberRoe(u, WeaponStatusName(u));
                }
                else if (st.Cmd == "course_from_threat" || st.Cmd == "evade_torpedo")
                {
                    TacticsEnsureMission();
                    _ctlMem.RememberRoute(u, Waypoints(u), DemandedCourse(u));
                    if (st.Cmd == "evade_torpedo") RememberFields(u, new[] { "telegraph" });
                }
                else if (st.Cmd == "set_telegraph" || st.Cmd == "set_emcon" || st.Cmd == "set_radar")
                {
                    TacticsEnsureMission();
                    RememberFields(u, FieldKeys(st));
                }
            }
            catch { }
        }

        private static string[] FieldKeys(ActionStep st)
        {
            if (st.Cmd == "set_telegraph") return new[] { "telegraph" };
            if (st.Cmd == "set_emcon") return new[] { "emcon" };
            string kind = st.Args.ContainsKey("kind") ? (st.Args["kind"] as string ?? "").ToLowerInvariant() : "all";
            if (kind == "air") return new[] { "air_radar" };
            if (kind == "surface") return new[] { "surface_radar" };
            return new[] { "air_radar", "surface_radar" };
        }

        /// <summary>Current value of a restorable field as text, null when the game does not tell.</summary>
        private string FieldValue(object u, string key)
        {
            try
            {
                if (key == "telegraph") { var t = Telegraph(u); return t.HasValue ? t.Value.ToString() : null; }
                var b = RxGet(u, key == "emcon" ? "Emcon" : key == "air_radar" ? "IsAirSearchRadarsOn" : "IsSurfaceSearchRadarsOn");
                return b is bool ? ((bool)b ? "true" : "false") : null;
            }
            catch { return null; }
        }

        private void RememberFields(object u, string[] keys)
        {
            foreach (var k in keys) _ctlMem.RememberField(u, k, FieldValue(u, k));
        }

        partial void AfterComboStep(object u, ActionStep st, string ackJson)
        {
            try
            {
                if (u == null || st == null || string.IsNullOrEmpty(ackJson)) return;
                var ack = Json.Parse(ackJson) as Dictionary<string, object>;
                if (ack == null || !(ack.ContainsKey("ok") && ack["ok"] is bool && (bool)ack["ok"])) return;
                var data = ack.ContainsKey("data") ? ack["data"] as Dictionary<string, object> : null;
                if (st.Cmd == "set_roe")
                {
                    string applied = null;
                    if (st.Args.ContainsKey("status")) applied = (st.Args["status"] as string ?? "").ToLowerInvariant();
                    else if (data != null && data.ContainsKey("weapon_status")) applied = data["weapon_status"] as string;
                    _ctlMem.NoteRoeSet(u, applied);
                }
                else if (st.Cmd == "course_from_threat" || st.Cmd == "evade_torpedo")
                {
                    var la = data != null && data.ContainsKey("dest_lat") ? D(data["dest_lat"]) : null;
                    var lo = data != null && data.ContainsKey("dest_lon") ? D(data["dest_lon"]) : null;
                    if (la.HasValue && lo.HasValue) _ctlMem.NoteRoutePlaced(u, la.Value, lo.Value);
                    if (st.Cmd == "evade_torpedo") NoteFields(u, new[] { "telegraph" });
                }
                else if (st.Cmd == "set_telegraph" || st.Cmd == "set_emcon" || st.Cmd == "set_radar")
                {
                    NoteFields(u, FieldKeys(st)); // actual values: a deferred telegraph (AI maneuver) then equals the saved one
                }
            }
            catch { }
        }

        private void NoteFields(object u, string[] keys)
        {
            foreach (var k in keys) _ctlMem.NoteFieldSet(u, k, FieldValue(u, k));
        }

        /// <summary>Course the unit is steering to now (CourseCommand), null when unknown.</summary>
        private double? DemandedCourse(object u)
        {
            try
            {
                var cc = RxGet(u, "CourseCommand");
                if (cc == null) return null;
                var dc = D(Call(cc, "GetDemandedCourse", u));
                return dc.HasValue ? (double?)(((dc.Value % 360) + 360) % 360) : null;
            }
            catch { return null; }
        }

        /// <summary>Selfcheck: the hooks never throw on foreign objects and record nothing "set by the mod" on a failed ack.</summary>
        internal bool SelfTestControlHooks()
        {
            try
            {
                var o = new object();
                var roe = new ActionStep { Cmd = "set_roe" };
                var route = new ActionStep { Cmd = "course_from_threat" };
                var spd = new ActionStep { Cmd = "set_telegraph" };
                BeforeComboStep(null, roe); BeforeComboStep(o, roe); BeforeComboStep(o, route); BeforeComboStep(o, spd); BeforeComboStep(o, null);
                AfterComboStep(null, roe, "{}"); AfterComboStep(o, roe, "not json"); AfterComboStep(o, route, null);
                AfterComboStep(o, roe, "{\"type\":\"ack\",\"id\":1,\"ok\":false,\"error\":\"x\"}");
                AfterComboStep(o, spd, "{\"type\":\"ack\",\"id\":1,\"ok\":false,\"error\":\"x\"}");
                var pe = _ctlMem.Peek(o);
                bool clean = pe == null || (pe.PlacedByMod == null && pe.RoeSetByMod == null && pe.Fields.Count == 0);
                _ctlMem.Clear();
                return clean && StopAutomaticSar(o) != "cancelled";
            }
            catch { return false; }
        }

        /// <summary>Border hook for the interface mod (ADR-0044): releases the unit from the mod's own controllers (ASW, SAR, CAP, evasion); the compiler removes the call in the voice mod.</summary>
        partial void ReleaseParticipants(object u);

        /// <summary>Hook for the interface mod (T-098): drops the standing-orders flags of the unit; no-op in the voice mod.</summary>
        partial void FlagStoreRestoreControl(object u, string kind);

        // ---------- Automatic SAR (workshop mod, internal static AutomaticSAR.RescueCoordinator) ----------

        /// <summary>
        /// Cancels the workshop mod's "Automatic SAR" job for the unit, the same call the mod makes when the player gives a
        /// command from the game UI. Returns "absent" (mod not loaded), "inactive", "cancelled" or "error:&lt;type&gt;".
        /// Looks through the loaded assemblies on every call (the mod may be loaded later than the first lookup).
        /// </summary>
        internal string StopAutomaticSar(object u)
        {
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType("AutomaticSAR.RescueCoordinator", false); } catch { t = null; }
                    if (t != null) break;
                }
                if (t == null) return "absent";
                var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var active = t.GetMethod("IsActive", flags);
                var cancel = t.GetMethod("Cancel", flags);
                if (active == null || cancel == null || active.GetParameters().Length != 1 || cancel.GetParameters().Length != 1) return "absent";
                var isActive = active.Invoke(null, new[] { u });
                if (!(isActive is bool) || !(bool)isActive) return "inactive";
                cancel.Invoke(null, new[] { u });
                return "cancelled";
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                return "error:" + inner.GetType().Name;
            }
        }

        // ---------- restore_control ----------

        /// <summary>Restore one remembered field; apply(value) returns the ack of the command used. Returns the decision.</summary>
        private string RestoreField(object u, ControlEntry entry, string key, List<string> detail, Func<string, string> apply)
        {
            string cur = FieldValue(u, key);
            string action = ControlMemory.DecideField(entry, key, cur);
            if (action == "restore")
            {
                string to = entry.Fields[key].Saved;
                try
                {
                    string r = apply(to);
                    if (r != null && !r.Contains("\"ok\":true")) action = "error";
                    else detail.Add(key + " " + cur + "->" + to);
                }
                catch (Exception e) { action = "error"; detail.Add(key + " " + e.GetType().Name); }
            }
            else if (action == "player_changed") detail.Add(key + " player_changed");
            return action;
        }

        private string RestoreControlCmd(string id, Dictionary<string, object> cmd)
        {
            Need("restore_control");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            string kind = UnitKindOfObj(u);
            if (kind != "ship" && kind != "air") return Err(id, "not_applicable", "restore_control is for ships, submarines and aircraft");
            TacticsEnsureMission();
            var detail = new List<string>();

            // 1. the mod's automation flags
            UnitFlags f;
            if (!_flags.TryGetValue(u, out f)) { f = new UnitFlags(); _flags[u] = f; }
            string flagsBefore = FlagsJson(u);
            if (kind == "ship") f.AutoEvade = false; else f.PostLaunch = "off";
            FlagStoreRestoreControl(u, kind);
            string flagsNow = FlagsJson(u);
            detail.Add("flags off");
            if (EventsAvailable)
                EvLine("flags_changed", "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(NameOf(u)) + ",\"flags\":" + flagsNow + "}");

            // 2. an after-launch turn that is still running: give the aircraft its old points back
            PlRun run;
            if (kind == "air" && _plRuns.TryGetValue(u, out run))
            {
                _plRuns.Remove(u);
                try
                {
                    var wps = Waypoints(u);
                    if (wps.Count > 0 && DistanceNm(wps[0][0], wps[0][1], run.Placed[0], run.Placed[1]) <= 0.2)
                    {
                        if (run.Saved.Count == 0) Call(u, "RemoveWaypoints");
                        for (int i = 0; i < run.Saved.Count; i++) PlaceWaypoint("0", u, run.Saved[i][0], run.Saved[i][1], i > 0, "");
                    }
                    if (EventsAvailable) EvLine("post_launch", PlEvent(u, run.Mode, "end", "released", run.Guidance, run.TargetTrack, null, null));
                    detail.Add("after-launch turn ended");
                }
                catch { }
            }

            // 3. Automatic SAR of the workshop mod
            string sar = StopAutomaticSar(u);
            if (sar == "cancelled")
            {
                detail.Add("automatic SAR cancelled");
                try
                {
                    // the mod braked the ship in reverse: stop the engines so it does not keep backing away
                    var tg = kind == "ship" ? Telegraph(u) : null;
                    if (tg.HasValue && tg.Value < 0 && Capabilities.Contains("set_telegraph")) { Call(u, "setTelegraph", 0); detail.Add("telegraph " + tg.Value + "->0"); }
                }
                catch { }
            }
            else if (sar.StartsWith("error:")) detail.Add("automatic SAR " + sar);

            // 3b. interface mod only (T-095, T-109, T-112, T-107): leave the ASW/SAR/CAP groups, drop own evasion; no-op in the voice mod
            ReleaseParticipants(u);

            // 4. ROE and route back to what they were before the mod's first command
            var entry = _ctlMem.Peek(u);
            string curRoe = WeaponStatusName(u);
            string roeAction = ControlMemory.DecideRoe(entry, curRoe, AllowAttack);
            string roeTo = entry != null ? entry.SavedRoe : null;
            if (roeAction == "restore")
            {
                var rc = new Dictionary<string, object> { { "uid", (double)UidOf(u) }, { "status", roeTo }, { "confirm", true } };
                string rr = RoeCmd("0", rc);
                if (!rr.Contains("\"ok\":true")) { roeAction = "error"; }
            }
            detail.Add(roeAction == "restore" ? "roe " + curRoe + "->" + roeTo : "roe " + roeAction);

            var wpsNow = Waypoints(u);
            string routeAction = ControlMemory.DecideRoute(entry, wpsNow.Count > 0 ? wpsNow[0] : null, (a, b) => DistanceNm(a[0], a[1], b[0], b[1]));
            int routePoints = 0;
            if (routeAction == "restore")
            {
                try
                {
                    if (entry.SavedRoute.Count > 0)
                    {
                        for (int i = 0; i < entry.SavedRoute.Count; i++) PlaceWaypoint("0", u, entry.SavedRoute[i][0], entry.SavedRoute[i][1], i > 0, "");
                        routePoints = entry.SavedRoute.Count;
                    }
                    else if (entry.SavedCourseDeg.HasValue)
                    {
                        double lat0, lon0, h, lat, lon;
                        if (OwnPos(u, out lat0, out lon0, out h))
                        {
                            Destination(lat0, lon0, entry.SavedCourseDeg.Value, 75, out lat, out lon);
                            PlaceWaypoint("0", u, lat, lon, false, "");
                            routePoints = 1;
                        }
                        else Call(u, "RemoveWaypoints");
                    }
                    else Call(u, "RemoveWaypoints");
                }
                catch (Exception e) { routeAction = "error"; detail.Add("route " + e.GetType().Name); }
            }
            detail.Add(routeAction == "restore" ? "route restored " + routePoints + " wp" : "route " + routeAction);

            // 5. speed, EMCON and radars (each only if the player has not changed it since)
            string speedAction = RestoreField(u, entry, "telegraph", detail,
                v => TelegraphCmd("0", new Dictionary<string, object> { { "uid", (double)UidOf(u) }, { "telegraph", (double)int.Parse(v) } }));
            string emconAction = RestoreField(u, entry, "emcon", detail, v =>
            {
                CallOpt(u, "setEMCON", v == "true", true);
                return "{\"ok\":true}";
            });
            string airAction = RestoreField(u, entry, "air_radar", detail,
                v => RadarCmd("0", new Dictionary<string, object> { { "uid", (double)UidOf(u) }, { "kind", "air" }, { "on", v == "true" } }));
            string surfAction = RestoreField(u, entry, "surface_radar", detail,
                v => RadarCmd("0", new Dictionary<string, object> { { "uid", (double)UidOf(u) }, { "kind", "surface" }, { "on", v == "true" } }));
            string radarAction = airAction == "restore" || surfAction == "restore" ? "restore"
                : airAction == "player_changed" || surfAction == "player_changed" ? "player_changed" : "nothing_saved";
            _ctlMem.Take(u);

            string summary = null;
            RestoreSummaryRu(roeAction, routeAction, speedAction, emconAction, radarAction, ref summary); // voice mod only: Russian line for the player
            return Ok(id, "\"uid\":" + UidOf(u) + ",\"flags\":" + flagsNow + ",\"previous_flags\":" + flagsBefore +
                ",\"automatic_sar\":" + Json.Str(sar) +
                ",\"roe\":{\"action\":" + Json.Str(roeAction) + ",\"from\":" + Json.Str(curRoe) + ",\"to\":" + Json.Str(roeAction == "restore" ? roeTo : curRoe) + "}" +
                ",\"route\":{\"action\":" + Json.Str(routeAction) + ",\"waypoints\":" + routePoints + "}" +
                ",\"speed\":" + Json.Str(speedAction) + ",\"emcon\":" + Json.Str(emconAction) + ",\"radars\":" + Json.Str(radarAction) +
                (summary != null ? ",\"summary_ru\":" + Json.Str(summary) : "") + ",\"detail\":" + Json.Str(string.Join("; ", detail.ToArray())));
        }
    }
}
