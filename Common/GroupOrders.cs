using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    /// <summary>Pure argument parsing for formation commands (no game types, unit-testable).</summary>
    internal static partial class FormationArgs
    {
        /// <summary>Returns null when ok, else a bad_args detail. scope defaults to "unit".</summary>
        public static string ParseScope(Dictionary<string, object> cmd, out string scope)
        {
            scope = "unit";
            object o;
            if (!cmd.TryGetValue("scope", out o) || o == null) return null;
            var s = o as string;
            if (s != null) s = s.Trim().ToLowerInvariant();
            if (s == "unit" || s == "formation" || s == "all") { scope = s; return null; }
            return "scope: unit|formation|all";
        }

        public static string ToSnake(string enumName)
        {
            if (string.IsNullOrEmpty(enumName)) return enumName;
            var sb = new StringBuilder();
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }

    /// <summary>Reading the player's formations and group orders (scope = unit | formation | all). Formation editing commands are voice-only (VoiceBotBridge/VoiceOnly/Formations.cs).</summary>
    internal sealed partial class GameApi
    {

        /// <summary>Capabilities formation_edit / formation_disband: implemented by the voice mod only (VoiceHooks).</summary>
        partial void SelfCheckFormationEdit(bool readOk);

        private void SelfCheckFormations(bool st)
        {
            const string F = "SeaPower.UnitFormation";
            bool rd = st;
            rd &= Has("formations", "SeaPower.Taskforce", "Formations", false, true);
            rd &= Has("formations", F, "Stations", false, true);
            rd &= Has("formations", F, "LeaderStation", false, true);
            rd &= Has("formations", F, "Pattern", false, true);
            rd &= Has("formations", F, "Spacing", false, true);
            rd &= Has("formations", F, "Name", false, true);
            rd &= Has("formations", F, "SelectedControlMode", false, true);
            rd &= Has("formations", F, "GetDistanceToStation", false, true);
            rd &= Has("formations", "SeaPower.Station", "UnitObject", false, true);
            rd &= Has("formations", "SeaPower.Station", "UnitActsIndependently", false, true);
            rd &= Has("formations", "SeaPower.ObjectBase", "Formation", false, true);
            rd &= Has("formations", "SeaPower.ObjectBase", "FollowingFormation", false, true);
            rd &= Has("formations", "SeaPower.ObjectBase", "IsFormationLeader", false, true);
            if (rd) Capabilities.Add("formations");

            SelfCheckFormationEdit(rd);

            if (rd) Capabilities.Add("group_orders");
        }

        // ---------- reading ----------

        private IEnumerable<object> PlayerFormations()
        {
            var tf = GetStatic("SeaPower.Globals", "_playerTaskforce");
            var list = Get(tf, "Formations") as IEnumerable;
            if (list == null) yield break;
            foreach (var f in new List<object>(Cast(list)))
            {
                if (f == null) continue;
                var dis = Get(f, "_isDisbanded");
                if (dis is bool && (bool)dis) continue;
                yield return f;
            }
        }


        private object LeaderOf(object f) { return Get(Get(f, "LeaderStation"), "UnitObject"); }

        private List<object> MembersOf(object f)
        {
            var res = new List<object>();
            var st = Get(f, "Stations") as IEnumerable;
            if (st == null) return res;
            foreach (var s in Cast(st))
            {
                var u = Get(s, "UnitObject");
                if (u != null && IsAlive(u)) res.Add(u);
            }
            return res;
        }


        private static string KindOfUnit(object u)
        {
            for (var t = u == null ? null : u.GetType(); t != null; t = t.BaseType)
            {
                switch (t.Name)
                {
                    case "Vessel": case "Submarine": return "surface";
                    case "Aircraft": return "air";
                    case "Helicopter": return "helicopter";
                    case "LandUnit": return "land";
                }
            }
            return "surface";
        }

        private bool IsShip(object u) { return KindOfUnit(u) == "surface"; }

        private string FormationRef(object f)
        {
            var l = LeaderOf(f);
            return "{\"name\":" + Json.Str(Get(f, "Name") as string) + ",\"leader_uid\":" + (l == null ? "null" : UidOf(l).ToString()) + "}";
        }

        /// <summary>Formation named by args: formation / leader_uid (leader uid), else the formation of unit uid, else of the selected unit.</summary>
        private object FindFormation(Dictionary<string, object> cmd, out string err)
        {
            err = null;
            if (!InMission()) { err = "not_in_session"; return null; }
            foreach (var key in new[] { "formation", "leader_uid" })
            {
                if (!cmd.ContainsKey(key) || cmd[key] == null) continue;
                var lu = D(cmd[key]);
                if (!lu.HasValue || cmd[key] is string) { err = "bad_args"; return null; }
                foreach (var f in PlayerFormations())
                {
                    var l = LeaderOf(f);
                    if (l != null && UidOf(l) == (int)lu.Value) return f;
                }
                err = "no_formation";
                return null;
            }
            object u;
            if (cmd.ContainsKey("uid") && cmd["uid"] != null)
            {
                u = FindOwnUnit((int)(D(cmd["uid"]) ?? -1));
                if (u == null) { err = "unit_not_found"; return null; }
            }
            else
            {
                string src; u = SelectedUnit(out src);
                if (u == null) { err = "no_selected_unit"; return null; }
            }
            var fm = Get(u, "Formation");
            if (fm == null) err = "no_formation";
            return fm;
        }

        // ---------- group orders: scope = unit | formation | all ----------

        private static readonly Regex ErrRx = new Regex("\"error\":\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex SalvoRx = new Regex("\"salvo_ordered\":(\\d+)", RegexOptions.Compiled);
        private static readonly Regex WeaponRx = new Regex("\"weapon\":(\"[^\"]*\")", RegexOptions.Compiled);

        /// <summary>
        /// Runs a single-unit handler for every unit of the scope. The handler is the unchanged 0.7 code path: it is called
        /// with a copy of the command (scope removed, uid = the member). Movement orders go to the leader only (a follower
        /// that gets a waypoint leaves the formation, game rule MakeUnitActIndependently).
        /// </summary>
        private string Scoped(string id, Dictionary<string, object> cmd, string name, Func<Dictionary<string, object>, bool, string> handler)
        {
            string scope; var e = FormationArgs.ParseScope(cmd, out scope);
            if (e != null) return Err(id, "bad_args", e);
            if (scope == "unit") return handler(cmd, false);
            bool attack = name == "attack" || name == "attack_nearest_hostile";
            if (attack && scope == "all") return Err(id, "bad_args", "attack supports scope unit|formation only");
            if (name == "set_roe" && cmd.ContainsKey("status") && (cmd["status"] as string ?? "").ToLowerInvariant() == "free" && !Confirmed(cmd))
                return Err(id, "confirm_required", "weapons free needs \"confirm\":true");
            if (attack && !Confirmed(cmd)) return Err(id, "confirm_required", "attack needs \"confirm\":true");
            Need("group_orders");
            if (!InMission()) return Err(id, "not_in_session", null);
            bool movement = name == "set_course" || name == "goto" || name == "add_waypoint" || name == "clear_waypoints" || name == "set_telegraph";

            object formation = null;
            var targets = new List<object>();
            var skipped = new List<string>();
            if (scope == "formation")
            {
                string err; formation = FindFormation(cmd, out err);
                if (formation == null) return Err(id, err == "no_formation" ? "no_formation" : err, null);
                var leader = LeaderOf(formation);
                foreach (var m in MembersOf(formation))
                {
                    bool isLeader = ReferenceEquals(m, leader);
                    if (movement && !isLeader) continue; // followers keep their station
                    string why = null;
                    if (!BoolOf(Get(m, "AcceptsOrdersFromPlayer"))) why = "no_control";
                    else if (attack)
                    {
                        if (IsAirUnit(m) && !InFlight(m)) why = "not_airborne";
                        else if (!isLeader && !BoolOf(Get(m, "FollowingFormation"))) why = "independent";
                    }
                    if (why != null) skipped.Add(SkipRow(m, why)); else targets.Add(m);
                }
            }
            else
            {
                foreach (var m in OwnUnits())
                {
                    if (!IsShip(m)) continue;
                    var f = Get(m, "Formation");
                    if (movement && f != null && !ReferenceEquals(LeaderOf(f), m) && BoolOf(Get(m, "FollowingFormation")))
                    { skipped.Add(SkipRow(m, "following")); continue; }
                    if (!BoolOf(Get(m, "AcceptsOrdersFromPlayer"))) { skipped.Add(SkipRow(m, "no_control")); continue; }
                    targets.Add(m);
                }
            }
            if (targets.Count == 0) return Err(id, "no_targets", skipped.Count + " skipped");

            bool fixedTarget = false;
            if (name == "attack_nearest_hostile")
            {
                // one target for the whole group: nearest hostile surface contact to the leader
                var leader = LeaderOf(formation) ?? targets[0];
                double lat0, lon0, h;
                if (!OwnPos(leader, out lat0, out lon0, out h)) return Err(id, "no_position", null);
                List<int> nskip;
                object best = PickNearestHostile(leader, lat0, lon0, out nskip);
                if (best == null) return Err(id, "no_hostile_contact", "no hostile surface contact in the plotting table");
                cmd = new Dictionary<string, object>(cmd) { { "target_id", D(Get(best, "Id")) ?? -1 } };
                fixedTarget = true;
            }
            if (attack && scope == "formation" && !ConfirmedNeutral(cmd) && cmd.ContainsKey("target_id") && cmd["target_id"] != null)
            {
                // T-079: one neutral check by the leader before any member fires (no partial launch)
                var tid = D(cmd["target_id"]);
                var tv = tid.HasValue ? FindContact((int)tid.Value) : null;
                if (tv != null && !IsGone(tv) && Relation(tv) == "hostile")
                {
                    string hj; string ge = NeutralGuardError(id, cmd, LeaderOf(formation) ?? targets[0], tv, out hj);
                    if (ge != null) return ge;
                }
            }

            var fleader = formation == null ? null : LeaderOf(formation);
            var rows = new List<string>();
            int applied = 0; string firstErr = null, firstDetail = null;
            foreach (var m in targets)
            {
                var c = new Dictionary<string, object>(cmd);
                c.Remove("scope"); c.Remove("formation"); c.Remove("leader_uid");
                c["uid"] = (double)UidOf(m);
                string r = handler(c, fixedTarget);
                bool ok = r.StartsWith("{\"type\":\"ack\",\"id\":" + id + ",\"ok\":true");
                string role = formation == null ? "single" : (ReferenceEquals(m, fleader) ? "leader" : "member");
                var row = new StringBuilder("{\"uid\":" + UidOf(m) + ",\"name\":" + Json.Str(RxGet(m, "Name") as string) + ",\"ok\":" + Json.Bool(ok) + ",\"role\":" + Json.Str(role));
                if (ok)
                {
                    applied++;
                    if (attack)
                    {
                        var sm = SalvoRx.Match(r); var wm = WeaponRx.Match(r);
                        if (sm.Success) row.Append(",\"salvo_ordered\":").Append(sm.Groups[1].Value);
                        if (wm.Success) row.Append(",\"weapon\":").Append(wm.Groups[1].Value);
                    }
                }
                else
                {
                    var em = ErrRx.Match(r);
                    string code = em.Success ? em.Groups[1].Value : "error";
                    if (firstErr == null) { firstErr = code; var dm = Regex.Match(r, "\"detail\":\"([^\"]*)\""); firstDetail = dm.Success ? dm.Groups[1].Value : null; }
                    row.Append(",\"error\":").Append(Json.Str(code));
                }
                rows.Add(row.Append('}').ToString());
            }
            return GroupResult(id, scope, formation, rows, skipped, applied, firstErr, firstDetail);
        }

        private string SkipRow(object u, string reason)
        {
            return "{\"uid\":" + UidOf(u) + ",\"name\":" + Json.Str(RxGet(u, "Name") as string) + ",\"reason\":" + Json.Str(reason) + "}";
        }

        private string GroupResult(string id, string scope, object formation, List<string> unitRows, List<string> skipped, int applied, string firstErr, string firstDetail)
        {
            if (applied == 0) return Err(id, firstErr ?? "no_targets", firstDetail);
            return Ok(id, "\"scope\":" + Json.Str(scope) + ",\"formation\":" + (formation == null ? "null" : FormationRef(formation)) +
                ",\"units\":[" + string.Join(",", unitRows.ToArray()) + "],\"applied\":" + applied +
                ",\"skipped\":[" + string.Join(",", skipped.ToArray()) + "]");
        }
    }
}
